using System.Windows.Controls;
using System.Windows.Input;
using SmartMacro.Core.Models;
using SmartMacro.ViewModels;

namespace SmartMacro.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // Records the key combination pressed while a shortcut box has focus.
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string which } || DataContext is not SettingsViewModel vm) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Let Tab move focus normally, and wait while only modifier keys are held.
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
        {
            e.Handled = true;
            return;
        }

        var modifiers = KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= KeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= KeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= KeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= KeyModifiers.Windows;

        vm.CaptureHotkey(which, new HotkeyBinding(modifiers, KeyInterop.VirtualKeyFromKey(key)));
        e.Handled = true;
    }
}
