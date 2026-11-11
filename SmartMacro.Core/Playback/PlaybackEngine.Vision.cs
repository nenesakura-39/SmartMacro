using System.Diagnostics;
using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    // ---------------- Screen search (Find text / image / color) ----------------

    /// <summary>
    /// The shared waiting loop of the Find steps: looks at the screen until the goal is met (it appeared / it's gone)
    /// or the timeout passes. Timeouts and check intervals are real time (not affected by the Speed setting), and time
    /// spent paused doesn't count. Pause / Stop / Esc are honoured between and during checks.
    /// The looking itself is done by <see cref="ScreenProbes"/>, which conditions use too.
    /// </summary>
    private async Task SearchAsync(ScreenSearchStep step, CancellationToken token)
    {
        var search = (_probes ?? throw new InvalidOperationException("Playback isn't running.")).Prepare(step);
        var what = search.What;
        var action = step.EffectiveFailureAction;
        var maxRetries = action == StepFailureAction.Retry
            ? Math.Clamp(step.FailurePolicy?.RetryCount ?? 2, 1, Validation.MacroValidator.MaxRetryCount)
            : 0;
        var retryDelayMs = Math.Max(0, step.FailurePolicy?.RetryDelayMs ?? 500);

        for (var attempt = 0; ; attempt++)
        {
            var timer = search.NewTimer();
            var started = _clock.Elapsed;
            var shiftAtStart = _timelineShift;
            var timeout = TimeSpan.FromMilliseconds(step.TimeoutMs);
            TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);
            string Stats() => timer is null ? string.Empty : " " + timer.Describe(Elapsed());

            var confirmTarget = Math.Clamp(step.ConfirmFrames <= 0 ? 1 : step.ConfirmFrames, 1, 10);
            var confirmHits = 0;
            string? missMessage = null;
            while (true)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var result = await search.LookAsync(timer, token).ConfigureAwait(false);
                if (search.IsTimed) RecordProbeTime(result.ElapsedMs);

                if (step.Goal == WaitGoal.Appear && result.Found)
                {
                    confirmHits++;
                    if (confirmHits < confirmTarget)
                    {
                        var confirmInterval = Math.Clamp(step.CheckIntervalMs, 8, 25);
                        await WaitAsync(confirmInterval, token).ConfigureAwait(false);
                        continue;
                    }

                    RememberFound(result.X, result.Y, result.Match);
                    ReportTarget(result.X, result.Y, result.Match, result.Confidence, StepDescriber.DisplayName(step.Kind), result.ElapsedMs,
                        result.FromHotSpot, result.AllMatches);
                    Message?.Invoke(this, result.FoundText + Stats());

                    var held = _held ?? throw new InvalidOperationException("Playback isn't running.");
                    var timing = FoundActionTiming.Of(step);
                    var lastActedX = result.X;
                    var lastActedY = result.Y;

                    if (step.TargetSelection == SearchMatchMode.AllMatches
                        && step.Action != FoundTextAction.None
                        && result.EffectiveMatches is { Count: > 1 } multiMatches)
                    {
                        var limit = step.MaxMatchesToActOn > 0 ? Math.Min(step.MaxMatchesToActOn, multiMatches.Count) : multiMatches.Count;
                        for (var i = 0; i < limit; i++)
                        {
                            await WaitIfPausedAsync(token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();

                            var mRect = multiMatches[i];
                            var (mx, my) = (mRect.CenterX, mRect.CenterY);
                            RememberFound(mx, my, mRect);
                            lastActedX = mx;
                            lastActedY = my;

                            await PerformFoundActionAsync(step.Action, mx + step.OffsetX, my + step.OffsetY, step.ActionHoldMs,
                                timing, held, token, mRect).ConfigureAwait(false);

                            if (i + 1 < limit && step.BetweenMatchesDelayMs > 0)
                            {
                                await WaitAsync(Vary(step.BetweenMatchesDelayMs, HumanizeMinDelayMs), token).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        await PerformFoundActionAsync(step.Action, result.X + step.OffsetX, result.Y + step.OffsetY, step.ActionHoldMs,
                            timing, held, token, result.Match).ConfigureAwait(false);
                    }

                    if (step.MoveCursorAwayAfterAction && step.Action is not (FoundTextAction.None or FoundTextAction.Move))
                    {
                        // "Park" the cursor so it (and its hover effect) doesn't cover the spot where the next button appears.
                        var (awayX, awayY) = AwaySpot(step, lastActedX, lastActedY, result.Area);
                        await MoveCursorAsync(awayX, awayY, step.MoveStyle, step.MoveDurationMs, 1.0, onTimeline: false, token).ConfigureAwait(false);
                    }

                    return;
                }

                confirmHits = 0;

                if (step.Goal == WaitGoal.Disappear && !result.Found)
                {
                    Message?.Invoke(this, $"{what} is no longer on screen." + Stats());
                    return;
                }

                if (Elapsed() >= timeout)
                {
                    var seconds = timeout.TotalSeconds;
                    if (step.Goal == WaitGoal.Appear)
                    {
                        missMessage = step.TimeoutMs == 0
                            ? $"{what} wasn't found on screen."
                            : $"{what} didn't appear within {seconds:0.#} s.";
                        missMessage += result.MissText;
                    }
                    else
                    {
                        missMessage = step.TimeoutMs == 0
                            ? $"{what} is still on screen."
                            : $"{what} was still on screen after {seconds:0.#} s.";
                        missMessage += " " + result.FoundText;
                    }

                    missMessage += Stats();
                    break;
                }

                var pollMs = step.Strategy == VisionSearchStrategy.Fastest
                    ? Math.Min(step.CheckIntervalMs, 16)
                    : step.CheckIntervalMs;
                await WaitAsync(pollMs, token).ConfigureAwait(false);
            }

            if (attempt < maxRetries)
            {
                Message?.Invoke(this, $"{missMessage} Retrying ({attempt + 1}/{maxRetries})…");
                if (retryDelayMs > 0) await WaitAsync(retryDelayMs, token).ConfigureAwait(false);
                continue;
            }

            await ApplyFinalFailurePolicyAsync(action, step.FailurePolicy, missMessage!, forgetFound: step.Goal == WaitGoal.Appear, token)
                .ConfigureAwait(false);
            return;
        }
    }

    // ---------------- Wait for screen change / Focus window / Take screenshot ----------------

    /// <summary>The file the latest Take screenshot step saved (null before the first one).</summary>
    public string? LastScreenshotPath { get; private set; }

    /// <summary>
    /// "Wait for screen change": takes a starting picture of the area, then looks again every check interval until enough
    /// of it differs. The centre of the changed pixels becomes the last found spot (and gets the step's action). Time spent
    /// paused doesn't count, and after a pause the starting picture is taken again (the screen may have changed while
    /// you were doing something else).
    /// </summary>
    private async Task WaitForChangeAsync(WaitForChangeStep step, HeldInput held, CancellationToken token)
    {
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Wait for screen change steps can't run.");
        var area = step.Region;
        var action = step.EffectiveFailureAction;
        var maxRetries = action == StepFailureAction.Retry
            ? Math.Clamp(step.FailurePolicy?.RetryCount ?? 2, 1, Validation.MacroValidator.MaxRetryCount)
            : 0;
        var retryDelayMs = Math.Max(0, step.FailurePolicy?.RetryDelayMs ?? 500);

        for (var attempt = 0; ; attempt++)
        {
            var started = _clock.Elapsed;
            var shiftAtStart = _timelineShift;
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, step.TimeoutMs));
            TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);

            await WaitIfPausedAsync(token).ConfigureAwait(false);
            await ClearTargetOverlayAsync(token).ConfigureAwait(false); // the reticle must not be in (or vanish from) the starting picture
            var baseline = await capture.CaptureAsync(area, token).ConfigureAwait(false);
            var best = default(ScreenChange);
            string? missMessage = null;

            while (true)
            {
                var shiftBeforeWait = _timelineShift;
                await WaitAsync(Math.Max(1, step.CheckIntervalMs), token).ConfigureAwait(false);
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var lookStarted = _clock.Elapsed;
                var frame = await capture.CaptureAsync(area, token).ConfigureAwait(false);
                if (_timelineShift != shiftBeforeWait || frame.Bounds != baseline.Bounds)
                {
                    // Paused (or the area moved partly off screen): start comparing from what is there now.
                    if (_timelineShift != shiftBeforeWait && await ClearTargetOverlayAsync(token).ConfigureAwait(false))
                        frame = await capture.CaptureAsync(area, token).ConfigureAwait(false);
                    baseline = frame;
                }
                else
                {
                    var change = ScreenChangeDetector.Compare(baseline, frame, step.PixelTolerance);
                    RecordProbeTime((_clock.Elapsed - lookStarted).TotalMilliseconds);
                    if (change.ChangedPercent > best.ChangedPercent) best = change;
                    if (change.Any && change.ChangedPercent >= step.MinChangedPercent)
                    {
                        var (x, y) = (change.CenterX, change.CenterY);
                        RememberFound(x, y, change.Bounds);
                        ReportTarget(x, y, change.Bounds, 1.0, StepDescriber.DisplayName(step.Kind));
                        Message?.Invoke(this, string.Format(CultureInfo.InvariantCulture,
                            "The watched area changed: {0:0.##} % of its pixels, around ({1}, {2}).", change.ChangedPercent, x, y));
                        await PerformFoundActionAsync(step.Action, x, y, step.ActionHoldMs, FoundActionTiming.Snappy, held, token, change.Bounds)
                            .ConfigureAwait(false);
                        return;
                    }
                }

                if (Elapsed() >= timeout)
                {
                    missMessage = string.Format(CultureInfo.InvariantCulture,
                        "The watched area didn't change {0} (largest change: {1:0.##} % of its pixels, needed {2:0.##} %).",
                        step.TimeoutMs == 0 ? "when checked" : string.Format(CultureInfo.InvariantCulture, "within {0:0.#} s", timeout.TotalSeconds),
                        best.ChangedPercent, step.MinChangedPercent);
                    break;
                }
            }

            if (attempt < maxRetries)
            {
                Message?.Invoke(this, $"{missMessage} Retrying ({attempt + 1}/{maxRetries})…");
                if (retryDelayMs > 0) await WaitAsync(retryDelayMs, token).ConfigureAwait(false);
                continue;
            }

            await ApplyFinalFailurePolicyAsync(action, step.FailurePolicy, missMessage!, forgetFound: true, token)
                .ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Asks the app to hide the target-lock reticle now (see <see cref="TargetOverlayClearRequested"/>); when one was hidden,
    /// waits <see cref="OverlayClearSettleMs"/> so the next capture shows the screen without it. Returns true when it hid one.
    /// </summary>
    private async Task<bool> ClearTargetOverlayAsync(CancellationToken token)
    {
        if (TargetOverlayClearRequested is not { } handler) return false;
        var request = new OverlayClearRequest();
        try
        {
            handler(this, request);
        }
        catch (Exception ex)
        {
            Message?.Invoke(this, $"Target overlay error: {ex.Message}");
            return false;
        }

        if (!request.Cleared) return false;
        await WaitAsync(OverlayClearSettleMs, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>How often "Focus window" looks for a window that isn't open yet.</summary>
    private const int FocusWindowPollMs = 250;

    /// <summary>
    /// "Focus window": finds the window by (part of) its title (waiting for it up to the step's time), restores it when
    /// minimized and brings it to the front, then pauses briefly so the app is ready for the next input.
    /// </summary>
    private async Task FocusWindowAsync(FocusWindowStep step, CancellationToken token)
    {
        var layout = _layout ?? throw new InvalidOperationException("The window list isn't available, so Focus window steps can't run.");
        var action = step.EffectiveFailureAction;
        var maxRetries = action == StepFailureAction.Retry
            ? Math.Clamp(step.FailurePolicy?.RetryCount ?? 2, 1, Validation.MacroValidator.MaxRetryCount)
            : 0;
        var retryDelayMs = Math.Max(0, step.FailurePolicy?.RetryDelayMs ?? 500);

        for (var attempt = 0; ; attempt++)
        {
            var title = InterpolateActive(step.WindowTitle).Trim();
            var started = _clock.Elapsed;
            var shiftAtStart = _timelineShift;
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, step.WaitForWindowMs));
            TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);

            string? missMessage = null;
            while (true)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (SearchArea.FindWindowByTitleOrApp(layout.GetWindows(), title) is { } window)
                {
                    if (layout is not IWindowFocuser focuser)
                    {
                        Message?.Invoke(this, $"Found the window \"{window.Title}\", but this SmartMacro can't bring windows to the front.");
                        return;
                    }

                    var focused = focuser.TryFocus(window, step.RestoreIfMinimized);
                    if (step.SnapLayout != WindowSnapLayout.KeepCurrent)
                    {
                        var primary = layout.GetMonitors().FirstOrDefault(m => m.IsPrimary)?.Bounds
                                      ?? layout.GetMonitors().FirstOrDefault()?.Bounds
                                      ?? new ScreenRect(0, 0, 1920, 1080);
                        focuser.TrySnap(window, step.SnapLayout, primary);
                    }
                    Message?.Invoke(this, focused
                        ? $"Brought the window \"{window.Title}\" to the front."
                        : $"Found the window \"{window.Title}\", but Windows didn't let SmartMacro bring it to the front (click it once by hand, or run SmartMacro as the same user / admin level as the game).");
                    if (step.SettleMs > 0) await WaitAsync(step.SettleMs, token).ConfigureAwait(false);
                    return;
                }

                if (Elapsed() >= timeout)
                {
                    missMessage = step.WaitForWindowMs == 0
                        ? $"No open window has \"{title}\" in its title or app name."
                        : string.Format(CultureInfo.InvariantCulture, "No window with \"{0}\" in its title or app name opened within {1:0.#} s.", title, timeout.TotalSeconds);
                    break;
                }

                await WaitAsync(Math.Min(FocusWindowPollMs, Math.Max(1, (timeout - Elapsed()).TotalMilliseconds)), token).ConfigureAwait(false);
            }

            if (attempt < maxRetries)
            {
                Message?.Invoke(this, $"{missMessage} Retrying ({attempt + 1}/{maxRetries})…");
                if (retryDelayMs > 0) await WaitAsync(retryDelayMs, token).ConfigureAwait(false);
                continue;
            }

            await ApplyFinalFailurePolicyAsync(action, step.FailurePolicy, missMessage!, forgetFound: false, token)
                .ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// "Take screenshot": captures the main monitor / all monitors / an area / an app window and saves it as a PNG. A
    /// screenshot that can't be taken or saved is only reported: the macro carries on (it's a log, not a goal).
    /// </summary>
    private async Task TakeScreenshotAsync(ScreenshotStep step, CancellationToken token)
    {
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Take screenshot steps can't run.");
        ScreenRect area;
        try
        {
            var titleOverride = step.Area == ScreenshotArea.Window ? InterpolateActive(step.WindowTitle) : null;
            area = ScreenshotFiles.ResolveArea(step, capture.GetVirtualScreen(), _layout, titleOverride);
        }
        catch (SearchAreaUnavailableException ex)
        {
            Message?.Invoke(this, $"📸 No screenshot taken: {ex.Message}");
            return;
        }

        ScreenFrame frame;
        try
        {
            frame = await capture.CaptureAsync(area, token).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            Message?.Invoke(this, $"📸 No screenshot taken: {ex.Message}");
            return;
        }

        var folder = ScreenshotFiles.FolderOf(step);
        var prefix = InterpolateActive(step.FileNamePrefix);
        var now = DateTime.Now;
        try
        {
            var path = await Task.Run(() => ScreenshotFiles.Save(frame.Image, folder, prefix, now), token).ConfigureAwait(false);
            LastScreenshotPath = path;
            Message?.Invoke(this, $"📸 Screenshot saved: {path} ({frame.Bounds.Width}×{frame.Bounds.Height})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Message?.Invoke(this, $"📸 Couldn't save the screenshot in \"{folder}\": {ex.Message}");
        }

        if (step.SoundCue != SoundCueKind.None)
        {
            try
            {
                SoundCuePlayer?.Invoke(step.SoundCue);
            }
            catch
            {
                // Sound cue errors never interrupt playback.
            }
        }

        var noteText = !string.IsNullOrWhiteSpace(step.NotificationMessage)
            ? InterpolateActive(step.NotificationMessage).Trim()
            : string.Empty;
        if (noteText.Length > 0)
        {
            Message?.Invoke(this, $"🔔 {noteText}");
        }

        if (!string.IsNullOrWhiteSpace(step.WebhookUrl))
        {
            var webhookMsg = noteText.Length > 0
                ? noteText
                : $"📸 SmartMacro screenshot saved ({prefix}) at {now:HH:mm:ss}";
            await TryPostWebhookAsync(step.WebhookUrl.Trim(), webhookMsg, token).ConfigureAwait(false);
        }
    }

    private static readonly System.Net.Http.HttpClient WebhookHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private DateTimeOffset _lastWebhookSentAt = DateTimeOffset.MinValue;

    private async Task TryPostWebhookAsync(string webhookUrl, string messageText, CancellationToken token)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastWebhookSentAt).TotalSeconds < 2.0)
        {
            return; // Rate-limit to at most 1 webhook every 2 seconds.
        }

        _lastWebhookSentAt = now;
        try
        {
            var escaped = messageText
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            var payload = $"{{\"content\":\"{escaped}\",\"text\":\"{escaped}\"}}";
            using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await WebhookHttpClient.PostAsync(uri, content, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Message?.Invoke(this, "🌐 Webhook notification sent.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Message?.Invoke(this, $"🌐 Webhook skipped: {ex.Message}");
        }
    }

    /// <summary>How far below the clicked spot the cursor goes when the step searches the whole screen.</summary>
    public const int MoveAwayDistance = 150;

    /// <summary>
    /// Where "move the cursor away after clicking" puts the cursor: just outside the bottom-right corner of the search
    /// area (a custom area, or the app window that was searched), or — when a whole monitor / all monitors are searched —
    /// <see cref="MoveAwayDistance"/> pixels below the spot that was found.
    /// </summary>
    internal static (int X, int Y) AwaySpot(ScreenSearchStep step, int foundX, int foundY, ScreenRect searchedArea = default)
    {
        switch (step.EffectiveScope)
        {
            case SearchScope.CustomArea:
                return (step.RegionX + step.RegionWidth + 8, step.RegionY + step.RegionHeight + 8);
            case SearchScope.Window when !searchedArea.IsEmpty:
                return (searchedArea.Right + 8, searchedArea.Bottom + 8);
            default:
                return (foundX, foundY + MoveAwayDistance);
        }
    }

    /// <summary>
    /// How a "when found" / "when true" action moves and clicks: movement style + glide time, click hold, double-click
    /// gap and the wait after the click. If steps (which have no such settings) use the defaults: jump, 35 / 65 / 120 ms.
    /// </summary>
    private readonly record struct FoundActionTiming(
        MouseMoveStyle? Style,
        int MoveMs,
        int HoldMs,
        int DoubleGapMs,
        int SettleMs,
        int FoundKey = 0x20,
        string? FoundText = null,
        int ScrollClicks = 3)
    {
        public static FoundActionTiming Default => new(null, 0, FoundClickHoldMs, DoubleClickGapMs, ClickSettleMs);

        /// <summary>Wait for screen change (no click settings of its own): jump, 6 ms rest, 12 ms hold – for things that move fast.</summary>
        public static FoundActionTiming Snappy => new(null, 0, MinTargetClickHoldMs, DoubleClickGapMs, ClickSettleMs);

        public static FoundActionTiming Of(ScreenSearchStep step) =>
            new(step.MoveStyle, step.MoveDurationMs, step.ClickHoldMs, step.DoubleClickIntervalMs, step.PostActionDelayMs,
                step.FoundVirtualKey <= 0 ? 0x20 : step.FoundVirtualKey, step.FoundTextToType, step.FoundScrollClicks <= 0 ? 3 : step.FoundScrollClicks);
    }

    /// <summary>
    /// The "when found" action of a Find step (and the "when true" action of an If) at a spot on screen (offset already
    /// added): move there, click / double-click / right-click, or hold the left button for <paramref name="holdMs"/>.
    /// The hold is tracked, so Stop / Esc / pause release it. Clicks and holds are followed by a short settle so the
    /// clicked window can react before the next step.
    /// </summary>
    private async Task PerformFoundActionAsync(FoundTextAction action, int x, int y, int holdMs, FoundActionTiming timing, HeldInput held,
        CancellationToken token, ScreenRect matchBounds = default)
    {
        if (action == FoundTextAction.None) return;
        if (action == FoundTextAction.PressKey)
        {
            var vk = timing.FoundKey <= 0 ? 0x20 : timing.FoundKey;
            var keyHold = Math.Max(MinTargetClickHoldMs, timing.HoldMs);
            var hk = new HeldKey(vk);
            _input.Key(vk, true);
            held.PressKey(hk);
            await WaitAsync(keyHold, token).ConfigureAwait(false);
            _input.Key(vk, false);
            held.ReleaseKey(hk);
            await WaitAsync(Math.Max(0, timing.SettleMs), token).ConfigureAwait(false);
            return;
        }

        if (action != FoundTextAction.Move) (x, y) = Jitter(x, y, matchBounds); // Humanize: stays inside what was found

        await MoveCursorAsync(x, y, timing.Style, timing.MoveMs, 1.0, onTimeline: false, token).ConfigureAwait(false);

        // Reliable game click: the cursor rests on the target for a moment before the press (games read the cursor once
        // per frame), and the button stays down at least MinTargetClickHoldMs.
        if (action != FoundTextAction.Move) await WaitAsync(TargetHoverMs, token).ConfigureAwait(false);
        var clickHold = Math.Max(MinTargetClickHoldMs, timing.HoldMs);
        switch (action)
        {
            case FoundTextAction.Click:
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.HoverThenClick:
                await WaitAsync(Math.Max(60, holdMs > 0 && holdMs != ScreenSearchStep.DefaultActionHoldMs ? holdMs : 80), token).ConfigureAwait(false);
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.DoubleClick:
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                await WaitAsync(Math.Max(0, timing.DoubleGapMs), token).ConfigureAwait(false);
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.RightClick:
                await HeldClickAsync(MacroMouseButton.Right, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.MiddleClick:
                await HeldClickAsync(MacroMouseButton.Middle, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.Hold:
                _input.MouseButton(MacroMouseButton.Left, true);
                held.Buttons.Add(MacroMouseButton.Left);
                await WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, holdMs)), token).ConfigureAwait(false);
                _input.MouseButton(MacroMouseButton.Left, false);
                held.Buttons.Remove(MacroMouseButton.Left);
                break;
            case FoundTextAction.ClickAndPressKey:
            {
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                await WaitAsync(35, token).ConfigureAwait(false);
                var vk = timing.FoundKey <= 0 ? 0x20 : timing.FoundKey;
                var hk = new HeldKey(vk);
                _input.Key(vk, true);
                held.PressKey(hk);
                await WaitAsync(clickHold, token).ConfigureAwait(false);
                _input.Key(vk, false);
                held.ReleaseKey(hk);
                break;
            }
            case FoundTextAction.ClickAndTypeText:
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                await WaitAsync(45, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(timing.FoundText))
                    _input.TypeCharacters(InterpolateActive(timing.FoundText));
                break;
            case FoundTextAction.ScrollUp:
                _input.Scroll(Math.Max(1, timing.ScrollClicks) * 120, horizontal: false);
                break;
            case FoundTextAction.ScrollDown:
                _input.Scroll(-Math.Max(1, timing.ScrollClicks) * 120, horizontal: false);
                break;
        }

        if (action != FoundTextAction.Move) await WaitAsync(Math.Max(0, timing.SettleMs), token).ConfigureAwait(false);
    }

    /// <summary>Largest Smart-Aim search distance (px each way).</summary>
    public const int MaxSmartAimRadiusPx = SmartAim.MaxRadiusPx;

    /// <summary>Logged (as a warning) when a click "on the last found spot" runs before anything was found.</summary>
    public const string SkippedClickNoTarget = "⚠ Skipped click at Last Found Target because no prior step has found a target in this run.";

    /// <summary>Logged (as a warning) when a move "to the last found spot" runs before anything was found.</summary>
    public const string SkippedMoveNoTarget = "⚠ Skipped move to Last Found Target because no prior step has found a target in this run.";

    /// <summary>
    /// Smart-Aim (Smart Click Snapshots): looks for the click's recorded thumbnail in a small area around where it was
    /// recorded (the thumbnail's rectangle grown by SmartAimRadiusPx on every side) and returns the same spot on it, so
    /// a button that moved a bit is still hit. When several copies are found, the one nearest the recorded spot wins.
    /// Not found (or no screen capture / a damaged picture) = the recorded X / Y. Never throws except for Stop.
    /// Returns the matched rectangle too (empty when not found), so Humanize keeps its jitter inside the target.
    /// </summary>
    private async Task<((int X, int Y) Spot, ScreenRect Bounds)> SmartAimAsync(MouseClickStep c, CancellationToken token)
    {
        (int X, int Y) recorded = (c.X, c.Y);
        if (_capture is not { } capture)
        {
            NoteSmartAim(c, "Smart-Aim: screen capture isn't available, so the click uses its recorded spot.");
            return (recorded, default);
        }

        // Decoded once per picture and shared (TemplateCache), so a Smart-Aim click inside a fast loop doesn't decode its
        // PNG again every turn.
        var template = SmartClickConversion.TryDecodeThumbnail(c, out var error);
        if (template is null)
        {
            NoteSmartAim(c, $"Smart-Aim: the recorded picture can't be used ({error}); clicking the recorded spot.");
            return (recorded, default);
        }

        var started = Stopwatch.GetTimestamp();
        var radius = SmartAim.RadiusOf(c);
        var region = SmartAim.SearchArea(c, template);

        ScreenFrame frame;
        try
        {
            frame = await capture.CaptureAsync(region, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NoteSmartAim(c, $"Smart-Aim: couldn't look at the screen ({ex.Message}); clicking the recorded spot.");
            return (recorded, default);
        }

        if (frame.Image.Width < template.Width || frame.Image.Height < template.Height)
        {
            NoteSmartAim(c, "Smart-Aim: the area around the click is off screen; clicking the recorded spot.");
            return (recorded, default);
        }

        var (hit, bestScore) = SmartAim.Locate(c, template, frame, token);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RecordProbeTime(elapsedMs);
        if (hit is null)
        {
            NoteSmartAim(c, $"Smart-Aim: the clicked target wasn't found within {radius} px (best {bestScore:P0}, needs {SmartAim.OptionsFor(c).MinScore:P0}); clicking the recorded spot ({c.X}, {c.Y}).");
            return (recorded, default);
        }

        ReportTarget(hit.Bounds.CenterX, hit.Bounds.CenterY, hit.Bounds, hit.Score, "Smart-Aim", elapsedMs);
        NoteSmartAim(c, !hit.Moved
            ? $"Smart-Aim: the clicked target is where it was recorded ({hit.Score:P0} match)."
            : $"Smart-Aim: the clicked target moved by ({hit.ShiftX:+0;-0;0}, {hit.ShiftY:+0;-0;0}) px; clicking it at ({hit.X}, {hit.Y}) ({hit.Score:P0} match).");
        return ((hit.X, hit.Y), hit.Bounds);
    }

    /// <summary>Logs a Smart-Aim / Smart Intent note, but not the same note for the same step twice in a row (clicks inside loops).</summary>
    private void NoteSmartAim(MouseClickStep c, string note)
    {
        if (_smartAimLastNote.TryGetValue(c.Id, out var last) && last == note) return;
        _smartAimLastNote[c.Id] = note;
        Message?.Invoke(this, note);
    }

    /// <summary>
    /// v1.1 Smart Intent: resolves the click target via <see cref="TargetResolver"/> (UI Automation → OCR text →
    /// Multi-scale snapshot → Window-relative → Coordinates), flashes the target reticle when a non-coordinate tier hits,
    /// and logs which strategy succeeded.
    /// </summary>
    private async Task<((int X, int Y) Spot, ScreenRect Bounds)> SmartIntentAsync(MouseClickStep c, CancellationToken token)
    {
        var resolver = new TargetResolver(_uiLocator, _ocr, _capture, _layout);
        var resolution = await resolver.ResolveAsync(c, token).ConfigureAwait(false);
        RecordProbeTime(resolution.TotalElapsedMs);

        if (resolution.WinningStrategy != LocatorStrategyKind.Coordinates)
        {
            RecordSelfHealCandidate(c, resolution);
            var badge = resolution.WinningStrategy switch
            {
                LocatorStrategyKind.UiAutomation => "Intent · UI",
                LocatorStrategyKind.OcrText => "Intent · OCR",
                LocatorStrategyKind.ImageSnapshot => "Intent · Image",
                LocatorStrategyKind.WindowRelative => "Intent · Window",
                _ => "Intent",
            };
            ReportTarget(
                resolution.Bounds.CenterX,
                resolution.Bounds.CenterY,
                resolution.Bounds,
                resolution.Confidence,
                badge,
                resolution.TotalElapsedMs);
            NoteSmartAim(c, $"🧭 Intent: {resolution.Summary} ({resolution.TotalElapsedMs:0.#} ms).");
            return ((resolution.X, resolution.Y), resolution.Bounds);
        }

        NoteSmartAim(c, $"🧭 Intent: fell back to recorded coordinates ({c.X}, {c.Y}).");
        return ((c.X, c.Y), default);
    }
}
