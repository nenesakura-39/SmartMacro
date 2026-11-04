using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;
using Xunit;

namespace SmartMacro.Tests;

public sealed class BedrockAndReflexTests
{
    // ---------------- v1.3 Bedrock: StepCatalog & CrashReporter ----------------

    [Fact]
    public void StepCatalog_CoversEveryStepKind_WithValidMetadata()
    {
        var allKinds = Enum.GetValues<StepKind>();
        Assert.Equal(allKinds.Length, StepCatalog.All.Count);

        foreach (var kind in allKinds)
        {
            var desc = StepCatalog.Get(kind);
            Assert.Equal(kind, desc.Kind);
            Assert.False(string.IsNullOrWhiteSpace(desc.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(desc.Glyph));
            Assert.False(string.IsNullOrWhiteSpace(desc.Description));
        }

        // InputSequence is created by recording, not added as a blank tile from the toolbox.
        var addable = StepCatalog.All.Where(s => s.IsAddableFromToolbox).ToList();
        Assert.DoesNotContain(addable, s => s.Kind == StepKind.InputSequence);
    }

    [Fact]
    public void CrashReporter_WritesReport_GetsLatestSummary_AndPrunesOldReports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartMacro_CrashTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < CrashReporter.MaxRetainedReports + 4; i++)
            {
                var ex = new InvalidOperationException($"Simulated crash #{i}");
                var path = CrashReporter.WriteReport(
                    tempDir,
                    "UnitTest",
                    ex,
                    "1.4.0",
                    new[] { new LogEntry(DateTimeOffset.UtcNow, AppLogLevel.Error, $"Log line {i}") });
                Assert.NotNull(path);
                Assert.True(File.Exists(path!));
            }

            var crashDir = Path.Combine(tempDir, CrashReporter.CrashesSubfolder);
            var files = Directory.GetFiles(crashDir, "crash-*.log");
            Assert.Equal(CrashReporter.MaxRetainedReports, files.Length);

            var summary = CrashReporter.GetLatestCrashSummary(tempDir);
            Assert.NotNull(summary);
            Assert.Contains("Latest crash (", summary!);
            Assert.Contains("InvalidOperationException", summary!);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------------- v1.4 Reflex: Schema 17, Validation & Storage ----------------

    [Fact]
    public void SchemaVersion_Is17OnlyWhenMacroHasTriggers()
    {
        var plain = new Macro
        {
            Name = "Plain",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 100 } },
        };
        Assert.True(MacroDocument.RequiredSchemaVersion(plain) < 17);

        plain.Triggers = new List<MacroTrigger>
        {
            new()
            {
                Name = "Quick Start",
                Kind = TriggerKind.Hotkey,
                Hotkey = new HotkeyBinding(KeyModifiers.Control | KeyModifiers.Shift, 0x75), // F6
            },
        };
        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(plain));
        Assert.Contains("Ctrl+Shift+F6", plain.TriggerSummary);
    }

    [Fact]
    public void Validator_CatchesInvalidTriggers_AndDuplicateHotkeys()
    {
        var macro = new Macro
        {
            Name = "Trigger Validation",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 50 } },
            Triggers = new List<MacroTrigger>
            {
                new() { Name = "Bad Hotkey", Kind = TriggerKind.Hotkey, Hotkey = new HotkeyBinding(KeyModifiers.None, 0) },
                new() { Name = "Bad Daily", Kind = TriggerKind.Schedule, Recurrence = ScheduleRecurrence.DailyAtTime, TimeOfDay = "25:99" },
                new() { Name = "Bad Window", Kind = TriggerKind.WindowEvent, WindowTitle = "   " },
            },
        };

        var issues = MacroValidator.Validate(macro);
        Assert.Equal(3, issues.Count(i => i.Severity == IssueSeverity.Error));

        // Duplicate hotkey on two enabled triggers produces a warning.
        var hk = new HotkeyBinding(KeyModifiers.Control, 0x76); // Ctrl+F7
        macro.Triggers = new List<MacroTrigger>
        {
            new() { Name = "T1", Kind = TriggerKind.Hotkey, Hotkey = hk },
            new() { Name = "T2", Kind = TriggerKind.Hotkey, Hotkey = hk },
        };
        var dupIssues = MacroValidator.Validate(macro);
        Assert.Contains(dupIssues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("used more than once on this macro"));
    }

    [Fact]
    public void MacroFileFormat_RoundTripsTriggers_AndRenewsTriggerIdsOnImport()
    {
        var triggerId = Guid.NewGuid();
        var macro = new Macro
        {
            Name = "Trigger Bundle",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 100 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Id = triggerId,
                    Name = "Hold F7",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = new HotkeyBinding(KeyModifiers.None, 0x76),
                    HotkeyMode = HotkeyTriggerMode.HoldToRun,
                    ConflictPolicy = TriggerConflictPolicy.StopCurrentAndRun,
                },
            },
        };

        var json = MacroFileFormat.Serialize(macro);
        Assert.True(MacroFileFormat.TryParse(json, out var parsed, out var err), err);
        var imported = MacroFileFormat.PrepareForImport(parsed!, Array.Empty<string>());

        Assert.NotNull(imported.Triggers);
        Assert.Single(imported.Triggers!);
        Assert.NotEqual(triggerId, imported.Triggers![0].Id);
        Assert.Equal(HotkeyTriggerMode.HoldToRun, imported.Triggers[0].HotkeyMode);
        Assert.Equal(TriggerConflictPolicy.StopCurrentAndRun, imported.Triggers[0].ConflictPolicy);
    }

    // ---------------- v1.4 Reflex: TriggerEvaluator & Run Governor ----------------

    [Fact]
    public void TriggerEvaluator_HotkeyModes_PressOnce_Toggle_HoldToRun_RepeatWhileHeld()
    {
        var hk = new HotkeyBinding(KeyModifiers.Control, 0x75); // Ctrl+F6
        var trigger = new MacroTrigger
        {
            Name = "Hotkey",
            Kind = TriggerKind.Hotkey,
            Hotkey = hk,
            HotkeyMode = HotkeyTriggerMode.HoldToRun,
        };
        var macro = new Macro
        {
            Name = "Hold Macro",
            RepeatCount = 3,
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 100 } },
            Triggers = new List<MacroTrigger> { trigger },
        };
        var macros = new[] { macro };
        var evaluator = new TriggerEvaluator();
        var now = DateTimeOffset.UtcNow;

        // 1. HoldToRun: key-down starts, repeated key-down while held is ignored, key-up stops immediately.
        var down1 = evaluator.EvaluateHotkeyDown(macros, hk, "Notepad", currentlyPlayingMacroId: null, now);
        Assert.Equal(TriggerDecisionKind.StartMacro, down1.Kind);
        Assert.Equal(3, down1.Macro!.RepeatCount);

        var downRepeat = evaluator.EvaluateHotkeyDown(macros, hk, "Notepad", currentlyPlayingMacroId: macro.Id, now.AddMilliseconds(50));
        Assert.Equal(TriggerDecisionKind.None, downRepeat.Kind);

        var up1 = evaluator.EvaluateHotkeyUp(hk, currentlyPlayingMacroId: macro.Id);
        Assert.Equal(TriggerDecisionKind.StopPlaying, up1.Kind);

        // 2. RepeatWhileHeld: forces RepeatCount = 0 on the played copy and stops on key-up.
        trigger.HotkeyMode = HotkeyTriggerMode.RepeatWhileHeld;
        var downLoop = evaluator.EvaluateHotkeyDown(macros, hk, "Notepad", currentlyPlayingMacroId: null, now.AddSeconds(1));
        Assert.Equal(TriggerDecisionKind.StartMacro, downLoop.Kind);
        Assert.Equal(0, downLoop.Macro!.RepeatCount);
        Assert.Equal(3, macro.RepeatCount); // original untouched

        var upLoop = evaluator.EvaluateHotkeyUp(hk, currentlyPlayingMacroId: macro.Id);
        Assert.Equal(TriggerDecisionKind.StopPlaying, upLoop.Kind);

        // 3. Toggle: first press starts, key-up does nothing, second press stops.
        trigger.HotkeyMode = HotkeyTriggerMode.Toggle;
        var toggleOn = evaluator.EvaluateHotkeyDown(macros, hk, "Notepad", currentlyPlayingMacroId: null, now.AddSeconds(2));
        Assert.Equal(TriggerDecisionKind.StartMacro, toggleOn.Kind);
        var toggleUp = evaluator.EvaluateHotkeyUp(hk, currentlyPlayingMacroId: macro.Id);
        Assert.Equal(TriggerDecisionKind.None, toggleUp.Kind);
        var toggleOff = evaluator.EvaluateHotkeyDown(macros, hk, "Notepad", currentlyPlayingMacroId: macro.Id, now.AddSeconds(3));
        Assert.Equal(TriggerDecisionKind.StopPlaying, toggleOff.Kind);
    }

    [Fact]
    public void TriggerEvaluator_Schedule_And_WindowEvents_FireOnTransitions()
    {
        var startupMacro = new Macro
        {
            Name = "Startup",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 50 } },
            Triggers = new List<MacroTrigger>
            {
                new() { Name = "Boot", Kind = TriggerKind.Schedule, Recurrence = ScheduleRecurrence.OnAppStartup },
            },
        };
        var windowMacro = new Macro
        {
            Name = "Excel Helper",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 50 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "On Excel Focus",
                    Kind = TriggerKind.WindowEvent,
                    WindowEvent = WindowTriggerEvent.WindowFocused,
                    WindowTitle = "Excel",
                    ConflictPolicy = TriggerConflictPolicy.QueueNext,
                },
            },
        };

        var macros = new[] { startupMacro, windowMacro };
        var evaluator = new TriggerEvaluator();
        var t0 = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

        // Tick 1: OnAppStartup fires once; Excel is not focused yet.
        var d1 = evaluator.EvaluateTick(macros, t0, new[] { "Desktop" }, "Desktop", currentlyPlayingMacroId: null);
        Assert.Single(d1);
        Assert.Equal(startupMacro.Id, d1[0].Macro!.Id);

        // Tick 2: Excel becomes focused while Startup is still running -> ConflictPolicy.QueueNext queues Excel Helper!
        var d2 = evaluator.EvaluateTick(macros, t0.AddSeconds(1), new[] { "Book1 - Excel" }, "Book1 - Excel", currentlyPlayingMacroId: startupMacro.Id);
        Assert.Single(d2);
        Assert.Equal(TriggerDecisionKind.QueueMacro, d2[0].Kind);
        Assert.Equal(1, evaluator.QueuedCount);

        // Dequeue after Startup finishes:
        var next = evaluator.DequeueNext();
        Assert.Equal(TriggerDecisionKind.StartMacro, next.Kind);
        Assert.Equal(windowMacro.Id, next.Macro!.Id);
        Assert.Equal(0, evaluator.QueuedCount);
    }

    // ---------------- v1.5 Context: ProfileRouter & Auto-Switching ----------------

    [Fact]
    public void ProfileRouter_ResolvesActiveProfile_And_FiltersArmedMacros()
    {
        var globalMacro = new Macro { Name = "Global Macro", ProfileName = null };
        var excelMacro = new Macro { Name = "Excel Macro", ProfileName = "Excel" };
        var gameMacro = new Macro { Name = "Elden Ring Macro", ProfileName = "Elden Ring", TargetWindowTitle = "ELDEN RING" };
        var workMacro = new Macro { Name = "Work Macro", ProfileName = "Work" };
        var library = new[] { globalMacro, excelMacro, gameMacro, workMacro };

        var rules = new[]
        {
            new MacroProfileRule { ProfileName = "Work", WindowTitleMatch = "Google Docs", Enabled = true },
        };

        // 1. Explicit rule match ("Google Docs" -> "Work")
        var resolvedWork = ProfileRouter.ResolveActiveProfile(rules, library, "Budget - Google Docs", manualProfileFilter: null, autoSwitchByWindow: true);
        Assert.Equal("Work", resolvedWork);

        // 2. Direct ProfileName substring match ("ELDEN RING™" -> "Elden Ring")
        var resolvedGame = ProfileRouter.ResolveActiveProfile(rules, library, "ELDEN RING™", manualProfileFilter: null, autoSwitchByWindow: true);
        Assert.Equal("Elden Ring", resolvedGame);

        // 3. Direct ProfileName substring match ("Book1 - Excel" -> "Excel")
        var resolvedExcel = ProfileRouter.ResolveActiveProfile(rules, library, "Book1 - Excel", manualProfileFilter: null, autoSwitchByWindow: true);
        Assert.Equal("Excel", resolvedExcel);

        // 4. FilterArmedMacros when AutoSwitch is ON and Excel is focused: only Global + Excel macros are armed.
        var armedInExcel = ProfileRouter.FilterArmedMacros(
            library,
            activeProfile: resolvedExcel,
            foregroundWindowTitle: "Book1 - Excel",
            autoSwitchByWindow: true,
            isManualProfileSelected: false);
        Assert.Equal(2, armedInExcel.Count);
        Assert.Contains(armedInExcel, m => m.Id == globalMacro.Id);
        Assert.Contains(armedInExcel, m => m.Id == excelMacro.Id);

        // 5. FilterArmedMacros when AutoSwitch is OFF and no manual filter is set: all macros are armed.
        var armedAll = ProfileRouter.FilterArmedMacros(
            library,
            activeProfile: null,
            foregroundWindowTitle: "Desktop",
            autoSwitchByWindow: false,
            isManualProfileSelected: false);
        Assert.Equal(4, armedAll.Count);
    }

    // ---------------- v1.6 Eyes: GaugeFillReader & RelativeRect ----------------

    [Fact]
    public void GaugeFillReader_MeasuresHorizontalAndVerticalBars_AndHandlesTextOverlay()
    {
        var green = new RgbColor(34, 197, 94); // #22C55E
        var white = new RgbColor(255, 255, 255);

        // 100x20 horizontal bar filled 65% left-to-right
        var img = BgraImage.Create(100, 20);
        img.FillRectangle(new ScreenRect(0, 0, 65, 20), green);
        // Simulate white HP text ("650 / 1000") sitting in the middle 4 rows of the bar
        img.FillRectangle(new ScreenRect(35, 8, 20, 4), white);

        var frame = new ScreenFrame(new ScreenRect(0, 0, 100, 20), img);
        var pct = GaugeFillReader.MeasureFillPercent(frame, "#22C55E", tolerance: 24, GaugeDirection.LeftToRight);
        Assert.Equal(65.0, pct);

        // Right-to-left bar filled 40% from the right edge
        var rtlImg = BgraImage.Create(100, 16);
        rtlImg.FillRectangle(new ScreenRect(60, 0, 40, 16), green);
        var rtlPct = GaugeFillReader.MeasureFillPercent(new ScreenFrame(new ScreenRect(0, 0, 100, 16), rtlImg), "#22C55E", 24, GaugeDirection.RightToLeft);
        Assert.Equal(40.0, rtlPct);

        // Bottom-to-top vertical bar filled 75% from the bottom
        var bttImg = BgraImage.Create(20, 100);
        bttImg.FillRectangle(new ScreenRect(0, 25, 20, 75), green);
        var bttPct = GaugeFillReader.MeasureFillPercent(new ScreenFrame(new ScreenRect(0, 0, 20, 100), bttImg), "#22C55E", 24, GaugeDirection.BottomToTop);
        Assert.Equal(75.0, bttPct);
    }

    [Fact]
    public void RelativeRect_ScalesCleanly_From1080p_To1440pAnd4K()
    {
        var fullHd = new ScreenRect(0, 0, 1920, 1080);
        var barAt1080p = new ScreenRect(192, 972, 384, 54); // 10% X, 90% Y, 20% W, 5% H

        var rel = RelativeRect.FromScreenRect(barAt1080p, fullHd);
        Assert.True(rel.IsValid);
        Assert.Equal(0.1, rel.X, 4);
        Assert.Equal(0.9, rel.Y, 4);
        Assert.Equal(0.2, rel.Width, 4);
        Assert.Equal(0.05, rel.Height, 4);

        var qhd = new ScreenRect(0, 0, 2560, 1440);
        var barAt1440p = rel.ToScreenRect(qhd);
        Assert.Equal(new ScreenRect(256, 1296, 512, 72), barAt1440p);

        var uhd4k = new ScreenRect(0, 0, 3840, 2160);
        var barAt4k = rel.ToScreenRect(uhd4k);
        Assert.Equal(new ScreenRect(384, 1944, 768, 108), barAt4k);
    }

    [Fact]
    public void SetVariableStep_ScreenGaugePercent_ValidatesAndBumpsSchema17()
    {
        var macro = new Macro
        {
            Name = "HP Watcher",
            ProfileName = "Elden Ring",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "hp", Type = MacroVariableType.Number, DefaultValue = "100" },
            },
            Steps = new List<MacroStep>
            {
                new SetVariableStep
                {
                    VariableName = "hp",
                    Source = VariableSourceKind.ScreenGaugePercent,
                    RegionX = 100,
                    RegionY = 50,
                    RegionWidth = 200,
                    RegionHeight = 20,
                    GaugeColor = "#22C55E",
                    GaugeTolerance = 28,
                    GaugeDirection = GaugeDirection.LeftToRight,
                },
            },
        };

        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(macro));
        var issues = MacroValidator.Validate(macro);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    // ---------------- v1.7 Workbench + Arena: Text Transforms, List Indexing, Window Snap & Blueprints ----------------

    [Fact]
    public void VariableStore_WorkbenchTextOperations_And_LineAtLoopIndex_WorkCleanly()
    {
        var store = new VariableStore();

        Assert.Equal("Hello World", store.Apply("clean", MacroVariableType.Text, VariableOperation.TrimText, "   Hello   \r\n  World  "));
        Assert.Equal("12450", store.Apply("digits", MacroVariableType.Text, VariableOperation.ExtractDigits, "Order #12-450 (Paid)"));
        Assert.Equal("URGENT", store.Apply("upper", MacroVariableType.Text, VariableOperation.ToUpper, "  urgent "));
        Assert.Equal("ready", store.Apply("lower", MacroVariableType.Text, VariableOperation.ToLower, " READY "));

        // CSV / semicolon single-line list
        var csv = "SKU-101, SKU-102 ; SKU-103";
        Assert.Equal("SKU-101", VariableStore.ExtractItemAtOneBasedIndex(csv, 1));
        Assert.Equal("SKU-102", VariableStore.ExtractItemAtOneBasedIndex(csv, 2));
        Assert.Equal("SKU-103", VariableStore.ExtractItemAtOneBasedIndex(csv, 3));
        Assert.Equal(string.Empty, VariableStore.ExtractItemAtOneBasedIndex(csv, 4));

        // Multi-line list
        var lines = "First Row\r\n\r\nSecond Row\nThird Row";
        Assert.Equal("First Row", VariableStore.ExtractItemAtOneBasedIndex(lines, 1));
        Assert.Equal("Second Row", VariableStore.ExtractItemAtOneBasedIndex(lines, 2));
        Assert.Equal("Third Row", VariableStore.ExtractItemAtOneBasedIndex(lines, 3));
        Assert.Equal(string.Empty, VariableStore.ExtractItemAtOneBasedIndex(lines, 4));
    }

    [Fact]
    public void FocusWindowStep_SnapLayout_BumpsSchema17_AndDescribesCleanly()
    {
        var focus = new FocusWindowStep
        {
            WindowTitle = "Excel",
            RestoreIfMinimized = true,
            SnapLayout = WindowSnapLayout.SnapLeftHalf,
        };
        var macro = new Macro
        {
            Name = "Snap Excel",
            Steps = new List<MacroStep> { focus },
        };

        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(macro));
        Assert.Contains("snap left half", StepDescriber.Describe(focus));
    }

    [Fact]
    public void MacroBlueprints_ArenaAndWorkbench_BuildBalancedBlocks_AndSeedVariables()
    {
        foreach (var bp in new[]
        {
            MacroBlueprint.ArenaAutoPotionLoop,
            MacroBlueprint.ArenaSkillPriorityRotation,
            MacroBlueprint.WorkbenchListRowLoop,
        })
        {
            var info = MacroBlueprints.Info(bp);
            Assert.False(string.IsNullOrWhiteSpace(info.Title));

            var steps = MacroBlueprints.Build(bp);
            Assert.NotEmpty(steps);

            var blocks = BlockStructure.Analyze(steps);
            Assert.True(blocks.IsValid);
        }

        var autoPotVars = MacroBlueprints.BuildVariables(MacroBlueprint.ArenaAutoPotionLoop);
        Assert.Contains(autoPotVars, v => v.Name == "hp");

        var workbenchVars = MacroBlueprints.BuildVariables(MacroBlueprint.WorkbenchListRowLoop);
        Assert.Contains(workbenchVars, v => v.Name == "rowList");
        Assert.Contains(workbenchVars, v => v.Name == "currentItem");
    }
}
