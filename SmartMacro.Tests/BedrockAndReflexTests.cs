using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
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
}
