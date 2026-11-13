using SmartMacro.Core.Models;

namespace SmartMacro.Core.Playback;

/// <summary>A key that is currently pressed, and how it was pressed (so it is released the same way).</summary>
/// <param name="ScanMode">True when it was pressed with <see cref="IInputSimulator.KeyWithScanCode"/> (recorded game input).</param>
public readonly record struct TrackedKey(int VirtualKey, int ScanCode = 0, bool Extended = false, bool ScanMode = false);

/// <summary>A background mouse button currently pressed on a specific target window handle.</summary>
public readonly record struct TrackedBackgroundMouseButton(nint WindowHandle, MacroMouseButton Button, int ClientX = 0, int ClientY = 0);

/// <summary>A background key currently pressed on a specific target window handle.</summary>
public readonly record struct TrackedBackgroundKey(nint WindowHandle, int VirtualKey, int ScanCode = 0, bool Extended = false);

/// <summary>
/// Remembers which keys and mouse buttons an input simulator has pressed and not released yet (in press order).
/// Used by the Windows input simulator (and <see cref="TrackingInputSimulator"/>) to implement <see cref="IInputSimulator.ReleaseAllHeldInputs"/>:
/// the failsafe that lets go of everything when playback stops for any reason. Thread-safe (Stop can come from the UI
/// thread while the playback thread is still sending input).
/// </summary>
public sealed class HeldInputTracker
{
    private readonly object _gate = new();
    private readonly List<TrackedKey> _keys = new();
    private readonly List<MacroMouseButton> _buttons = new();
    private readonly List<TrackedBackgroundKey> _backgroundKeys = new();
    private readonly List<TrackedBackgroundMouseButton> _backgroundButtons = new();

    /// <summary>True when nothing is held (foreground or background).</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count == 0
                    && _buttons.Count == 0
                    && _backgroundKeys.Count == 0
                    && _backgroundButtons.Count == 0;
            }
        }
    }

    /// <summary>The keys held right now, oldest press first.</summary>
    public IReadOnlyList<TrackedKey> HeldKeys
    {
        get { lock (_gate) return _keys.ToList(); }
    }

    /// <summary>The mouse buttons held right now, oldest press first.</summary>
    public IReadOnlyList<MacroMouseButton> HeldButtons
    {
        get { lock (_gate) return _buttons.ToList(); }
    }

    /// <summary>Background mouse buttons held right now, oldest press first.</summary>
    public IReadOnlyList<TrackedBackgroundMouseButton> HeldBackgroundButtons
    {
        get { lock (_gate) return _backgroundButtons.ToList(); }
    }

    /// <summary>Background keys held right now, oldest press first.</summary>
    public IReadOnlyList<TrackedBackgroundKey> HeldBackgroundKeys
    {
        get { lock (_gate) return _backgroundKeys.ToList(); }
    }

    /// <summary>A virtual-key press / release was sent.</summary>
    public void NoteKey(int virtualKey, bool down) => Note(new TrackedKey(virtualKey), down);

    /// <summary>A scan-code press / release was sent (recorded game input).</summary>
    public void NoteScanKey(int virtualKey, int scanCode, bool extended, bool down) =>
        Note(new TrackedKey(virtualKey, scanCode, extended, ScanMode: true), down);

    /// <summary>A mouse button press / release was sent.</summary>
    public void NoteButton(MacroMouseButton button, bool down)
    {
        lock (_gate)
        {
            _buttons.Remove(button);
            if (down) _buttons.Add(button);
        }
    }

    /// <summary>A background mouse button down / up was posted to <paramref name="windowHandle"/>.</summary>
    public void NoteBackgroundButton(nint windowHandle, MacroMouseButton button, int clientX, int clientY, bool down)
    {
        if (windowHandle == 0) return;
        lock (_gate)
        {
            _backgroundButtons.RemoveAll(b => b.WindowHandle == windowHandle && b.Button == button);
            if (down) _backgroundButtons.Add(new TrackedBackgroundMouseButton(windowHandle, button, clientX, clientY));
        }
    }

    /// <summary>A background key down / up was posted to <paramref name="windowHandle"/>.</summary>
    public void NoteBackgroundKey(nint windowHandle, int virtualKey, int scanCode, bool extended, bool down)
    {
        if (windowHandle == 0) return;
        lock (_gate)
        {
            _backgroundKeys.RemoveAll(k => k.WindowHandle == windowHandle && k.VirtualKey == virtualKey);
            if (down) _backgroundKeys.Add(new TrackedBackgroundKey(windowHandle, virtualKey, scanCode, extended));
        }
    }

    private void Note(TrackedKey key, bool down)
    {
        lock (_gate)
        {
            // A release matches the key however it was pressed: by its virtual-key code (a Key press step and recorded
            // input agree on it), or by the same scan code + extended flag for recorded keys without a virtual key.
            _keys.RemoveAll(k => (key.VirtualKey != 0 && k.VirtualKey == key.VirtualKey)
                                 || (key.ScanMode && k.ScanMode && key.ScanCode != 0 && k.ScanCode == key.ScanCode && k.Extended == key.Extended));
            if (down) _keys.Add(key);
        }
    }

    /// <summary>Everything that is held, and forgets it (the caller sends the releases).</summary>
    public (IReadOnlyList<MacroMouseButton> Buttons, IReadOnlyList<TrackedKey> Keys) TakeAll()
    {
        lock (_gate)
        {
            var result = (Buttons: (IReadOnlyList<MacroMouseButton>)_buttons.ToList(), Keys: (IReadOnlyList<TrackedKey>)_keys.ToList());
            _buttons.Clear();
            _keys.Clear();
            return result;
        }
    }

    /// <summary>Everything background that is held, and forgets it (the caller sends the releases).</summary>
    public (IReadOnlyList<TrackedBackgroundMouseButton> Buttons, IReadOnlyList<TrackedBackgroundKey> Keys) TakeAllBackground()
    {
        lock (_gate)
        {
            var result = (
                Buttons: (IReadOnlyList<TrackedBackgroundMouseButton>)_backgroundButtons.ToList(),
                Keys: (IReadOnlyList<TrackedBackgroundKey>)_backgroundKeys.ToList());
            _backgroundButtons.Clear();
            _backgroundKeys.Clear();
            return result;
        }
    }

    public string? ReleaseAll(Action<MacroMouseButton> releaseButton, Action<TrackedKey> releaseKey) =>
        ReleaseAll(releaseButton, releaseKey, releaseBackgroundButton: null, releaseBackgroundKey: null);

    public string? ReleaseAll(
        Action<MacroMouseButton> releaseButton,
        Action<TrackedKey> releaseKey,
        Action<TrackedBackgroundMouseButton>? releaseBackgroundButton,
        Action<TrackedBackgroundKey>? releaseBackgroundKey)
    {
        ArgumentNullException.ThrowIfNull(releaseButton);
        ArgumentNullException.ThrowIfNull(releaseKey);
        var (buttons, keys) = TakeAll();
        var (bgButtons, bgKeys) = TakeAllBackground();
        string? error = null;

        foreach (var button in buttons)
        {
            try { releaseButton(button); }
            catch (Exception ex) { error ??= $"Could not release the {StepDescriber.DescribeMouseButton(button)}: {ex.Message}"; }
        }

        for (var i = keys.Count - 1; i >= 0; i--)
        {
            try { releaseKey(keys[i]); }
            catch (Exception ex) { error ??= $"Could not release a held key: {ex.Message}"; }
        }

        if (releaseBackgroundButton is not null)
        {
            foreach (var bgBtn in bgButtons)
            {
                try { releaseBackgroundButton(bgBtn); }
                catch (Exception ex) { error ??= $"Could not release the background {StepDescriber.DescribeMouseButton(bgBtn.Button)}: {ex.Message}"; }
            }
        }

        if (releaseBackgroundKey is not null)
        {
            for (var i = bgKeys.Count - 1; i >= 0; i--)
            {
                try { releaseBackgroundKey(bgKeys[i]); }
                catch (Exception ex) { error ??= $"Could not release a held background key: {ex.Message}"; }
            }
        }

        TakeAll();
        TakeAllBackground();
        return error;
    }
}

/// <summary>
/// Wraps any <see cref="IInputSimulator"/> and adds the "release everything" failsafe by tracking what it pressed.
/// Handy for simulators that don't track input themselves (and for tests).
/// </summary>
public sealed class TrackingInputSimulator : IInputSimulator
{
    private readonly IInputSimulator _inner;

    public TrackingInputSimulator(IInputSimulator inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>What is held right now.</summary>
    public HeldInputTracker Tracker { get; } = new();

    public void MoveMouse(int x, int y) => _inner.MoveMouse(x, y);

    public void MoveMouseRelative(int dx, int dy) => _inner.MoveMouseRelative(dx, dy);

    public void MouseButton(MacroMouseButton button, bool down)
    {
        // A press is noted before it is sent (so one that half-arrived is still released), a release after.
        if (down) Tracker.NoteButton(button, true);
        _inner.MouseButton(button, down);
        if (!down) Tracker.NoteButton(button, false);
    }

    public void Scroll(int delta, bool horizontal) => _inner.Scroll(delta, horizontal);

    public void Key(int virtualKey, bool down)
    {
        if (down) Tracker.NoteKey(virtualKey, true);
        _inner.Key(virtualKey, down);
        if (!down) Tracker.NoteKey(virtualKey, false);
    }

    public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down)
    {
        if (down) Tracker.NoteScanKey(virtualKey, scanCode, extended, true);
        _inner.KeyWithScanCode(virtualKey, scanCode, extended, down);
        if (!down) Tracker.NoteScanKey(virtualKey, scanCode, extended, false);
    }

    public void TypeCharacters(string text) => _inner.TypeCharacters(text);

    public bool TryGetCursorPosition(out int x, out int y) => _inner.TryGetCursorPosition(out x, out y);

    public bool SendBackgroundMouseClick(string windowTitle, MacroMouseButton button, MouseClickAction action, int screenX, int screenY, int holdMs) =>
        _inner.SendBackgroundMouseClick(windowTitle, button, action, screenX, screenY, holdMs);

    public bool SendBackgroundKey(string windowTitle, int virtualKey, KeyAction action, int holdMs, KeyModifiers modifiers) =>
        _inner.SendBackgroundKey(windowTitle, virtualKey, action, holdMs, modifiers);

    public bool SendBackgroundText(string windowTitle, string text, int characterDelayMs) =>
        _inner.SendBackgroundText(windowTitle, text, characterDelayMs);

    public string? ReleaseAllHeldInputs()
    {
        var error = Tracker.ReleaseAll(
            button => _inner.MouseButton(button, false),
            key =>
            {
                if (key.ScanMode) _inner.KeyWithScanCode(key.VirtualKey, key.ScanCode, key.Extended, false);
                else _inner.Key(key.VirtualKey, false);
            });
        return error ?? _inner.ReleaseAllHeldInputs();
    }
}
