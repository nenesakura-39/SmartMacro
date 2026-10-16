using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;

namespace SmartMacro.Tests;

/// <summary>
/// Checks the KEYBDINPUT fields SmartMacro builds (wVk, wScan, dwFlags). These tests can't show what Windows or a
/// game does with them: whether SendInput accepts the event, how the active keyboard layout maps a scan code, and
/// whether a game reads injected input at all can only be checked on a real Windows PC.
/// </summary>
public class InputEncodingTests
{
    private const uint ExtendedKey = 0x0001, KeyUp = 0x0002, ScanCode = 0x0008;

    // Set 1 scan codes: left Shift 2A, right Shift 36, Ctrl 1D (right = E0 1D), Alt 38 (right = E0 38),
    // Up arrow E0 48 vs numpad 8 = 48.
    [Theory]
    [InlineData(0x2A, false)] // left Shift
    [InlineData(0x36, false)] // right Shift
    [InlineData(0x1D, false)] // left Ctrl
    [InlineData(0x1D, true)]  // right Ctrl
    [InlineData(0x38, false)] // left Alt
    [InlineData(0x38, true)]  // right Alt (AltGr)
    [InlineData(0x48, false)] // numpad 8
    [InlineData(0x48, true)]  // Up arrow
    [InlineData(0x11, false)] // W
    public void Scan_code_mode_identifies_the_key_by_scan_code(int scan, bool extended)
    {
        var press = KeyboardInputEncoder.ForScanCode(scan, extended, down: true);
        var release = KeyboardInputEncoder.ForScanCode(scan, extended, down: false);

        // wVk is ignored with KEYEVENTF_SCANCODE, so it's 0; wScan carries the key.
        Assert.Equal(0, press.VirtualKey);
        Assert.Equal(scan, press.ScanCode);
        Assert.Equal(ScanCode | (extended ? ExtendedKey : 0), press.Flags);

        // The release is the same key: same scan code and the same extended flag, plus KEYUP.
        Assert.Equal(press.ScanCode, release.ScanCode);
        Assert.Equal(press.Flags | KeyUp, release.Flags);
        Assert.True(release.IsKeyUp);
        Assert.Equal(extended, release.IsExtended);
    }

    [Theory]
    [InlineData(0xA0, 0x2A, false)] // VK_LSHIFT
    [InlineData(0xA1, 0x36, false)] // VK_RSHIFT
    [InlineData(0xA2, 0x1D, false)] // VK_LCONTROL
    [InlineData(0xA3, 0x1D, true)]  // VK_RCONTROL needs the extended flag
    [InlineData(0xA4, 0x38, false)] // VK_LMENU
    [InlineData(0xA5, 0x38, true)]  // VK_RMENU needs the extended flag
    [InlineData(0x26, 0x48, true)]  // VK_UP is extended
    [InlineData(0x68, 0x48, false)] // VK_NUMPAD8 isn't
    [InlineData(0x57, 0x11, false)] // W
    public void Virtual_key_mode_identifies_the_key_by_virtual_key(int vk, int mappedScan, bool extended)
    {
        var press = KeyboardInputEncoder.ForVirtualKey(vk, mappedScan, down: true);
        var release = KeyboardInputEncoder.ForVirtualKey(vk, mappedScan, down: false);

        Assert.Equal(vk, press.VirtualKey);
        Assert.Equal(mappedScan, press.ScanCode);
        Assert.False(press.UsesScanCode);
        Assert.Equal(extended ? ExtendedKey : 0, press.Flags);
        Assert.Equal(press.Flags | KeyUp, release.Flags);
        Assert.Equal(press.VirtualKey, release.VirtualKey);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0x2A, true)]
    [InlineData(0xFE, true)]
    [InlineData(0xFF, false)] // keyboard overrun code
    [InlineData(0x100, false)]
    [InlineData(-1, false)]
    public void Only_real_scan_codes_use_scan_code_mode(int scan, bool usable)
    {
        Assert.Equal(usable, KeyboardInputEncoder.CanUseScanCode(scan));
        if (!usable) Assert.Throws<ArgumentOutOfRangeException>(() => KeyboardInputEncoder.ForScanCode(scan, false, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Invalid_virtual_keys_are_refused(int vk)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyboardInputEncoder.ForVirtualKey(vk, 0, true));
    }

    [Fact]
    public async Task Recorded_modifiers_are_pressed_and_released_with_the_same_scan_code_and_extended_flag()
    {
        var input = new FakeInput();
        var step = new InputSequenceStep
        {
            Events =
            {
                InputEvent.KeyDown(0, 0x11, 0x1D, extended: true),   // right Ctrl
                InputEvent.KeyDown(0, 0x10, 0x2A),                   // left Shift
                InputEvent.KeyDown(0, 0x10, 0x36),                   // right Shift
                InputEvent.KeyUp(1_000, 0x10, 0x2A),
                InputEvent.KeyUp(2_000, 0x10, 0x36),
                InputEvent.KeyUp(3_000, 0x11, 0x1D, extended: true),
                InputEvent.KeyDown(4_000, 0x13, 0),                  // Pause (E1 key, recorded without scan code)
                InputEvent.KeyUp(5_000, 0x13, 0),
            },
        };

        await new PlaybackEngine(input, new FakeClock()).RunAsync(new Macro { Name = "Mods", Steps = { step } });

        Assert.Equal(new[]
        {
            "key 17 down sc29e", "key 16 down sc42", "key 16 down sc54",
            "key 16 up sc42", "key 16 up sc54", "key 17 up sc29e",
            "key 19 down", "key 19 up",
        }, input.Calls);
    }

    [Fact]
    public async Task Stopping_releases_a_held_extended_key_as_the_same_key()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call => { if (call == "key 17 down sc29e") engine.Stop(); };

        await engine.RunAsync(new Macro
        {
            Name = "Stop",
            Steps =
            {
                new InputSequenceStep
                {
                    Events = { InputEvent.KeyDown(0, 0x11, 0x1D, extended: true), InputEvent.KeyUp(9_000_000, 0x11, 0x1D, extended: true) },
                },
            },
        });

        Assert.Equal(new[] { "key 17 down sc29e", "key 17 up sc29e" }, input.Calls);
    }
}
