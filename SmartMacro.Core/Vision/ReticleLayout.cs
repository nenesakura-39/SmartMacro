using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Where the target-lock reticle window goes and where its "🎯 98% · 45ms · Find image" badge sits inside it, in physical
/// screen pixels. The window is the target plus room for the corner brackets and glow, grown so the WHOLE badge (and its
/// glow) fits: a small target (a 63 px ball) with a 180 px badge used to get a ~99 px wide window that cut the text off.
/// The badge is centred over the target, goes below it when there's no room above, and is pushed inward at the screen
/// edges instead of being clipped. Pure, so it is unit tested.
/// </summary>
public static class ReticleLayout
{
    /// <summary>Room (px) around the target for the corner brackets and their glow.</summary>
    public const int Pad = 18;

    /// <summary>Gap (px) between the bracket area and the badge.</summary>
    public const int BadgeGap = 4;

    /// <summary>Room (px) around the badge for its glow (drop shadow), so the glow isn't cut off either.</summary>
    public const int BadgeGlow = 8;

    /// <summary>
    /// The reticle window and the badge's top-left corner inside it (relative to the window), or an empty window when
    /// nothing of it is on screen.
    /// </summary>
    /// <param name="box">What the brackets surround (the target, or the hollow area in streamer mode).</param>
    /// <param name="badgeWidth">Measured badge width in physical px (0 = no badge).</param>
    /// <param name="badgeHeight">Measured badge height in physical px (0 = no badge).</param>
    /// <param name="screen">The whole desktop (all monitors).</param>
    public static ReticleFrame Compute(ScreenRect box, int badgeWidth, int badgeHeight, ScreenRect screen)
    {
        var brackets = new ScreenRect(box.X - Pad, box.Y - Pad, box.Width + 2 * Pad, box.Height + 2 * Pad);
        if (badgeWidth <= 0 || badgeHeight <= 0)
        {
            var only = brackets.Intersect(screen);
            return new ReticleFrame(only, 0, 0, BadgePlacement.None);
        }

        // Above the target when it fits on screen (with its glow), otherwise below it.
        var aboveY = brackets.Y - BadgeGap - badgeHeight;
        var placement = aboveY - BadgeGlow >= screen.Y ? BadgePlacement.Above : BadgePlacement.Below;
        var badgeY = placement == BadgePlacement.Above ? aboveY : brackets.Bottom + BadgeGap;
        badgeY = Math.Clamp(badgeY, screen.Y + BadgeGlow, Math.Max(screen.Y + BadgeGlow, screen.Bottom - BadgeGlow - badgeHeight));

        // Centred over the target, pushed inward at the left / right screen edge (never cut off).
        var badgeX = box.CenterX - badgeWidth / 2;
        badgeX = Math.Clamp(badgeX, screen.X + BadgeGlow, Math.Max(screen.X + BadgeGlow, screen.Right - BadgeGlow - badgeWidth));

        var badge = new ScreenRect(badgeX - BadgeGlow, badgeY - BadgeGlow, badgeWidth + 2 * BadgeGlow, badgeHeight + 2 * BadgeGlow);
        var window = brackets.Union(badge).Intersect(screen);
        if (window.IsEmpty) return new ReticleFrame(window, 0, 0, BadgePlacement.None);
        return new ReticleFrame(window, badgeX - window.X, badgeY - window.Y, placement);
    }
}

/// <summary>Where the badge ended up relative to the target.</summary>
public enum BadgePlacement
{
    None,
    Above,
    Below,
}

/// <summary>
/// The reticle window (physical screen px) and the badge's top-left corner relative to that window.
/// </summary>
public readonly record struct ReticleFrame(ScreenRect Window, int BadgeLeft, int BadgeTop, BadgePlacement Placement)
{
    /// <summary>The badge rectangle on screen (physical px) for a badge of this size.</summary>
    public ScreenRect BadgeOnScreen(int badgeWidth, int badgeHeight) =>
        new(Window.X + BadgeLeft, Window.Y + BadgeTop, badgeWidth, badgeHeight);
}
