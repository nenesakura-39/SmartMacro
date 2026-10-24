using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// "🎥 Show overlays in OBS / Discord" (streamer mode): the setting itself and the capture rules the overlay windows
/// apply (<see cref="OverlayCapturePolicy"/>). The windows themselves (SetWindowDisplayAffinity) are WPF / Win32 and are
/// checked by hand: OBS "Display Capture" while a macro plays, with the switch off and on.
/// </summary>
public class StreamerModeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Overlays_stay_out_of_recordings_by_default()
    {
        Assert.False(new AppSettings().ShowOverlaysInObsAndRecording);
        Assert.True(new AppSettings { ShowOverlaysInObsAndRecording = true }.Clone().ShowOverlaysInObsAndRecording);
    }

    [Fact]
    public async Task The_setting_round_trips_through_settings_json()
    {
        var store = new JsonSettingsStore(_dir.Combine("settings.json"));

        await store.SaveAsync(new AppSettings { ShowOverlaysInObsAndRecording = true });
        var loaded = await store.LoadAsync();

        Assert.Null(loaded.Warning);
        Assert.True(loaded.Settings.ShowOverlaysInObsAndRecording);
        Assert.Contains("\"showOverlaysInObsAndRecording\": true", await File.ReadAllTextAsync(_dir.Combine("settings.json")));

        await store.SaveAsync(new AppSettings { ShowOverlaysInObsAndRecording = false });
        Assert.False((await store.LoadAsync()).Settings.ShowOverlaysInObsAndRecording);
    }

    [Fact]
    public async Task Settings_files_from_before_the_option_keep_overlays_out_of_recordings()
    {
        var path = _dir.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 3, "showRunOverlay": true }""");

        var result = await new JsonSettingsStore(path).LoadAsync();

        Assert.Null(result.Warning);
        Assert.False(result.Settings.ShowOverlaysInObsAndRecording);
        Assert.True(result.Settings.ShowRunOverlay);
    }

    [Fact]
    public void Display_affinity_follows_the_switch()
    {
        Assert.Equal(0x11u, OverlayCapturePolicy.WdaExcludeFromCapture);
        Assert.Equal(0x00u, OverlayCapturePolicy.WdaNone);
        Assert.Equal(OverlayCapturePolicy.WdaExcludeFromCapture, OverlayCapturePolicy.AffinityFor(showOverlaysInRecordings: false));
        Assert.Equal(OverlayCapturePolicy.WdaNone, OverlayCapturePolicy.AffinityFor(showOverlaysInRecordings: true));
    }

    [Fact]
    public void The_playback_reticle_fades_after_180_ms_in_streamer_mode()
    {
        var normal = TimeSpan.FromMilliseconds(350);

        Assert.Equal(normal, OverlayCapturePolicy.ReticleHold(normal, showOverlaysInRecordings: false));
        Assert.Equal(TimeSpan.FromMilliseconds(180), OverlayCapturePolicy.ReticleHold(normal, showOverlaysInRecordings: true));
        Assert.Equal(TimeSpan.FromMilliseconds(100), OverlayCapturePolicy.ReticleHold(TimeSpan.FromMilliseconds(100), true)); // never longer
        Assert.True(OverlayCapturePolicy.NeedsNonInterferingReticle(true));
        Assert.False(OverlayCapturePolicy.NeedsNonInterferingReticle(false));
    }

    [Fact]
    public void The_streamer_mode_reticle_never_touches_the_target()
    {
        var target = new ScreenRect(100, 200, 40, 30);
        var clear = OverlayCapturePolicy.KeepClearArea(target);

        Assert.Equal(new ScreenRect(94, 194, 52, 42), clear);
        Assert.False(OverlayCapturePolicy.IsClearOf(new ScreenRect(110, 210, 5, 5), target));  // over the target
        Assert.False(OverlayCapturePolicy.IsClearOf(new ScreenRect(96, 195, 60, 1), target));  // 5 px above: too close
        Assert.True(OverlayCapturePolicy.IsClearOf(new ScreenRect(92, 186, 60, 2), target));   // a box edge 8 px out
        Assert.True(OverlayCapturePolicy.IsClearOf(new ScreenRect(80, 200, 6, 30), target));   // a bracket on the left
        Assert.True(OverlayCapturePolicy.IsClearOf(default, target));
    }
}
