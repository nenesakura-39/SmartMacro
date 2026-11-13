using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SmartMacro.Converters;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;
using SmartMacro.Views;
using SmartMacro.Views.EditorPanes;

namespace SmartMacro.AppTests;

public sealed class ConvertersServicesAndPanesTests
{
    private const int WmHotkey = 0x0312;

    [Fact]
    public void Converters_ConvertExpectedValuesAndInvertParameters()
    {
        StaTestRunner.Run(() =>
        {
            var culture = CultureInfo.InvariantCulture;

            var boolToVis = new BoolToVisibilityConverter();
            Assert.Equal(Visibility.Visible, boolToVis.Convert(true, typeof(Visibility), null, culture));
            Assert.Equal(Visibility.Collapsed, boolToVis.Convert(false, typeof(Visibility), null, culture));
            Assert.Equal(Visibility.Collapsed, boolToVis.Convert(true, typeof(Visibility), "Invert", culture));

            var nullToVis = new NullToVisibilityConverter();
            Assert.Equal(Visibility.Visible, nullToVis.Convert("ok", typeof(Visibility), null, culture));
            Assert.Equal(Visibility.Collapsed, nullToVis.Convert("", typeof(Visibility), null, culture));
            Assert.Equal(Visibility.Visible, nullToVis.Convert(null, typeof(Visibility), "Invert", culture));

            var invBool = new InverseBoolConverter();
            Assert.Equal(false, invBool.Convert(true, typeof(bool), null, culture));
            Assert.Equal(true, invBool.Convert(false, typeof(bool), null, culture));

            var countToVis = new CountToVisibilityConverter();
            Assert.Equal(Visibility.Visible, countToVis.Convert(2, typeof(Visibility), null, culture));
            Assert.Equal(Visibility.Collapsed, countToVis.Convert(0, typeof(Visibility), null, culture));
            Assert.Equal(Visibility.Visible, countToVis.Convert(0, typeof(Visibility), "Invert", culture));

            var categoryBrush = new StepCategoryBrushConverter();
            var visionBrush = categoryBrush.Convert(StepKind.FindImage, typeof(Brush), "Accent", culture);
            Assert.IsAssignableFrom<Brush>(visionBrush);
            Assert.NotEqual(Brushes.Gray, visionBrush);

            var filter = new StepTypeFilterConverter();
            var filteredVision = Assert.IsAssignableFrom<IReadOnlyList<StepTypeInfo>>(
                filter.Convert(StepTypeCatalog.Addable, typeof(IReadOnlyList<StepTypeInfo>), "Vision", culture));
            Assert.Contains(filteredVision, s => s.Kind == StepKind.FindImage);
            Assert.DoesNotContain(filteredVision, s => s.Kind == StepKind.MouseClick);

            var match = new StringMatchConverter();
            Assert.Equal(true, match.Convert("WARN", typeof(bool), "WARN", culture));
            Assert.Equal("ERROR", match.ConvertBack(true, typeof(string), "ERROR", culture));

            var pill = new LogPillConverter();
            var warnEntry = new LogEntry(DateTimeOffset.UtcNow, AppLogLevel.Warning, "Careful", null);
            Assert.Equal("WARN", pill.Convert(warnEntry, typeof(string), "Label", culture));
        });
    }

    [Fact]
    public void HotkeyService_RegistersFiresRespectsSuppressionAndPollsRelease()
    {
        StaTestRunner.Run(() =>
        {
            var api = new FakeSystemHotkeyApi();
            var log = new FakeLogService();
            using var service = new HotkeyService(log, api);
            service.AttachHandle(new IntPtr(0x1234));

            var settings = new AppSettings
            {
                RecordHotkey = new HotkeyBinding(KeyModifiers.Control, 0x78), // Ctrl+F9
                PlayHotkey = new HotkeyBinding(KeyModifiers.Control, 0x79),   // Ctrl+F10
            };

            // Simulate PlayHotkey conflict from another process.
            api.RejectBindings.Add((0x4000u | 0x0002u, 0x79u)); // MOD_NOREPEAT | MOD_CONTROL
            var failed = service.Apply(settings);
            Assert.Single(failed);
            Assert.Contains(log.Entries, e => e.Level == AppLogLevel.Warning);

            var trigger = new HotkeyBinding(KeyModifiers.None, 0x75); // F6
            service.ApplyMacroHotkeys([trigger]);

            var pressedActions = new List<HotkeyAction>();
            var releasedBindings = new List<HotkeyBinding>();
            var pressedBindings = new List<HotkeyBinding>();
            service.Pressed += (_, a) => pressedActions.Add(a);
            service.MacroHotkeyPressed += (_, b) => pressedBindings.Add(b);
            service.MacroHotkeyReleased += (_, b) => releasedBindings.Add(b);

            // Global action hotkey
            var handled = service.HandleWindowMessage(WmHotkey, new IntPtr((int)HotkeyAction.ToggleRecording), IntPtr.Zero);
            Assert.True(handled);
            Assert.Equal([HotkeyAction.ToggleRecording], pressedActions);

            // Suppressed mode ignores hotkeys
            HotkeyService.SuppressGlobalHotkeys = true;
            Assert.False(service.HandleWindowMessage(WmHotkey, new IntPtr((int)HotkeyAction.ToggleRecording), IntPtr.Zero));
            HotkeyService.SuppressGlobalHotkeys = false;

            // Macro hotkey hold -> poll -> release when key leaves DownVirtualKeys
            var macroHotkeyId = api.Registered.Single(kv => kv.Value.Vk == (uint)trigger.VirtualKey).Key;
            api.DownVirtualKeys.Add(trigger.VirtualKey);
            Assert.True(service.HandleWindowMessage(WmHotkey, new IntPtr(macroHotkeyId), IntPtr.Zero));
            Assert.Equal([trigger], pressedBindings);

            service.PollHeldHotkeysOnce();
            Assert.Empty(releasedBindings);

            api.DownVirtualKeys.Remove(trigger.VirtualKey);
            service.PollHeldHotkeysOnce();
            Assert.Equal([trigger], releasedBindings);
        });
    }

    [Fact]
    public void ScreenInfoProvider_FiltersCloakedOwnedToolAndOwnProcessWindows()
    {
        const uint ownPid = 999u;
        var winApi = new FakeWindowApi();
        winApi.AddWindow(new FakeWindowApi.FakeWindowEntry(
            new IntPtr(1), "SmartMacro", ownPid, "SmartMacro", new ScreenRect(0, 0, 800, 600)));
        winApi.AddWindow(new FakeWindowApi.FakeWindowEntry(
            new IntPtr(2), "Cloaked App", 2001, "StoreApp", new ScreenRect(10, 10, 300, 300), Cloaked: true));
        winApi.AddWindow(new FakeWindowApi.FakeWindowEntry(
            new IntPtr(3), "Floating Tool", 2002, "Tool", new ScreenRect(20, 20, 200, 200), ToolWindow: true));
        winApi.AddWindow(new FakeWindowApi.FakeWindowEntry(
            new IntPtr(4), "Modal Child", 2003, "Dialog", new ScreenRect(30, 30, 200, 200), HasOwner: true));
        winApi.AddWindow(new FakeWindowApi.FakeWindowEntry(
            new IntPtr(5), "Program Manager", 2004, "explorer", new ScreenRect(0, 0, 1920, 1080)));
        winApi.AddWindow(new FakeWindowApi.FakeWindowEntry(
            new IntPtr(6), "Notes - Notepad", 2005, "notepad", new ScreenRect(100, 120, 900, 700)));

        var provider = new ScreenInfoProvider(winApi, ownPid);
        var windows = provider.GetWindows();

        var single = Assert.Single(windows);
        Assert.Equal("Notes - Notepad", single.Title);
        Assert.Equal("notepad", single.ProcessName);
        Assert.Equal(new ScreenRect(100, 120, 900, 700), single.Bounds);

        winApi.ForegroundWindow = new IntPtr(1);
        Assert.Equal((null, string.Empty), provider.GetForegroundWindowInfo());

        winApi.ForegroundWindow = new IntPtr(6);
        Assert.Equal(("Notes - Notepad", "notepad"), provider.GetForegroundWindowInfo());
    }

    [Fact]
    public void EditorPanesAndEditorView_InstantiateHeadlessWithoutXamlOrBindingErrors()
    {
        StaTestRunner.Run(() =>
        {
            var editorVm = new EditorViewModel(
                new FakeMacroRepository(),
                new FakeLogService(),
                new FakeDialogService(),
                new FakeScreenInfoProvider(),
                () => 250,
                _ => { },
                (_, _) => { },
                () => { });
            editorVm.Load(new Macro
            {
                Name = "UI Pane Smoke",
                Steps = [new DelayStep { DurationMs = 100 }],
            });

            var toolbox = new ToolboxPane { DataContext = editorVm };
            var canvas = new WorkflowCanvasPane { DataContext = editorVm };
            var dock = new BottomDockPane { DataContext = editorVm };
            var palette = new CommandPaletteOverlay { DataContext = editorVm };
            var view = new EditorView { DataContext = editorVm };

            toolbox.Measure(new Size(260, 800));
            canvas.Measure(new Size(640, 800));
            dock.Measure(new Size(800, 240));
            palette.Measure(new Size(800, 600));
            view.Measure(new Size(1280, 800));
            StaTestRunner.DoEvents();

            Assert.Equal(Visibility.Visible, canvas.Visibility);
            Assert.NotNull(canvas.FindName("StepList"));
            Assert.NotNull(view.FindName("WorkflowCanvas"));
            Assert.NotNull(view.FindName("BottomDock"));
            Assert.NotNull(view.FindName("CommandPalette"));
            Assert.Same(editorVm, toolbox.DataContext);
            Assert.Same(editorVm, canvas.DataContext);
            Assert.Same(editorVm, dock.DataContext);
            Assert.Same(editorVm, palette.DataContext);
            Assert.Same(editorVm, view.DataContext);
        });
    }
}
