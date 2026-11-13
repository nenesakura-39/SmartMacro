using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;
using Xunit;

namespace SmartMacro.Tests;

public sealed class Plan10GroupGalleryAuditTests
{
    private static string EncodeSolidPattern(byte r, byte g, byte b, int width = 24, int height = 24)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var border = x < 3 || y < 3 || x >= width - 3 || y >= height - 3;
                pixels[i + 0] = border ? (byte)15 : b;
                pixels[i + 1] = border ? (byte)15 : g;
                pixels[i + 2] = border ? (byte)15 : r;
                pixels[i + 3] = 255;
            }
        }
        return TemplateCodec.Encode(new BgraImage(width, height, pixels));
    }

    private static string EncodePngPattern(byte r, byte g, byte b, int width = 24, int height = 24)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var border = x < 3 || y < 3 || x >= width - 3 || y >= height - 3;
                pixels[i + 0] = border ? (byte)15 : b;
                pixels[i + 1] = border ? (byte)15 : g;
                pixels[i + 2] = border ? (byte)15 : r;
                pixels[i + 3] = 255;
            }
        }
        return Convert.ToBase64String(PngCodec.Encode(new BgraImage(width, height, pixels)));
    }

    [Fact]
    public void BeginAndEndGroup_RoundTripJson_SchemaVersion_AndBlockStructure()
    {
        var bg = new BeginGroupStep { Title = "Buff Routine", ColorHex = "#10B981" };
        var inner = new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Press };
        var eg = new EndGroupStep();

        var macro = new Macro
        {
            Name = "Group Schema Test",
            Steps = [bg, inner, eg],
        };

        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(macro));
        Assert.Equal("📦 Group \"Buff Routine\"", StepDescriber.Describe(bg));
        Assert.Contains(StepCatalog.All, s => s.Kind == StepKind.BeginGroup);
        Assert.Contains(StepCatalog.All, s => s.Kind == StepKind.EndGroup);

        var json = MacroFileFormat.Serialize(macro);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        Assert.NotNull(loaded);

        var loadedBg = Assert.IsType<BeginGroupStep>(loaded!.Steps[0]);
        Assert.Equal("Buff Routine", loadedBg.Title);
        Assert.Equal("#10B981", loadedBg.ColorHex);
        Assert.IsType<EndGroupStep>(loaded.Steps[2]);

        var blocks = BlockStructure.Analyze(loaded.Steps);
        Assert.True(blocks.IsValid);
        Assert.Equal([0, 1, 0], blocks.Depth);
        Assert.Equal(2, blocks.EndOf(0));
        Assert.Equal(0, blocks.StartOf(2));
    }

    [Fact]
    public void BeginGroup_StepListViewAndEditing_Collapse_Rails_Delete_Toggle_AndRepairWork()
    {
        var bg = new BeginGroupStep { Title = "Setup" };
        var d1 = new DelayStep { DurationMs = 100 };
        var d2 = new DelayStep { DurationMs = 200 };
        var eg = new EndGroupStep();
        var after = new KeyPressStep { VirtualKey = 0x42, Action = KeyAction.Press };
        var steps = new List<MacroStep> { bg, d1, d2, eg, after };

        Assert.True(StepListView.CanCollapse(bg));
        Assert.True(StepListView.MatchesFilter(bg, StepFilter.VisionAndLogic));
        Assert.True(StepListView.MatchesFilter(eg, StepFilter.VisionAndLogic));

        var rails = StepListView.EnclosingBlockKinds(steps);
        Assert.Equal([BlockRailKind.None, BlockRailKind.Group, BlockRailKind.Group, BlockRailKind.None, BlockRailKind.None], rails);
        Assert.Equal("#0EA5E9", StepListView.RailColorHex(BlockRailKind.Group));

        var collapsed = new HashSet<Guid> { bg.Id };
        var fold = StepListView.Collapse(steps, collapsed);
        Assert.Equal([false, true, true, true, false], fold.Hidden);
        Assert.Equal(2, fold.HiddenCount[0]);

        // Deleting an expanded BeginGroup removes both BeginGroup & EndGroup while keeping inner steps.
        var deleteExpanded = StepListEditing.PlanDelete(steps, [0]);
        Assert.Equal([0, 3], deleteExpanded.Indexes);

        // Deleting a collapsed BeginGroup removes the whole collapsed Group section [0..3].
        var deleteCollapsed = StepListEditing.PlanDelete(steps, [0], collapsed);
        Assert.Equal([0, 1, 2, 3], deleteCollapsed.Indexes);

        // Toggle enabled targets all steps inside the Group.
        var toggleTargets = StepListEditing.ToggleEnabledTargets(steps, [0]);
        Assert.Equal([0, 1, 2, 3], toggleTargets);

        // Repair fixes unclosed BeginGroup and orphan EndGroup.
        var broken = new List<MacroStep> { new BeginGroupStep { Title = "Open" }, new DelayStep { DurationMs = 50 }, new EndGroupStep(), new EndGroupStep() };
        var repaired = StepCleanup.RepairBlockStructure(broken);
        Assert.True(repaired.Changed);
        Assert.True(BlockStructure.Analyze(broken).IsValid);
    }

    [Fact]
    public async Task PlaybackEngine_GroupExecutesZeroOverhead_SkipsDisabledGroup_AndSupportsSingleStepAndSlice()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);

        var k1 = new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Press };
        var disabledBg = new BeginGroupStep { Title = "Skipped", Enabled = false };
        var kSkipped = new KeyPressStep { VirtualKey = 0x42, Action = KeyAction.Press };
        var eg1 = new EndGroupStep();
        var enabledBg = new BeginGroupStep { Title = "Active", Enabled = true };
        var k2 = new KeyPressStep { VirtualKey = 0x43, Action = KeyAction.Press };
        var k3 = new KeyPressStep { VirtualKey = 0x44, Action = KeyAction.Press };
        var eg2 = new EndGroupStep();

        var macro = new Macro
        {
            Name = "Group Playback",
            Steps = [k1, disabledBg, kSkipped, eg1, enabledBg, k2, k3, eg2],
        };

        var stats = await engine.RunAsync(macro);
        Assert.Equal(3, stats.StepsExecuted);
        Assert.Equal(
            [
                "key 65 down", "key 65 up",
                "key 67 down", "key 67 up",
                "key 68 down", "key 68 up",
            ],
            input.Calls);

        // SingleStep on BeginGroup or EndGroup spans the whole Group section.
        Assert.Equal((4, 8), PlaybackRanges.SingleStep(macro.Steps, 4));
        Assert.Equal((4, 8), PlaybackRanges.SingleStep(macro.Steps, 7));

        // PlayFromHere starting inside enabledBg drops the enclosing EndGroup so the slice is balanced.
        var slice = PlaybackRanges.BuildPlayFromHereSlice(macro.Steps, 6);
        Assert.True(BlockStructure.Analyze(slice.Steps).IsValid);
        Assert.DoesNotContain(slice.Steps, s => s is BeginGroupStep or EndGroupStep);
        Assert.Single(slice.Steps);
    }

    [Fact]
    public void MacroValidator_FlagsEmptyGroupAndDisabledEndGroup()
    {
        var macro = new Macro
        {
            Name = "Group Validation",
            Steps =
            [
                new BeginGroupStep { Title = new string('G', BeginGroupStep.MaxTitleLength + 10) },
                new EndGroupStep { Enabled = false },
            ],
        };

        var issues = MacroValidator.Validate(macro);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("no enabled steps inside", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("turn off its opening step", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Property == nameof(BeginGroupStep.Title));
    }

    [Fact]
    public void MacroTemplateGallery_CollectsAndDeduplicatesFromStepsConditionsSnapshotsAndAssets()
    {
        var imgAData = EncodeSolidPattern(220, 40, 40);
        var imgBData = EncodeSolidPattern(40, 200, 80);
        var clickPng = EncodePngPattern(40, 90, 230);
        var assetKey = "asset_red_1";

        var step1 = new FindImageStep { TemplateAssetId = assetKey, TemplateWidth = 24, TemplateHeight = 24 };
        var step2 = new FindImageStep
        {
            TemplateData = imgAData, // Duplicate of step1 asset -> deduplicated
            TemplateWidth = 24,
            TemplateHeight = 24,
            AdditionalTemplates = [new TemplateVariant { Data = imgBData, Width = 24, Height = 24 }],
        };
        var clickStep = new MouseClickStep
        {
            TargetThumbnailBase64 = clickPng,
        };
        var ifStep = new IfStep
        {
            Condition = new SmartMacro.Core.Models.Condition
            {
                Check = new FindImageStep { TemplateData = EncodeSolidPattern(200, 180, 30), TemplateWidth = 24, TemplateHeight = 24 },
            },
        };

        var macro = new Macro
        {
            Name = "Gallery",
            Assets = new Dictionary<string, string> { [assetKey] = imgAData },
            Steps = [step1, step2, clickStep, ifStep, new EndIfStep()],
        };

        var all = MacroTemplateGallery.Collect(macro);
        Assert.Equal(4, all.Count);
        Assert.All(all, c => Assert.False(string.IsNullOrEmpty(c.PreviewPngBase64)));

        // Excluding step1 removes step1's primary template unless another step shares it, OR excluding clickStep drops the snapshot.
        var withoutClick = MacroTemplateGallery.Collect(macro, excludeStepId: clickStep.Id);
        Assert.Equal(3, withoutClick.Count);
        Assert.DoesNotContain(withoutClick, c => c.SourceDetail.Contains("Click snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryAuditService_DetectsHealthyAndUnhealthyConditionsAndWindowScopedCollisions()
    {
        var targetSub = new Macro
        {
            Name = "Worker",
            Steps = [new DelayStep { DurationMs = 50 }],
        };
        var cleanCaller = new Macro
        {
            Name = "Caller",
            Steps =
            [
                new BeginGroupStep { Title = "Run" },
                new CallMacroStep { TargetMacroId = targetSub.Id, TargetMacroName = targetSub.Name },
                new ScreenshotStep(),
                new EndGroupStep(),
            ],
            Triggers =
            [
                new MacroTrigger
                {
                    Enabled = true,
                    Kind = TriggerKind.Hotkey,
                    Name = "Notepad Bind",
                    Hotkey = new HotkeyBinding(KeyModifiers.Alt, 0x31),
                    OnlyWhenWindowActive = true,
                    WindowTitle = "Notepad",
                },
            ],
        };
        var disjointWindowMacro = new Macro
        {
            Name = "Calc Macro",
            Steps = [new DelayStep { DurationMs = 10 }],
            Triggers =
            [
                new MacroTrigger
                {
                    Enabled = true,
                    Kind = TriggerKind.Hotkey,
                    Name = "Calc Bind",
                    Hotkey = new HotkeyBinding(KeyModifiers.Alt, 0x31),
                    OnlyWhenWindowActive = true,
                    WindowTitle = "Calculator",
                },
            ],
        };

        var healthy = LibraryAuditService.Audit([targetSub, cleanCaller, disjointWindowMacro], isOcrAvailable: true);
        Assert.True(healthy.IsHealthy);
        Assert.Empty(healthy.Entries);

        var broken = new Macro
        {
            Name = "Worker", // Duplicate name with targetSub
            Steps =
            [
                new FindTextStep { Text = "Go" },
                new FindImageStep { TemplateAssetId = "missing_asset_99" },
                new CallMacroStep { TargetMacroId = Guid.NewGuid(), TargetMacroName = "Ghost" },
                new ScreenshotStep { FolderPath = @"C:\Users\gamer\Pictures\CustomShots" },
            ],
            Triggers =
            [
                new MacroTrigger
                {
                    Enabled = true,
                    Kind = TriggerKind.Hotkey,
                    Name = "Global Clash",
                    Hotkey = new HotkeyBinding(KeyModifiers.Alt, 0x31), // Global overlaps Notepad bind AND AppSettings PlayHotkey
                    OnlyWhenWindowActive = false,
                },
            ],
        };
        var settings = new AppSettings
        {
            PlayHotkey = new HotkeyBinding(KeyModifiers.Alt, 0x31),
        };

        var report = LibraryAuditService.Audit([targetSub, cleanCaller, broken], settings, isOcrAvailable: false);
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Entries, e => e.Severity == LibraryAuditSeverity.Warning && e.Category == "Naming");
        Assert.Contains(report.Entries, e => e.Severity == LibraryAuditSeverity.Error && e.Category == "Assets");
        Assert.Contains(report.Entries, e => e.Severity == LibraryAuditSeverity.Error && e.Category == "Sub-Macro");
        Assert.Contains(report.Entries, e => e.Severity == LibraryAuditSeverity.Warning && e.Category == "Environment");
        Assert.Contains(report.Entries, e => e.Severity == LibraryAuditSeverity.Info && e.Category == "Portability");
        Assert.Contains(report.Entries, e => e.Severity == LibraryAuditSeverity.Warning && e.Category == "Hotkey");
    }
}
