using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// Target-lock reticle badge layout: the overlay window always fits the WHOLE "🎯 98% · 45ms · Find image" badge (it used
/// to be cut off at the right edge of a small target's window), centred over the target, inside the screen.
/// </summary>
public class ReticleLayoutTests
{
    private static readonly ScreenRect FullHd = new(0, 0, 1920, 1080);

    private static bool Contains(ScreenRect outer, ScreenRect inner) => outer.Intersect(inner) == inner;

    private static ScreenRect BadgeOf(ReticleFrame frame, int width, int height) => frame.BadgeOnScreen(width, height);

    [Fact]
    public void A_small_target_gets_a_window_wide_enough_for_the_whole_badge()
    {
        var ball = new ScreenRect(860, 555, 63, 63); // the Click the Target ball
        var frame = ReticleLayout.Compute(ball, 180, 24, FullHd);
        var badge = BadgeOf(frame, 180, 24);

        Assert.Equal(BadgePlacement.Above, frame.Placement);
        Assert.True(frame.Window.Width >= 180 + 2 * ReticleLayout.BadgeGlow, $"window {frame.Window}");
        Assert.True(Contains(frame.Window, badge), $"badge {badge} must be inside window {frame.Window}");
        Assert.InRange(badge.CenterX - ball.CenterX, -1, 1);              // centred over the target
        Assert.True(badge.Bottom <= ball.Y - ReticleLayout.Pad);           // above the brackets, not on the target
        Assert.True(Contains(frame.Window, new ScreenRect(ball.X - ReticleLayout.Pad, ball.Y - ReticleLayout.Pad,
            ball.Width + 2 * ReticleLayout.Pad, ball.Height + 2 * ReticleLayout.Pad))); // brackets still fit
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void The_badge_fits_at_any_display_scaling(double scale)
    {
        int width = (int)(180 * scale), height = (int)(24 * scale);
        foreach (var target in new[]
                 {
                     new ScreenRect(860, 555, 63, 63), // middle
                     new ScreenRect(0, 500, 40, 40),   // left edge
                     new ScreenRect(1880, 500, 40, 40), // right edge
                     new ScreenRect(900, 0, 40, 40),   // top edge
                     new ScreenRect(900, 1040, 40, 40), // bottom edge
                     new ScreenRect(0, 0, 30, 30),     // top-left corner
                 })
        {
            var frame = ReticleLayout.Compute(target, width, height, FullHd);
            var badge = BadgeOf(frame, width, height);
            Assert.True(Contains(frame.Window, badge), $"target {target}: badge {badge} outside window {frame.Window}");
            Assert.True(Contains(FullHd, frame.Window), $"target {target}: window {frame.Window} off screen");
            Assert.True(badge.X >= FullHd.X + ReticleLayout.BadgeGlow && badge.Right <= FullHd.Right - ReticleLayout.BadgeGlow,
                $"target {target}: badge {badge} not pushed inside the screen");
        }
    }

    [Fact]
    public void No_room_above_puts_the_badge_below_the_target()
    {
        var target = new ScreenRect(900, 5, 40, 40);
        var frame = ReticleLayout.Compute(target, 180, 24, FullHd);
        var badge = BadgeOf(frame, 180, 24);

        Assert.Equal(BadgePlacement.Below, frame.Placement);
        Assert.True(badge.Y >= target.Bottom + ReticleLayout.Pad);
        Assert.True(OverlayCapturePolicy.IsClearOf(badge, target)); // never on the target (streamer mode needs this)
    }

    [Fact]
    public void Works_on_a_monitor_left_of_the_main_one()
    {
        var desktop = new ScreenRect(-1920, 0, 3840, 1080);
        var target = new ScreenRect(-1915, 400, 50, 50);
        var frame = ReticleLayout.Compute(target, 200, 26, desktop);
        var badge = BadgeOf(frame, 200, 26);

        Assert.True(Contains(frame.Window, badge));
        Assert.True(badge.X >= desktop.X + ReticleLayout.BadgeGlow);
    }

    [Fact]
    public void Without_a_badge_the_window_is_just_the_brackets()
    {
        var target = new ScreenRect(500, 500, 63, 63);
        var frame = ReticleLayout.Compute(target, 0, 0, FullHd);

        Assert.Equal(BadgePlacement.None, frame.Placement);
        Assert.Equal(new ScreenRect(482, 482, 99, 99), frame.Window);
    }

    [Fact]
    public void The_badge_never_touches_the_target_above_or_below()
    {
        foreach (var target in new[] { new ScreenRect(860, 555, 63, 63), new ScreenRect(860, 3, 63, 63) })
        {
            var frame = ReticleLayout.Compute(target, 180, 24, FullHd);
            Assert.True(OverlayCapturePolicy.IsClearOf(BadgeOf(frame, 180, 24), target), $"target {target}");
        }
    }
}
