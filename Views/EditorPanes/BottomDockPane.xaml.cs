using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Views.EditorPanes;

public partial class BottomDockPane : UserControl
{
    public BottomDockPane()
    {
        InitializeComponent();
    }

    /// <summary>Records the key chord pressed while a per-macro trigger shortcut box has focus.</summary>
    private void TriggerHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TriggerItemViewModel trigger }) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Core.Models.KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= Core.Models.KeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= Core.Models.KeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= Core.Models.KeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= Core.Models.KeyModifiers.Windows;

        trigger.Hotkey = new Core.Models.HotkeyBinding(modifiers, KeyInterop.VirtualKeyFromKey(key));
        e.Handled = true;
    }

    private void LiveVariableBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not EditorViewModel editor || sender is not FrameworkElement fe) return;
        if (editor.ApplyLiveVariableCommand.CanExecute(fe.DataContext))
        {
            editor.ApplyLiveVariableCommand.Execute(fe.DataContext);
            e.Handled = true;
        }
    }

    private void BreakpointConditionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not EditorViewModel editor) return;
        if (editor.ApplyBreakpointConditionCommand.CanExecute(null))
        {
            editor.ApplyBreakpointConditionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void WatchpointBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not EditorViewModel editor) return;
        if (editor.AddWatchpointCommand.CanExecute(null))
        {
            editor.AddWatchpointCommand.Execute(null);
            e.Handled = true;
        }
    }
}
