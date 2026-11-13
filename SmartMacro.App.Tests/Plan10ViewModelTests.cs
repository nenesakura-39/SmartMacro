using System.Windows;
using System.Windows.Media;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.AppTests;

public sealed class Plan10ViewModelTests
{
    private static (EditorViewModel Vm, FakeMacroRepository Repo, FakeDialogService Dialogs, List<Macro> SavedCallbacks)
        CreateEditorHarness(List<Macro>? libraryStore = null)
    {
        var repo = new FakeMacroRepository();
        var log = new FakeLogService();
        var dialogs = new FakeDialogService();
        var screen = new FakeScreenInfoProvider();
        var saved = new List<Macro>();
        var vm = new EditorViewModel(
            repo,
            log,
            dialogs,
            screen,
            () => 250,
            m =>
            {
                saved.Add(m);
                if (libraryStore is not null)
                {
                    var idx = libraryStore.FindIndex(x => x.Id == m.Id);
                    if (idx >= 0) libraryStore[idx] = m;
                    else libraryStore.Add(m);
                }
            },
            (_, _) => { },
            () => { })
        {
            GetLibraryMacros = () => libraryStore ?? (IReadOnlyList<Macro>)saved,
        };
        return (vm, repo, dialogs, saved);
    }

    private static string EncodeTestTemplate(byte r, byte g, byte b, int w = 24, int h = 24)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var border = x < 4 || y < 4 || x >= w - 4 || y >= h - 4;
                pixels[i + 0] = border ? (byte)20 : b;
                pixels[i + 1] = border ? (byte)20 : g;
                pixels[i + 2] = border ? (byte)20 : r;
                pixels[i + 3] = 255;
            }
        }
        return TemplateCodec.Encode(new BgraImage(w, h, pixels));
    }

    [Fact]
    public void Editor_WrapInGroup_CustomColor_AndCollapse_UpdateCanvasVisualsCleanly()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _) = CreateEditorHarness();
            var macro = new Macro
            {
                Name = "Dungeon Flow",
                Steps =
                [
                    new DelayStep { DurationMs = 100 },
                    new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Press },
                    new DelayStep { DurationMs = 200 },
                ],
            };
            vm.Load(macro);

            vm.SelectedStep = vm.Steps[0];
            vm.SetSelection([vm.Steps[0], vm.Steps[1]]);
            Assert.True(vm.WrapInGroupCommand.CanExecute(null));
            vm.WrapInGroupCommand.Execute(null);

            Assert.Equal(5, vm.Steps.Count);
            var groupVm = Assert.IsType<BeginGroupStepViewModel>(vm.Steps[0]);
            Assert.IsType<BlockMarkerStepViewModel>(vm.Steps[3]);
            Assert.Equal(1, vm.Steps[1].IndentLevel);
            Assert.Equal(BlockRailKind.Group, vm.Steps[1].BlockRailKind);

            groupVm.GroupTitle = "Pull & Buff";
            groupVm.ColorHex = "#10B981"; // Emerald

            Assert.Equal("Pull & Buff", groupVm.PrimaryBadge);
            Assert.Equal("#10B981", vm.Steps[1].BlockRailColorHex);
            var railBrush = Assert.IsType<SolidColorBrush>(vm.Steps[1].BlockRailBrush);
            Assert.Equal(Color.FromRgb(0x10, 0xB9, 0x81), railBrush.Color);
            var headerAccent = Assert.IsType<SolidColorBrush>(groupVm.BlockAccentBrush);
            Assert.Equal(Color.FromRgb(0x10, 0xB9, 0x81), headerAccent.Color);

            Assert.True(groupVm.CanCollapse);
            vm.SelectedStep = groupVm;
            vm.ToggleCollapseCommand.Execute(groupVm);
            Assert.True(groupVm.IsCollapsed);
            Assert.True(vm.Steps[1].IsHiddenByCollapse);
            Assert.True(vm.Steps[3].IsHiddenByCollapse);
            Assert.False(vm.Steps[4].IsHiddenByCollapse);
        });
    }

    [Fact]
    public async Task Editor_ExtractGroupToSubMacro_SavesSubMacro_ReplacesWithCallMacro_AndUndoRestores()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var library = new List<Macro>();
            var (vm, repo, _, saved) = CreateEditorHarness(library);
            var bg = new BeginGroupStep { Title = "Login Sequence", ColorHex = "#EC4899" };
            var inner1 = new KeyPressStep { VirtualKey = 0x42, Action = KeyAction.Press };
            var inner2 = new DelayStep { DurationMs = 150 };
            var eg = new EndGroupStep();
            var tail = new DelayStep { DurationMs = 50 };

            var macro = new Macro
            {
                Name = "Daily Automation",
                Steps = [bg, inner1, inner2, eg, tail],
            };
            library.Add(macro);
            vm.Load(macro);

            // Selecting an inner step finds the enclosing Group automatically.
            vm.SelectedStep = vm.Steps[1];
            Assert.True(vm.ExtractGroupToSubMacroCommand.CanExecute(null));

            await TestCommandHelper.RunAsync(vm.ExtractGroupToSubMacroCommand);

            Assert.Equal(2, vm.Steps.Count);
            var callVm = Assert.IsType<CallMacroStepViewModel>(vm.Steps[0]);
            Assert.Equal("Login Sequence", callVm.TargetMacroName);
            Assert.IsType<DelayStep>(vm.Steps[1].Model);

            var extracted = repo.FindStored(((CallMacroStep)callVm.Model).TargetMacroId);
            Assert.NotNull(extracted);
            Assert.Equal("Login Sequence", extracted!.Name);
            Assert.Equal(2, extracted.Steps.Count);
            Assert.Contains(saved, m => m.Id == extracted.Id);

            // Ctrl+Z Undo restores the original Group + EndGroup + inner steps in the canvas.
            Assert.True(vm.UndoCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);
            Assert.Equal(5, vm.Steps.Count);
            Assert.IsType<BeginGroupStep>(vm.Steps[0].Model);
            Assert.IsType<EndGroupStep>(vm.Steps[3].Model);
        });
    }

    [Fact]
    public void Editor_FindImageSharedTemplates_ListsAndAppliesOtherStepImage_AndRefreshesIfCondition()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _) = CreateEditorHarness();
            var buttonTemplate = EncodeTestTemplate(220, 60, 40, 28, 28);
            var step1 = new FindImageStep
            {
                TemplateData = buttonTemplate,
                TemplateWidth = 28,
                TemplateHeight = 28,
            };
            var step2 = new FindImageStep
            {
                TemplateAssetId = "stale-asset-id",
            };
            var ifStep = new IfStep
            {
                Condition = new SmartMacro.Core.Models.Condition
                {
                    Check = new FindImageStep(),
                },
            };

            vm.Load(new Macro
            {
                Name = "Shared Gallery Test",
                Steps = [step1, step2, ifStep, new EndIfStep()],
            });

            vm.SelectedStep = vm.Steps[1];
            var targetVm = Assert.IsType<FindImageStepViewModel>(vm.SelectedStep);
            Assert.True(targetVm.HasSharedTemplates);
            Assert.Single(targetVm.SharedTemplates);
            Assert.Equal(28, targetVm.SharedTemplates[0].TemplateWidth);

            targetVm.ApplySharedTemplateCommand.Execute(targetVm.SharedTemplates[0]);
            var step2Model = Assert.IsType<FindImageStep>(targetVm.Model);
            Assert.True(targetVm.HasTemplate);
            Assert.Equal(buttonTemplate, step2Model.TemplateData);
            Assert.Null(step2Model.TemplateAssetId);
            Assert.NotNull(targetVm.TemplatePreview);

            // Condition-embedded FindImageStepViewModel also gets populated when switching selection.
            vm.SelectedStep = vm.Steps[2];
            var ifVm = Assert.IsType<IfStepViewModel>(vm.SelectedStep);
            var condFindVm = Assert.IsType<FindImageStepViewModel>(ifVm.Condition.CheckEditor);
            Assert.True(condFindVm.HasSharedTemplates);
            Assert.True(condFindVm.SharedTemplates.Count >= 1);
        });
    }

    [Fact]
    public async Task LibraryViewModel_AuditLibrary_FindsIssues_OpensMacro_AndClosesDrawer()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var repo = new FakeMacroRepository();
            var log = new FakeLogService();
            var dialogs = new FakeDialogService();
            var opened = new List<Guid>();

            var badMacro = new Macro
            {
                Name = "Broken Link Macro",
                Steps =
                [
                    new CallMacroStep { TargetMacroId = Guid.NewGuid(), TargetMacroName = "MissingSub" },
                ],
                Triggers =
                [
                    new MacroTrigger
                    {
                        Enabled = true,
                        Kind = TriggerKind.Hotkey,
                        Name = "Start Hotkey",
                        Hotkey = new HotkeyBinding(KeyModifiers.Control, 0x78),
                    },
                ],
            };
            var settings = new AppSettings
            {
                RecordHotkey = new HotkeyBinding(KeyModifiers.Control, 0x78),
            };
            repo.Seed(badMacro);

            var libraryVm = new LibraryViewModel(
                repo,
                log,
                dialogs,
                () => false,
                id =>
                {
                    opened.Add(id);
                    return Task.CompletedTask;
                },
                _ => { },
                (_, _) => { },
                getSettings: () => settings,
                isOcrAvailable: () => false);

            await libraryVm.LoadAsync();
            Assert.False(libraryVm.IsAuditOpen);

            libraryVm.AuditLibraryCommand.Execute(null);

            Assert.True(libraryVm.IsAuditOpen);
            Assert.True(libraryVm.HasAuditEntries);
            Assert.False(libraryVm.IsAuditHealthy);
            Assert.Contains(libraryVm.AuditEntries, e => e.Category == "Sub-Macro" && e.Severity == LibraryAuditSeverity.Error);
            Assert.Contains(libraryVm.AuditEntries, e => e.Category == "Hotkey" && e.Severity == LibraryAuditSeverity.Warning);

            await TestCommandHelper.RunAsync(libraryVm.OpenAuditEntryCommand, libraryVm.AuditEntries[0]);
            Assert.Equal(badMacro.Id, Assert.Single(opened));

            libraryVm.CloseAuditCommand.Execute(null);
            Assert.False(libraryVm.IsAuditOpen);
        });
    }

    [Fact]
    public async Task Plan10_ViewsAndTemplates_InstantiateAndMeasureCleanlyInWpf()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            if (Application.Current is null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            if (!Application.Current!.Resources.Contains("BoolToVisibility"))
            {
                var res = Application.Current.Resources;
                res.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SmartMacro;component/Themes/Colors.xaml", UriKind.Absolute) });
                res.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SmartMacro;component/Themes/Tokens.xaml", UriKind.Absolute) });
                res.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SmartMacro;component/Themes/Icons.xaml", UriKind.Absolute) });
                res.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SmartMacro;component/Themes/Controls.xaml", UriKind.Absolute) });
                res.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SmartMacro;component/Themes/Brand.xaml", UriKind.Absolute) });
                res["BoolToVisibility"] = new SmartMacro.Converters.BoolToVisibilityConverter();
                res["InverseBoolToVisibility"] = new SmartMacro.Converters.InverseBoolToVisibilityConverter();
                res["NullToVisibility"] = new SmartMacro.Converters.NullToVisibilityConverter();
                res["InverseBool"] = new SmartMacro.Converters.InverseBoolConverter();
                res["PageToBool"] = new SmartMacro.Converters.PageToBoolConverter();
                res["LevelToBrush"] = new SmartMacro.Converters.LevelToBrushConverter();
                res["LevelToGlyph"] = new SmartMacro.Converters.LevelToGlyphConverter();
            }

            var logicDict = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SmartMacro;component/Views/StepEditors/LogicStepEditors.xaml", UriKind.Absolute),
            };
            var groupTemplate = Assert.IsType<DataTemplate>(logicDict[new DataTemplateKey(typeof(BeginGroupStepViewModel))]);
            Assert.NotNull(groupTemplate.LoadContent());

            var (editorVm, _, _, _) = CreateEditorHarness();
            var tpl = EncodeTestTemplate(200, 80, 40, 24, 24);
            editorVm.Load(new Macro
            {
                Name = "Plan10 Visual Measure",
                Steps =
                [
                    new BeginGroupStep { Title = "Setup", ColorHex = "#10B981" },
                    new FindImageStep { TemplateData = tpl, TemplateWidth = 24, TemplateHeight = 24 },
                    new FindImageStep(),
                    new EndGroupStep(),
                ],
            });
            editorVm.SelectedStep = editorVm.Steps[0];
            var editorView = new SmartMacro.Views.EditorView { DataContext = editorVm };
            editorView.Measure(new Size(1280, 800));
            StaTestRunner.DoEvents();
            editorVm.SelectedStep = editorVm.Steps[2];
            editorView.Measure(new Size(1280, 800));
            StaTestRunner.DoEvents();

            var repo = new FakeMacroRepository();
            repo.Seed(new Macro
            {
                Name = "Broken Sub",
                Steps = [new CallMacroStep { TargetMacroId = Guid.NewGuid(), TargetMacroName = "Missing" }],
            });
            var libraryVm = new LibraryViewModel(
                repo,
                new FakeLogService(),
                new FakeDialogService(),
                () => false,
                _ => Task.CompletedTask,
                _ => { },
                (_, _) => { });
            await libraryVm.LoadAsync();
            libraryVm.AuditLibraryCommand.Execute(null);

            var libraryView = new SmartMacro.Views.LibraryView { DataContext = libraryVm };
            libraryView.Measure(new Size(1200, 800));
            StaTestRunner.DoEvents();
            Assert.True(libraryVm.IsAuditOpen);
        });
    }
}
