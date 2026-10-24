using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// The rules behind "🎥 Show overlays in OBS / Discord" (streamer mode), kept here (pure, unit tested) so the WPF overlay
/// windows only have to apply them.
///
/// Windows decides per window whether screen captures may see it ("display affinity"):
/// - <see cref="WdaExcludeFromCapture"/> (0x11, normal mode): the user sees the overlay, but every capture API – OBS,
///   Discord screen share, Xbox Game Bar, and SmartMacro's own GDI captures for Find steps – gets what is behind it.
/// - <see cref="WdaNone"/> (0x00, streamer mode): captures see the overlay like any other window, so streams and
///   recordings show the HUD, banner, reticle and highlight boxes.
///
/// In streamer mode SmartMacro's own captures can see the overlays too, so the playback reticle becomes "non-interfering":
/// it is drawn only OUTSIDE the target it marks (at least <see cref="StreamReticleClearancePx"/> px away, no fill and no
/// centre mark over the target) and fades after <see cref="StreamReticleHold"/>, so the next look at that target sees
/// the target, not the reticle.
/// </summary>
public static class OverlayCapturePolicy
{
    /// <summary>SetWindowDisplayAffinity: no restriction (captures see the window).</summary>
    public const uint WdaNone = 0x00000000;

    /// <summary>SetWindowDisplayAffinity: shown on the monitor, left out of every screen capture (Windows 10 2004+).</summary>
    public const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>How long the playback target-lock reticle stays at most in streamer mode before it fades.</summary>
    public static readonly TimeSpan StreamReticleHold = TimeSpan.FromMilliseconds(180);

    /// <summary>In streamer mode the reticle never draws within this many px of (or inside) the target rectangle.</summary>
    public const int StreamReticleClearancePx = 6;

    /// <summary>The display affinity an overlay window should get.</summary>
    public static uint AffinityFor(bool showOverlaysInRecordings) =>
        showOverlaysInRecordings ? WdaNone : WdaExcludeFromCapture;

    /// <summary>
    /// True when the overlay must be drawn so it can't disturb SmartMacro's own next look (streamer mode, and the overlay
    /// isn't left out of captures).
    /// </summary>
    public static bool NeedsNonInterferingReticle(bool showOverlaysInRecordings) => showOverlaysInRecordings;

    /// <summary>How long the playback reticle stays: unchanged normally, at most <see cref="StreamReticleHold"/> in streamer mode.</summary>
    public static TimeSpan ReticleHold(TimeSpan normalHold, bool showOverlaysInRecordings) =>
        showOverlaysInRecordings && normalHold > StreamReticleHold ? StreamReticleHold : normalHold;

    /// <summary>
    /// The area the streamer-mode reticle must keep clear: the target grown by <see cref="StreamReticleClearancePx"/> on every
    /// side. Anything drawn must lie completely outside it (see <see cref="IsClearOf"/>).
    /// </summary>
    public static ScreenRect KeepClearArea(ScreenRect target) =>
        new(target.X - StreamReticleClearancePx, target.Y - StreamReticleClearancePx,
            target.Width + 2 * StreamReticleClearancePx, target.Height + 2 * StreamReticleClearancePx);

    /// <summary>
    /// In streamer mode the reticle leaves at least this big a square (px) completely unpainted around the click point,
    /// even for a tiny target, so the target's core pixels are never tinted or covered.
    /// </summary>
    public const int StreamHollowCorePx = 24;

    /// <summary>
    /// The hollow (unpainted) area of the streamer-mode reticle: the target rectangle, grown where needed to contain a
    /// <see cref="StreamHollowCorePx"/>-px square centred on the click point (<paramref name="centerX"/>, <paramref name="centerY"/>).
    /// The reticle is drawn only outside <see cref="KeepClearArea"/> of this rectangle.
    /// </summary>
    public static ScreenRect HollowArea(ScreenRect target, int centerX, int centerY)
    {
        var half = StreamHollowCorePx / 2;
        var core = new ScreenRect(centerX - half, centerY - half, StreamHollowCorePx, StreamHollowCorePx);
        return target.IsEmpty ? core : target.Union(core);
    }

    /// <summary>True when <paramref name="drawn"/> doesn't touch the keep-clear area around <paramref name="target"/>.</summary>
    public static bool IsClearOf(ScreenRect drawn, ScreenRect target) => drawn.IsEmpty || drawn.Intersect(KeepClearArea(target)).IsEmpty;
}
