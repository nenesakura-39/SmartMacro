using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartMacro.ViewModels;

namespace SmartMacro.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;
    private string? _inspectorNameBeforeEdit;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.FocusInspectorNameRequested -= OnFocusInspectorNameRequested;
        _vm = e.NewValue as LibraryViewModel;
        if (_vm is not null)
            _vm.FocusInspectorNameRequested += OnFocusInspectorNameRequested;
    }

    private void OnFocusInspectorNameRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!InspectorNameBox.IsVisible) return;
            InspectorNameBox.Focus();
            Keyboard.Focus(InspectorNameBox);
            InspectorNameBox.SelectAll();
        });
    }

    private void InspectorNameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
            _inspectorNameBeforeEdit = box.Text;
    }

    private void InspectorNameBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin)
            return;

        var text = box.Text ?? string.Empty;
        if (text.StartsWith("Recording ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("New Macro", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }
    }

    private async void InspectorNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _vm is null)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var newName = box.Text;
            ExitInspectorTypingMode();
            await _vm.RenameMacroAsync(_vm.SelectedItem, newName);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_inspectorNameBeforeEdit is not null)
                box.Text = _inspectorNameBeforeEdit;
            _vm.SelectedItem?.RaiseNameReverted();
            ExitInspectorTypingMode();
        }
    }

    private async void InspectorNameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || _vm is null || _vm.SelectedItem is null)
            return;

        var newName = box.Text;
        if (!string.Equals(_vm.SelectedItem.Name, newName?.Trim(), StringComparison.Ordinal))
        {
            var ok = await _vm.RenameMacroAsync(_vm.SelectedItem, newName);
            if (!ok && _inspectorNameBeforeEdit is not null)
                box.Text = _inspectorNameBeforeEdit;
        }
    }

    private void ExitInspectorTypingMode()
    {
        if (MacroList.IsVisible)
        {
            MacroList.Focus();
            Keyboard.Focus(MacroList);
        }
        else
        {
            Keyboard.ClearFocus();
        }
    }

    // Double-clicking a card opens it. Wired per item so double-clicking empty space does nothing, and a quick double
    // click on one of the card's own buttons (Play, Duplicate…) doesn't also open the editor.
    private void MacroItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        if (DataContext is LibraryViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsInsideButton(DependencyObject? node, DependencyObject? stopAt)
    {
        while (node is not null && !ReferenceEquals(node, stopAt))
        {
            if (node is ButtonBase) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
