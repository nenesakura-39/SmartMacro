using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using Xunit;

namespace SmartMacro.Tests;

public sealed class TriggerContextAndRoutingTests
{
    [Fact]
    public void ProfileRouter_MatchesProcessNameRules_WithExeNormalization_AndCombinedFilter()
    {
        var codeRule = new MacroProfileRule
        {
            ProfileName = "Coding",
            ProcessNameMatch = "Code.exe",
            Enabled = true,
        };
        var chromeDocsRule = new MacroProfileRule
        {
            ProfileName = "WorkDocs",
            WindowTitleMatch = "Design Doc",
            ProcessNameMatch = "chrome",
            Enabled = true,
        };

        var rules = new[] { codeRule, chromeDocsRule };
        var codingMacro = new Macro { Name = "Format", ProfileName = "Coding" };
        var docsMacro = new Macro { Name = "Paste", ProfileName = "WorkDocs" };
        var notepadMacro = new Macro { Name = "NotepadHelper", ProfileName = "notepad" };
        var globalMacro = new Macro { Name = "Global" };
        var library = new[] { codingMacro, docsMacro, notepadMacro, globalMacro };

        // Matches process rule even when window title has "Readme" instead of "Visual Studio Code".
        var resolvedCoding = ProfileRouter.ResolveActiveProfile(
            rules, library, "Program.cs - MyRepo", manualProfileFilter: null, autoSwitchByWindow: true, foregroundProcessName: "Code");
        Assert.Equal("Coding", resolvedCoding);

        // Combined WindowTitleMatch + ProcessNameMatch requires BOTH to match.
        var wrongTitle = ProfileRouter.ResolveActiveProfile(
            rules, library, "Inbox - Gmail", manualProfileFilter: null, autoSwitchByWindow: true, foregroundProcessName: "chrome.exe");
        Assert.Null(wrongTitle);

        var wrongProcess = ProfileRouter.ResolveActiveProfile(
            rules, library, "Design Doc - Viewer", manualProfileFilter: null, autoSwitchByWindow: true, foregroundProcessName: "msedge");
        Assert.Null(wrongProcess);

        var matchedBoth = ProfileRouter.ResolveActiveProfile(
            rules, library, "Design Doc - Google Docs", manualProfileFilter: null, autoSwitchByWindow: true, foregroundProcessName: "CHROME.EXE");
        Assert.Equal("WorkDocs", matchedBoth);

        // Direct ProfileName match against foregroundProcessName when no explicit rule exists.
        var directProc = ProfileRouter.ResolveActiveProfile(
            rules, library, "Untitled", manualProfileFilter: null, autoSwitchByWindow: true, foregroundProcessName: "notepad");
        Assert.Equal("notepad", directProc);

        var armedForNotepad = ProfileRouter.FilterArmedMacros(
            library,
            activeProfile: directProc,
            foregroundWindowTitle: "Untitled",
            autoSwitchByWindow: true,
            isManualProfileSelected: false,
            foregroundProcessName: "notepad");

        Assert.Equal(2, armedForNotepad.Count);
        Assert.Contains(armedForNotepad, m => m.Id == notepadMacro.Id);
        Assert.Contains(armedForNotepad, m => m.Id == globalMacro.Id);
    }

    [Fact]
    public void ResolveActiveHotkeyEntries_ScopesToActiveWindow_AndRespectsPassThroughPrecedence()
    {
        var f1 = new HotkeyBinding(KeyModifiers.None, 0x70);
        var f2 = new HotkeyBinding(KeyModifiers.None, 0x71);
        var eKey = new HotkeyBinding(KeyModifiers.None, 'E');

        var gameMacro = new Macro
        {
            Name = "Elden Auto",
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Interact",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = eKey,
                    OnlyWhenWindowActive = true,
                    ProcessNameMatch = "eldenring",
                    PassThroughKey = true,
                    Enabled = true,
                },
                new()
                {
                    Name = "Consuming Backup",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = f1,
                    OnlyWhenWindowActive = true,
                    WindowTitle = "ELDEN RING",
                    PassThroughKey = false,
                    Enabled = true,
                },
            },
        };

        var globalF1PassThrough = new Macro
        {
            Name = "Global F1 Pass",
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Observe F1",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = f1,
                    PassThroughKey = true,
                    Enabled = true,
                },
                new()
                {
                    Name = "Disabled F2",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = f2,
                    Enabled = false,
                },
            },
        };

        var armed = new[] { gameMacro, globalF1PassThrough };

        // In Explorer: game's 'E' and consuming 'F1' are out-of-context, so only global F1 (PassThroughKey=true) is active.
        var inExplorer = ProfileRouter.ResolveActiveHotkeyEntries(armed, "Documents", "explorer");
        Assert.Single(inExplorer);
        Assert.Equal(new ScopedHotkeyBinding(f1, PassThroughKey: true), inExplorer[0]);

        // In Elden Ring: both E (PassThroughKey=true) and F1 (mixed true + false -> PassThroughKey=false wins) are active.
        var inGame = ProfileRouter.ResolveActiveHotkeyEntries(armed, "ELDEN RING™", "eldenring.exe");
        Assert.Equal(2, inGame.Count);
        Assert.Contains(new ScopedHotkeyBinding(eKey, PassThroughKey: true), inGame);
        Assert.Contains(new ScopedHotkeyBinding(f1, PassThroughKey: false), inGame);
    }

    [Fact]
    public void TriggerEvaluator_MatchesProcessName_ForHotkeys_AndWindowEvents_WithoutResettingOpenBaseline()
    {
        var eKey = new HotkeyBinding(KeyModifiers.None, 'E');
        var hotkeyMacro = new Macro
        {
            Name = "Process Scoped Hotkey",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 25 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Game E",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = eKey,
                    OnlyWhenWindowActive = true,
                    ProcessNameMatch = "notepad.exe",
                    PassThroughKey = true,
                    Enabled = true,
                },
            },
        };

        var openedMacro = new Macro
        {
            Name = "Calc Opened",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 25 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "On Calc Launch",
                    Kind = TriggerKind.WindowEvent,
                    WindowEvent = WindowTriggerEvent.WindowOpened,
                    ProcessNameMatch = "CalculatorApp",
                    Enabled = true,
                },
            },
        };

        var focusedMacro = new Macro
        {
            Name = "Code Focused",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 25 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "On Code Focus",
                    Kind = TriggerKind.WindowEvent,
                    WindowEvent = WindowTriggerEvent.WindowFocused,
                    ProcessNameMatch = "Code",
                    Enabled = true,
                },
            },
        };

        var library = new[] { hotkeyMacro, openedMacro, focusedMacro };
        Assert.True(TriggerEvaluator.RequiresOpenWindowsSnapshot(library));
        Assert.False(TriggerEvaluator.HasScheduleTriggers(library));

        var evaluator = new TriggerEvaluator();
        var t0 = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

        // Hotkey denied in wrong process, allowed in matching process.
        var wrongProcDecision = evaluator.EvaluateHotkeyDown(library, eKey, "Untitled", null, t0, "chrome");
        Assert.Equal(TriggerDecisionKind.None, wrongProcDecision.Kind);

        var rightProcDecision = evaluator.EvaluateHotkeyDown(library, eKey, "Untitled - Notes", null, t0.AddSeconds(1), "notepad");
        Assert.Equal(TriggerDecisionKind.StartMacro, rightProcDecision.Kind);
        Assert.Equal(hotkeyMacro.Id, rightProcDecision.Macro!.Id);

        // First WindowInfo snapshot establishes baseline without false-firing WindowOpened or WindowFocused.
        var initialWindows = new[] { new WindowInfo(101, "Desktop", new ScreenRect(0, 0, 100, 100), "explorer") };
        var d1 = evaluator.EvaluateTick(library, t0.AddSeconds(2), initialWindows, "Desktop", "explorer", null);
        Assert.Empty(d1);

        // Second tick: Calculator opens -> WindowOpened fires cleanly by ProcessNameMatch.
        var nextWindows = new[]
        {
            new WindowInfo(101, "Desktop", new ScreenRect(0, 0, 100, 100), "explorer"),
            new WindowInfo(202, "Calculator", new ScreenRect(10, 10, 200, 300), "CalculatorApp"),
        };
        var d2 = evaluator.EvaluateTick(library, t0.AddSeconds(3), nextWindows, "Calculator", "CalculatorApp", null);
        Assert.Single(d2);
        Assert.Equal(openedMacro.Id, d2[0].Macro!.Id);

        // Third tick (playback idle): Code gains focus while Calculator remains open -> ONLY WindowFocused fires.
        var d3 = evaluator.EvaluateTick(library, t0.AddSeconds(4), nextWindows, "main.cs - Editor", "Code", null);
        Assert.Single(d3);
        Assert.Equal(focusedMacro.Id, d3[0].Macro!.Id);

        // Subsequent schedule/poll tick reusing cached nextWindows must NOT re-fire WindowOpened or WindowFocused.
        var d4 = evaluator.EvaluateTick(library, t0.AddSeconds(5), nextWindows, "main.cs - Editor", "Code", null);
        Assert.Empty(d4);
    }

    [Fact]
    public void MacroValidator_And_Serialization_SupportProcessNameAndPassThroughKey()
    {
        var macro = new Macro
        {
            Name = "Process Trigger Valid",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 50 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Scoped Hotkey",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = new HotkeyBinding(KeyModifiers.Control, 'K'),
                    OnlyWhenWindowActive = true,
                    ProcessNameMatch = "Code",
                    PassThroughKey = true,
                },
                new()
                {
                    Name = "Process Window Focus",
                    Kind = TriggerKind.WindowEvent,
                    WindowEvent = WindowTriggerEvent.WindowFocused,
                    ProcessNameMatch = "msedge",
                },
            },
        };

        var validIssues = MacroValidator.Validate(macro);
        Assert.DoesNotContain(validIssues, i => i.Severity == IssueSeverity.Error);

        // Serialization roundtrip & Clone preserve new fields.
        var json = MacroJson.Serialize(macro);
        var roundtripped = MacroJson.Deserialize<Macro>(json)!;
        Assert.NotNull(roundtripped.Triggers);
        Assert.Equal("Code", roundtripped.Triggers![0].ProcessNameMatch);
        Assert.True(roundtripped.Triggers[0].PassThroughKey);
        Assert.Equal("msedge", roundtripped.Triggers[1].ProcessNameMatch);
        Assert.Contains("msedge", roundtripped.Triggers[1].DescribeShort());

        var clonedTrigger = roundtripped.Triggers[0].Clone();
        Assert.Equal("Code", clonedTrigger.ProcessNameMatch);
        Assert.True(clonedTrigger.PassThroughKey);
        Assert.Contains("Pass-through", clonedTrigger.DescribeShort());

        // Validation rejects empty WindowTitle + empty ProcessNameMatch when required, and rejects overlong ProcessNameMatch.
        var invalidMacro = new Macro
        {
            Name = "Missing Filter",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 50 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Bad Scoped Hotkey",
                    Kind = TriggerKind.Hotkey,
                    Hotkey = new HotkeyBinding(KeyModifiers.None, 0x72),
                    OnlyWhenWindowActive = true,
                    WindowTitle = "",
                    ProcessNameMatch = "   ",
                },
                new()
                {
                    Name = "Overlong Process Name",
                    Kind = TriggerKind.WindowEvent,
                    WindowEvent = WindowTriggerEvent.WindowFocused,
                    ProcessNameMatch = new string('x', SearchArea.MaxWindowTitleLength + 5),
                },
            },
        };
        var invalidIssues = MacroValidator.Validate(invalidMacro);
        Assert.Equal(2, invalidIssues.Count(i => i.Severity == IssueSeverity.Error));

        // Rule Clone() preserves ProcessNameMatch.
        var rule = new MacroProfileRule { ProfileName = "Dev", WindowTitleMatch = "Studio", ProcessNameMatch = "devenv" };
        var clonedRule = rule.Clone();
        Assert.Equal("devenv", clonedRule.ProcessNameMatch);
        Assert.True(clonedRule.Matches("SmartMacro - Microsoft Visual Studio", "devenv.exe"));
    }

    [Fact]
    public void TriggerEvaluator_ScheduleTickWithCachedOpenWindows_EvaluatesScheduleWithoutBreakingWindowOpenedBaseline()
    {
        var openedMacro = new Macro
        {
            Name = "On Notepad Open",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 20 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Opened",
                    Kind = TriggerKind.WindowEvent,
                    WindowEvent = WindowTriggerEvent.WindowOpened,
                    ProcessNameMatch = "notepad",
                },
            },
        };

        var scheduleMacro = new Macro
        {
            Name = "Every 5 Min",
            Steps = new List<MacroStep> { new DelayStep { DurationMs = 20 } },
            Triggers = new List<MacroTrigger>
            {
                new()
                {
                    Name = "Periodic",
                    Kind = TriggerKind.Schedule,
                    Recurrence = ScheduleRecurrence.EveryNMinutes,
                    IntervalMinutes = 5,
                },
            },
        };

        var library = new[] { openedMacro, scheduleMacro };
        Assert.True(TriggerEvaluator.RequiresOpenWindowsSnapshot(library));
        Assert.True(TriggerEvaluator.HasScheduleTriggers(library));

        var evaluator = new TriggerEvaluator();
        var t0 = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var cachedBefore = new[] { new WindowInfo(1, "Desktop", new ScreenRect(0, 0, 100, 100), "explorer") };

        // Initial tick establishes WindowOpened and EveryNMinutes baselines without false firing.
        var t1Decisions = evaluator.EvaluateTick(library, t0, cachedBefore, "Desktop", "explorer", null);
        Assert.Empty(t1Decisions);

        // Next 5 minutes pass: cached open windows stays the same; Schedule fires while WindowOpened stays quiet.
        var t2Decisions = evaluator.EvaluateTick(library, t0.AddMinutes(5), cachedBefore, "Desktop", "explorer", null);
        Assert.Single(t2Decisions);
        Assert.Equal(scheduleMacro.Id, t2Decisions[0].Macro!.Id);

        // Now Notepad opens: WindowOpened fires immediately even right after schedule ticks.
        var withNotepad = new[]
        {
            new WindowInfo(1, "Desktop", new ScreenRect(0, 0, 100, 100), "explorer"),
            new WindowInfo(2, "Untitled - Notepad", new ScreenRect(0, 0, 500, 400), "notepad"),
        };
        var t3Decisions = evaluator.EvaluateTick(library, t0.AddMinutes(5).AddSeconds(2), withNotepad, "Untitled - Notepad", "notepad", null);
        Assert.Single(t3Decisions);
        Assert.Equal(openedMacro.Id, t3Decisions[0].Macro!.Id);
    }
}
