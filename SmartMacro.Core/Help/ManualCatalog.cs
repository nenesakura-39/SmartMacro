using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Help;

/// <summary>The Manual's category pills.</summary>
public enum ManualCategory
{
    QuickStart,
    VisionAndSmartAim,
    LogicAndLoops,
    MacroRecipes,
    KeyboardShortcuts,
    TroubleshootingAndTips,
}

/// <summary>One block of an article: optional heading, a paragraph and optional bullet points.</summary>
public sealed record ManualSection(string? Heading, string Body, IReadOnlyList<string>? Bullets = null)
{
    public IReadOnlyList<string> Points => Bullets ?? Array.Empty<string>();
    public bool HasHeading => !string.IsNullOrEmpty(Heading);
    public bool HasBody => !string.IsNullOrEmpty(Body);
    public bool HasPoints => Points.Count > 0;
}

/// <summary>
/// One Manual article / guide card. <see cref="Recipe"/> set = a Macro Recipe card with "➕ Load Example into Editor".
/// <see cref="Keywords"/> are extra words the search matches (not shown).
/// </summary>
public sealed record ManualArticle(
    string Id,
    ManualCategory Category,
    string Glyph,
    string Title,
    string Summary,
    IReadOnlyList<ManualSection> Sections,
    string Keywords = "",
    MacroBlueprint? Recipe = null)
{
    public bool IsRecipe => Recipe is not null;

    /// <summary>The category pill text shown on the card ("🎯 Vision & Smart-Aim").</summary>
    public string CategoryLabel => ManualCatalog.Label(Category);

    /// <summary>Everything the search looks in, lower case.</summary>
    public string SearchText { get; } = string.Join(" ",
        new[] { Title, Summary, Keywords }
            .Concat(Sections.SelectMany(s => new[] { s.Heading ?? "", s.Body }.Concat(s.Points)))).ToLowerInvariant();
}

/// <summary>One row of the shortcut cheat-sheet.</summary>
public sealed record ManualShortcut(string Keys, string Action, string Where)
{
    public string SearchText { get; } = (Keys + " " + Action + " " + Where).ToLowerInvariant();
}

/// <summary>
/// The In-App Manual's content (Studio Knowledge Base & Recipe Center) and its search. Plain data in Core so the search
/// and the recipe links can be unit tested; the app only shows it.
/// </summary>
public static class ManualCatalog
{
    /// <summary>Pill text for a category ("🚀 Quick Start" …).</summary>
    public static string Label(ManualCategory category) => category switch
    {
        ManualCategory.QuickStart => "🚀 Quick Start",
        ManualCategory.VisionAndSmartAim => "🎯 Vision & Smart-Aim",
        ManualCategory.LogicAndLoops => "🔀 Logic & Loops",
        ManualCategory.MacroRecipes => "🎮 Macro Recipes",
        ManualCategory.KeyboardShortcuts => "⌨️ Keyboard Shortcuts",
        ManualCategory.TroubleshootingAndTips => "🛠 Troubleshooting & Pro Tips",
        _ => category.ToString(),
    };

    /// <summary>
    /// The cheat-sheet with the keys really in use: the three global shortcuts come from Settings (they can be
    /// changed there), the editor keys are fixed.
    /// </summary>
    public static IReadOnlyList<ManualShortcut> Shortcuts(string recordKey, string playKey, string pauseKey) => new[]
    {
        new ManualShortcut(recordKey, "Start / stop recording", "Anywhere (global)"),
        new ManualShortcut(playKey, "Play the macro / stop playback", "Anywhere (global)"),
        new ManualShortcut(pauseKey, "Pause / resume (held keys and buttons are released while paused)", "While playing (global)"),
        new ManualShortcut("Esc", "Emergency stop (your real Esc key; the macro's own Esc presses are ignored)", "While playing"),
        new ManualShortcut("Shift+F5", "Play from the selected step to the end", "Editor"),
        new ManualShortcut("Ctrl+F5", "▶ Run step: play only the selected step (an If / Repeat runs its whole block)", "Editor"),
        new ManualShortcut("Ctrl+T", "🧪 Test Step: look for the selected step's target once (near-miss score), or run an action step once", "Editor"),
        new ManualShortcut("Ctrl+K", "Command Palette: add steps, blueprints, play, auto-fix, speed…", "Editor"),
        new ManualShortcut("Ctrl+F", "Search steps (Enter / F3 next, Shift+Enter / Shift+F3 previous, Esc clears)", "Editor"),
        new ManualShortcut("Ctrl+S", "Save the macro", "Editor"),
        new ManualShortcut("Ctrl+Z / Ctrl+Y", "Undo / redo (Ctrl+Shift+Z also redoes)", "Editor"),
        new ManualShortcut("Ctrl+C / Ctrl+V", "Copy / paste steps (whole If / Repeat blocks)", "Step list"),
        new ManualShortcut("Ctrl+D", "Block-aware duplicate (an If / Repeat copies its whole block)", "Step list"),
        new ManualShortcut("Delete", "Delete the selected steps (a folded block goes as a whole)", "Step list"),
        new ManualShortcut("Space", "Turn the selected steps (or a whole If / Repeat block) on / off", "Step list"),
        new ManualShortcut("← / →", "Fold / unfold the selected If / Repeat", "Step list"),
        new ManualShortcut("Alt+↑ / Alt+↓", "Block-aware move (a folded block moves as one row)", "Step list"),
        new ManualShortcut("Alt+Home / Alt+End", "Move to the top / bottom", "Step list"),
        new ManualShortcut("Ctrl+Shift+I / Ctrl+Shift+R", "Wrap the selected steps in an If / a Repeat", "Step list"),
    };

    /// <summary>Every article, in reading order.</summary>
    public static IReadOnlyList<ManualArticle> All { get; } = Build();

    /// <summary>
    /// Articles in <paramref name="category"/> (null = all) matching <paramref name="query"/>: every word must appear in the
    /// title, summary, text or keywords (any order, ignoring case). Reading order is kept.
    /// </summary>
    public static IReadOnlyList<ManualArticle> Filter(string? query, ManualCategory? category = null) =>
        All.Where(a => (category is null || a.Category == category) && Matches(a.SearchText, query)).ToList();

    /// <summary>Cheat-sheet rows matching <paramref name="query"/> (same word rules).</summary>
    public static IReadOnlyList<ManualShortcut> FilterShortcuts(IEnumerable<ManualShortcut> shortcuts, string? query) =>
        shortcuts.Where(s => Matches(s.SearchText, query)).ToList();

    /// <summary>The recipe card for a blueprint (every blueprint has one).</summary>
    public static ManualArticle RecipeFor(MacroBlueprint blueprint) => All.First(a => a.Recipe == blueprint);

    private static bool Matches(string text, string? query)
    {
        var words = (query ?? string.Empty).ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => text.Contains(w, StringComparison.Ordinal));
    }

    private static ManualSection S(string? heading, string body, params string[] bullets) =>
        new(heading, body, bullets.Length == 0 ? null : bullets);

    private static IReadOnlyList<ManualArticle> Build()
    {
        var list = new List<ManualArticle>
        {
            // ---------------- 🚀 Quick Start ----------------
            new("first-macro", ManualCategory.QuickStart, "🚀", "Your first macro in 60 seconds",
                "Record what you do, check the steps, play it back.",
                new[]
                {
                    S("1. Record", "Press the Record shortcut (or ● Record) and do the task once. Press it again to stop. SmartMacro turns everything into editable steps and opens them in the Workflow Editor.",
                        "Smart mode (default) works for normal apps and games: cursor parts become Move / Click steps, camera parts become a \"Recorded input\" step.",
                        "Desktop-only and Game-only modes are in Settings if Smart mode guesses wrong."),
                    S("2. Check the steps", "Each card is one step. Click it to see its settings in Properties on the right. Problems (red) must be fixed before you can save or play; warnings (amber) are hints."),
                    S("3. Play", "Press Play (or the Play shortcut). Your real Esc key stops playback at any time, and every key or mouse button SmartMacro was holding is released."),
                },
                "start begin beginner record play first tutorial new"),
            new("editor-basics", ManualCategory.QuickStart, "🧩", "The Workflow Editor",
                "Toolbox on the left, steps in the middle, Properties on the right.",
                new[]
                {
                    S("Adding steps", "Click a step in the toolbox to add it after the selected step (tick \"Add new steps at the end\" to always append). If / Repeat come with their End. Ctrl+K opens the Command Palette, which can add any step or blueprint by typing.",
                        "Steps inside an If / Repeat are indented with a coloured guide rail: amber = If / Else, violet = Repeat.",
                        "▼ / ▶ on an If / Repeat folds its block into one row; folded blocks still play."),
                    S("Saving", "Ctrl+S saves. Changes you haven't saved are marked with a dot in the sidebar; Discard goes back to the saved version. Ctrl+Z / Ctrl+Y undo and redo every edit."),
                },
                "toolbox properties add insert save undo redo fold collapse rail"),
            new("play-safely", ManualCategory.QuickStart, "🛡", "Playing, pausing and stopping safely",
                "Play n times, speed, pause, Play from here and Run step.",
                new[]
                {
                    S(null, "\"Play n times\" (0 = until stopped), \"Wait between repeats\" and the speed selector (0.5× – 5×) are in the editor header. Holds never get shorter than 25 ms, so games still see fast clicks.",
                        "Pause (F8 by default) releases every held key and button at once and presses them again on resume; paused time doesn't count toward timeouts.",
                        "Shift+F5 plays from the selected step to the end. Starting inside an If's \"then\" part skips its Else part; starting inside a loop plays the rest of the loop once.",
                        "Ctrl+F5 (▶ Run step) plays only the selected step; on an If / Repeat it runs the whole block.",
                        "After a run, the summary pill shows e.g. \"✓ 3 runs in 8.4s · 24 steps · 6 vision locks (avg 0.8ms)\"."),
                },
                "pause stop esc emergency speed repeat play from here run step summary"),

            // ---------------- 🎯 Vision & Smart-Aim ----------------
            new("template-capture", ManualCategory.VisionAndSmartAim, "🖼", "Find Image: capturing a good template",
                "Teach SmartMacro what a button looks like, and where to look for it.",
                new[]
                {
                    S("Capture", "Select a Find image step (or an If whose condition looks for an image) and press Capture image. SmartMacro hides, the screen freezes, and you drag a box tightly around the thing to find.",
                        "Capture only the stable part of a button (not a counter or a timer inside it).",
                        "Templates match at the size they were captured: after changing game resolution or Windows scaling, capture again."),
                    S("Where to look", "Main monitor, one monitor, a window (follows it when it moves) or a custom area. A smaller area is faster and avoids look-alikes elsewhere."),
                    S("Accuracy %", "How similar a spot must be (default 90 % for images). Lower it a little for buttons that glow or animate; raise it when the wrong thing gets clicked. Use 🧪 Test Step (Ctrl+T) to see the real score.",
                        "\"Ignore color\": shape only (buttons that change colour).",
                        "\"Ignore the edges\": for round buttons whose corners show the game behind them.",
                        "\"Ignore background\": for see-through game UI (bright text / outlines)."),
                },
                "image template capture picture button accuracy similarity area monitor window color edges background"),
            new("offset-picker", ManualCategory.VisionAndSmartAim, "✛", "Click offset picker (crosshair)",
                "Click a bit beside what was found, e.g. the \"Buy\" button next to an item.",
                new[]
                {
                    S(null, "Mouse click / Move mouse steps can act \"On the last found spot\" plus an offset. Press 🎯 Pick offset from screen: SmartMacro finds the reference target, marks it with a green ◎, and you click the spot you want. A dashed arrow and \"Offset (+x, +y)\" show the result live.",
                        "The offset is measured from the centre of what was found, so it keeps working when the target moves.",
                        "If nothing has been found yet in a run, a click on the last found spot is skipped with a warning (never clicks at 0, 0)."),
                },
                "offset crosshair pick last found spot relative click beside"),
            new("smart-aim", ManualCategory.VisionAndSmartAim, "✨", "Smart-Aim: local re-centering of recorded clicks",
                "Recorded clicks remember what they clicked and follow it if it moved a little.",
                new[]
                {
                    S(null, "With Smart Click Snapshots on (Settings), every recorded click keeps a 56×56 picture of what was under the cursor. Turn on Smart-Aim on the click and, during playback, SmartMacro looks for that picture within the Smart-Aim distance (120 px by default) and clicks the same spot on it.",
                        "Not found nearby → the recorded X / Y is clicked, exactly as before.",
                        "🎯 Test Smart-Aim shows how far the target moved and suggests a bigger distance when needed.",
                        "\"Convert Step to Full 'Find Image & Click'\" turns the snapshot into a full Find image step that searches the whole monitor."),
                },
                "smart aim snapshot recorded click recenter follow moved radius distance"),
            new("hot-spot", ManualCategory.VisionAndSmartAim, "⚡", "Hot-Spot ROI Turbo (< 1 ms re-checks)",
                "Why a loop that keeps finding the same button gets so fast.",
                new[]
                {
                    S(null, "After a Find image step or image condition has found its picture once, the next look first checks the last spot ± 24 px at full accuracy. A target that didn't move is confirmed in well under a millisecond; only when it isn't there does SmartMacro search the whole area again, so found / not found is exactly as before.",
                        "The header pill and the mini-HUD show the live look time (\"⚡ Vision: 0.6ms\").",
                        "The memory is per playback: every new Play starts with a full search.",
                        "With several identical copies on screen, the copy found last time keeps winning."),
                },
                "hot spot roi turbo fast speed telemetry millisecond loop performance"),
            new("test-step", ManualCategory.VisionAndSmartAim, "🧪", "🧪 Test Step (Ctrl+T) and near-miss tuning",
                "Check a vision step against the screen right now, and tune its Accuracy % with real numbers.",
                new[]
                {
                    S("How it works", "Select a Find image / colour / text step, or an If / Repeat with a screen condition, and press Ctrl+T (or 🧪 Test Step). SmartMacro hides for a moment, looks ONCE exactly like playback does, and shows the result in the bottom bar. Nothing is clicked.",
                        "Found: a green reticle flashes on the match — \"🧪 Test Passed: Found at (512, 340) · 96% confidence · 0.8ms\".",
                        "Not found: \"🧪 Test Failed: Not found (Closest match on screen: 79% at 512, 340 — step requires 85%)\", and an amber reticle flashes on that closest spot when it scored 50 % or more.",
                        "Any other step (click, move, key, delay…) is simply executed once: \"🧪 Executed step #4: …\"."),
                    S("Tuning the Accuracy % slider", "If the amber reticle sits on the RIGHT thing, set the accuracy a few points below the near-miss score (79 % → 75 %) or capture the image again. If it sits on the WRONG thing, the target isn't visible or looks different: check \"Where to look\", the game's resolution, or try Ignore color / edges / background."),
                },
                "test step near miss accuracy slider tune diagnose confidence closest match amber reticle ctrl+t"),
            new("color-text", ManualCategory.VisionAndSmartAim, "🎨", "Find Color and Find Text (OCR)",
                "Simple colour triggers and reading words on screen.",
                new[]
                {
                    S("Find color", "Looks for a patch of a colour (± tolerance) with at least N pixels — great for glows, health bars and \"ready\" lights. Keep the area small: whole-monitor colour searches find the colour everywhere."),
                    S("Find text (OCR)", "Uses the text recognition built into Windows (an OCR language pack must be installed). \"Normalize similar characters\" treats 0/O and 1/I/l alike. Stylized or very small game fonts may not be read; Find image is often more reliable there."),
                },
                "color colour pixel tolerance text ocr read words language"),

            // ---------------- 🔀 Logic & Loops ----------------
            new("if-else", ManualCategory.LogicAndLoops, "🔀", "If / Else / End If",
                "Do something only when an image, colour or text is (or is NOT) on screen.",
                new[]
                {
                    S(null, "An If checks its condition (looking for up to its \"keep checking for\" time), runs the steps up to Else when true, and the steps between Else and End If when false. Else is optional.",
                        "\"When found, also:\" can click / move / hold on the spot the condition saw, before the inner steps run.",
                        "\"is NOT on screen\" flips the answer (useful for \"wait until the loading screen is gone\").",
                        "A Mouse click with \"On the last found spot\" inside the If clicks what the If found."),
                },
                "if else end condition branch when found negate not"),
            new("repeat", ManualCategory.LogicAndLoops, "🔁", "Repeat loops: Times, While, Until",
                "Run steps again and again, safely.",
                new[]
                {
                    S(null, "Times runs a fixed number of turns. While runs as long as its condition is true; Until runs until it becomes true. While / Until always need a safety limit (max turns or max time), and you choose whether reaching it stops the macro or just leaves the loop.",
                        "\"Keep checking before leaving\" (800 ms by default for images / colours) stops a loop from ending just because a button blinked off for a frame.",
                        "Checks are spaced by \"check every\", so even an empty loop can't spin the CPU."),
                },
                "repeat loop times while until limit max turns grace keep checking"),
            new("blocks-tools", ManualCategory.LogicAndLoops, "🧱", "Working with blocks",
                "Folding, moving, duplicating and fixing If / Repeat blocks.",
                new[]
                {
                    S(null, "Blocks are always kept balanced:",
                        "Ctrl+D on an If / Repeat (or its Else / End) duplicates the whole block right after it, with fresh IDs.",
                        "Alt+↑ / ↓ moves steps one place; a folded block moves as one row and moving past a folded block jumps over it.",
                        "Deleting an If keeps the steps inside (its Else / End If go with it); deleting a FOLDED If deletes the whole block (Ctrl+Z brings it back).",
                        "New, pasted or blueprint steps never land inside a folded block: they go after it.",
                        "If the blocks ever don't match up (e.g. after a hand-edited file), the red banner's ✨ Auto-Fix Block Structure repairs them."),
                },
                "block fold collapse move duplicate delete auto fix balanced end if end repeat"),
            new("feedback", ManualCategory.LogicAndLoops, "⚖️", "Feedback loops (minigame bars)",
                "Keep a moving marker inside a zone by holding and releasing.",
                new[]
                {
                    S(null, "Pick the bar area, the marker colour and the zone (fixed % or found by its colour). The loop captures the bar up to N times per second and holds / releases / taps your key or mouse button depending on where the marker is. Measure loop speed and Preview 10 s (no input) help you tune it before playing.",
                        "It always stops by its max runtime, or earlier by its Done / Failed conditions.",
                        "Zones with momentum (heavy physics) are hard for any bang-bang controller; expect a lower win rate there."),
                },
                "feedback loop minigame bar fishing marker zone hold release tap"),

            // ---------------- ⌨️ Keyboard Shortcuts ----------------
            new("shortcuts", ManualCategory.KeyboardShortcuts, "⌨️", "Studio shortcut cheat-sheet",
                "Every key SmartMacro listens to. The three global ones can be changed in Settings → Keyboard shortcuts.",
                new[]
                {
                    S(null, "Global shortcuts work even while a game is in front. Editor and step-list keys only work while SmartMacro's editor has the keyboard, and never while you are typing in a text box (so Space and Delete type normally there)."),
                },
                "keyboard shortcut hotkey keys cheat sheet"),

            // ---------------- 🛠 Troubleshooting & Pro Tips ----------------
            new("admin", ManualCategory.TroubleshootingAndTips, "🛡", "The game ignores clicks and keys (Run as administrator)",
                "Windows blocks normal apps from controlling apps that run as administrator.",
                new[]
                {
                    S(null, "If the game or its launcher runs as administrator, SmartMacro's input is silently dropped (Windows UIPI). Start SmartMacro as administrator too (right-click → Run as administrator). Nothing works on the lock screen or UAC prompts.",
                        "Some anti-cheat systems ignore simulated input on purpose; SmartMacro never tries to bypass that.",
                        "Games that read raw mouse movement for the camera need a Game / Smart recording (relative movement), not a Desktop one."),
                },
                "administrator admin uipi blocked ignored input sendinput anti cheat game not working"),
            new("black-capture", ManualCategory.TroubleshootingAndTips, "🖥", "Black screen in tests (Borderless vs Exclusive Fullscreen)",
                "Find image can't see games that use exclusive fullscreen.",
                new[]
                {
                    S(null, "Screen capture (GDI BitBlt) gets a black picture from games in exclusive fullscreen and from some overlays. Switch the game to Borderless Windowed (or Windowed Fullscreen) and test again with 🧪 Test Step: the near-miss score jumps from 0 % to the real value."),
                },
                "black screen capture fullscreen exclusive borderless windowed bitblt overlay"),
            new("dpi", ManualCategory.TroubleshootingAndTips, "🧭", "Multi-monitor and DPI scaling",
                "Positions are physical pixels across all monitors.",
                new[]
                {
                    S(null, "SmartMacro is per-monitor DPI aware, so positions and captures are real pixels on every monitor (a monitor left of the main one has negative X). After changing the monitor layout or Windows scaling, re-pick positions — or turn on \"Auto-adjust coordinates\" (🎯 target window) so positions follow a window when it moves or resizes.",
                        "Templates match at their captured size: re-capture them after a scaling change."),
                },
                "monitor multi dpi scaling resolution negative coordinates auto adjust window"),
            new("stuck-keys", ManualCategory.TroubleshootingAndTips, "🔒", "Stuck-key safety protection",
                "SmartMacro never leaves a key or mouse button held down.",
                new[]
                {
                    S(null, "Every key / button SmartMacro presses is tracked. When a run ends — finished, stopped with Esc or the hotkey, failed, or the app closes — everything still down is released; Pause releases it too and presses it again on resume. If something still feels stuck after a crash of another app, tap the key once."),
                },
                "stuck key held button release safety failsafe stop pause"),
            new("obs", ManualCategory.TroubleshootingAndTips, "🎥", "The overlays don't show in OBS recordings",
                "That's on purpose.",
                new[]
                {
                    S(null, "The status pill, mini-HUD and target-lock reticle ask Windows to leave them out of every screen capture, so SmartMacro's own image searches never see them. OBS captures the screen the same way, so they're invisible in recordings and streams too; there is no OBS setting that changes this."),
                },
                "obs stream recording overlay hud reticle invisible capture"),
            new("pro-tips", ManualCategory.TroubleshootingAndTips, "💡", "Pro tips",
                "Small habits that make macros robust.",
                new[]
                {
                    S(null, "",
                        "Prefer \"Find image → click\" over fixed positions for buttons that can move.",
                        "Give steps a comment: it shows on the card, in the HUD and in search results.",
                        "Turn on Humanize for long-running loops (±2 px, ±4 % timing).",
                        "Ctrl+K → type a few letters is the fastest way to add anything.",
                        "Use 📐 Compact view for long macros, and Ctrl+F to jump to steps by name or comment.",
                        "Test vision steps with Ctrl+T before running a long loop."),
                },
                "tips tricks best practice robust humanize comment compact search palette"),
        };

        // ---------------- 🎮 Macro Recipes (one card per blueprint) ----------------
        var steps = new Dictionary<MacroBlueprint, string[]>
        {
            [MacroBlueprint.VisionClickWithFallback] = new[]
            {
                "Select the If and capture the image to look for (and set Where to look).",
                "The Mouse click clicks \"On the last found spot\" — the centre of what the If found. Use 🎯 Pick offset to click beside it.",
                "Press Ctrl+T on the If to check it finds the image right now.",
            },
            [MacroBlueprint.HumanizedAntiAfkLoop] = new[]
            {
                "Change the key (Space) or the count (50) in Properties.",
                "Humanize is switched on for the macro, so the 1.5 s wait varies a little every turn.",
                "Set \"Play n times\" to 0 in the header to keep it running until you press Esc.",
            },
            [MacroBlueprint.PixelColorTrigger] = new[]
            {
                "Pick the colour (Pick color) and a small area to watch.",
                "It waits up to 5 s; choose what happens when the colour doesn't appear (stop, continue, skip the next step).",
                "Ctrl+T shows how many matching pixels it sees right now.",
            },
            [MacroBlueprint.MinigameTrackingLoop] = new[]
            {
                "Select the Feedback loop and use the wizard: pick the bar area, the target colour and your bar's colour.",
                "Measure loop speed, then Preview 10 s (no input) to check the decisions.",
                "Raise the 1 s max runtime per round, or the 10 rounds, to fit your minigame.",
            },
        };

        foreach (var info in MacroBlueprints.All)
        {
            list.Add(new ManualArticle($"recipe-{info.Blueprint}", ManualCategory.MacroRecipes, info.Glyph, info.Title,
                $"{info.Shape}. {info.Description}",
                new[] { S("After loading", "", steps[info.Blueprint]) },
                "recipe example blueprint load template " + info.Keywords, info.Blueprint));
        }

        // Recipes right after Logic & Loops, before the cheat-sheet (reading order).
        return list
            .OrderBy(a => a.Category switch
            {
                ManualCategory.QuickStart => 0,
                ManualCategory.VisionAndSmartAim => 1,
                ManualCategory.LogicAndLoops => 2,
                ManualCategory.MacroRecipes => 3,
                ManualCategory.KeyboardShortcuts => 4,
                _ => 5,
            })
            .ToList();
    }
}
