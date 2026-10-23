using SmartMacro.Core.Models;

namespace SmartMacro.Core.Workflow;

/// <summary>What a Command Palette (Ctrl+K) entry does.</summary>
public enum StudioCommandKind
{
    /// <summary>Adds a step (<see cref="StudioCommand.Step"/>) like the toolbox does.</summary>
    AddStep,

    /// <summary>Plays the macro (the Play button / play shortcut).</summary>
    Play,

    /// <summary>Plays from the selected step to the end (Shift+F5).</summary>
    PlayFromSelected,

    /// <summary>"✨ Auto-Fix Block Structure".</summary>
    AutoFixBlocks,

    /// <summary>"🧹 Clean up": merges redundant Delay / Move mouse steps (same total timing).</summary>
    CleanUp,

    /// <summary>Compact step view on / off.</summary>
    ToggleCompactView,

    /// <summary>Humanize on / off.</summary>
    ToggleHumanize,

    /// <summary>Sets the playback speed to <see cref="StudioCommand.Speed"/>.</summary>
    SetSpeed,
}

/// <summary>
/// One entry of the Studio Command Palette (Ctrl+K). <see cref="Keywords"/> are extra words the filter matches (not
/// shown), e.g. "ocr read" for Find text. <see cref="Shortcut"/> is shown on the right ("Shift+F5"), may be empty.
/// </summary>
public sealed record StudioCommand(
    string Id,
    string Title,
    StudioCommandKind Kind,
    string Keywords = "",
    StepKind? Step = null,
    double Speed = 0,
    string Shortcut = "")
{
    /// <summary>The text the filter looks in (title + keywords, lower case).</summary>
    public string SearchText { get; } = (Title + " " + Keywords).ToLowerInvariant();
}

/// <summary>
/// The Command Palette's list and its filter, kept in Core so they can be unit tested (the editor only shows them and
/// runs the chosen one).
/// </summary>
public static class StudioCommandCatalog
{
    /// <summary>Speeds offered as "⚡ Set Playback Speed: …".</summary>
    public static IReadOnlyList<double> PaletteSpeeds { get; } = new[] { 0.5, 1.0, 2.0, 5.0 };

    /// <summary>Every palette command, in the order shown when the search box is empty.</summary>
    public static IReadOnlyList<StudioCommand> All { get; } = Build();

    private static IReadOnlyList<StudioCommand> Build()
    {
        var list = new List<StudioCommand>
        {
            Add("add-find-image", "➕ Add: Find Image on Screen", StepKind.FindImage, "vision picture template match screenshot search look"),
            Add("add-find-color", "➕ Add: Find Color on Screen", StepKind.FindColor, "vision colour pixel search look"),
            Add("add-find-text", "➕ Add: Find Text (OCR)", StepKind.FindText, "ocr read words vision search look"),
            Add("add-if", "➕ Add: If Condition ... End If", StepKind.If, "condition branch logic else when"),
            Add("add-repeat", "➕ Add: Repeat Loop ... End Repeat", StepKind.Repeat, "loop times while until logic"),
            Add("add-click", "➕ Add: Mouse Click", StepKind.MouseClick, "mouse press button tap"),
            Add("add-move", "➕ Add: Move Mouse", StepKind.MouseMove, "mouse cursor position glide"),
            Add("add-key", "➕ Add: Key Press", StepKind.KeyPress, "keyboard hotkey type button"),
            Add("add-delay", "➕ Add: Wait (Delay)", StepKind.Delay, "pause sleep timer wait"),
            Add("add-feedback", "➕ Add: Feedback Loop", StepKind.FeedbackLoop, "minigame bar fishing marker zone"),
            new("play", "▶ Play Macro", StudioCommandKind.Play, "run start"),
            new("play-from-selected", "▶ Play From Selected Step", StudioCommandKind.PlayFromSelected, "run start here partial", Shortcut: "Shift+F5"),
            new("auto-fix", "✨ Auto-Fix Block Structure", StudioCommandKind.AutoFixBlocks, "repair end if repeat orphan broken blocks"),
            new("clean-up", "🧹 Clean Up Redundant Steps", StudioCommandKind.CleanUp, "tidy merge mouse paths delays simplify"),
            new("compact", "📐 Toggle Compact Step View", StudioCommandKind.ToggleCompactView, "dense list rows view"),
            new("humanize", "🎲 Toggle Humanize Mode", StudioCommandKind.ToggleHumanize, "random jitter human natural"),
        };

        foreach (var speed in PaletteSpeeds)
        {
            var text = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
            list.Add(new StudioCommand($"speed-{text}", $"⚡ Set Playback Speed: {text}", StudioCommandKind.SetSpeed,
                "speed fast slow playback " + text, Speed: speed));
        }

        return list;

        static StudioCommand Add(string id, string title, StepKind kind, string keywords) =>
            new(id, title, StudioCommandKind.AddStep, "add insert new step " + keywords, Step: kind);
    }

    /// <summary>
    /// The commands matching <paramref name="query"/>: every word of the query must appear in the title or keywords
    /// (any order, ignoring case). Best first: title starts with / contains the first word before keyword-only matches;
    /// otherwise the catalog order is kept. An empty query returns everything.
    /// </summary>
    public static IReadOnlyList<StudioCommand> Filter(IEnumerable<StudioCommand> commands, string? query)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var list = commands.ToList();
        var words = (query ?? string.Empty).ToLowerInvariant()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return list;

        return list
            .Select((command, order) => (command, order, rank: Rank(command, words)))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.order)
            .Select(x => x.command)
            .ToList();
    }

    /// <summary>The filter for the full catalog.</summary>
    public static IReadOnlyList<StudioCommand> Filter(string? query) => Filter(All, query);

    // -1 = no match; 0 = a title word starts with the first query word; 1 = the title contains it; 2 = keywords only.
    private static int Rank(StudioCommand command, string[] words)
    {
        foreach (var word in words)
        {
            if (!command.SearchText.Contains(word, StringComparison.Ordinal)) return -1;
        }

        var title = command.Title.ToLowerInvariant();
        var first = words[0];
        var titleWords = title.Split(new[] { ' ', ':', '(', ')', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (titleWords.Any(w => w.StartsWith(first, StringComparison.Ordinal))) return 0;
        return title.Contains(first, StringComparison.Ordinal) ? 1 : 2;
    }
}
