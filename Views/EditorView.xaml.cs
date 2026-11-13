using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Views;

public partial class EditorView : UserControl
{
    private EditorViewModel? _editor;
    private readonly Dictionary<TextBox, string> _headerOriginalValues = new();

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.FocusMacroNameRequested -= OnFocusMacroNameRequested;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }

            _editor = DataContext as EditorViewModel;
            if (_editor is not null)
            {
                _editor.FocusMacroNameRequested += OnFocusMacroNameRequested;
                _editor.PropertyChanged += OnEditorPropertyChanged;
            }
        };
        PreviewKeyDown += Editor_PreviewKeyDown;
    }

    // ---------------- Header Rename & Quick-Commit (Enter saves, Esc reverts) ----------------

    private void OnFocusMacroNameRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MacroNameBox is null || !MacroNameBox.IsVisible) return;
            MacroNameBox.Focus();
            Keyboard.Focus(MacroNameBox);
            MacroNameBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HeaderTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        _headerOriginalValues[tb] = tb.Text ?? string.Empty;

        // If the macro still has a default "Recording …" or "Untitled …" name, select all text on focus for 1-keystroke rename.
        if (ReferenceEquals(tb, MacroNameBox) && IsDefaultMacroName(tb.Text))
            tb.SelectAll();
    }

    private void MacroNameBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin && IsDefaultMacroName(tb.Text))
        {
            e.Handled = true;
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        }
    }

    private static bool IsDefaultMacroName(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.StartsWith("Recording ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("New Macro", StringComparison.OrdinalIgnoreCase));

    private async void HeaderTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || _editor is null) return;

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ExitHeaderTypingMode();
            await _editor.CommitHeaderEditAsync();
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_headerOriginalValues.TryGetValue(tb, out var original))
            {
                tb.Text = original;
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            ExitHeaderTypingMode();
            if (_editor.HasMacro && _editor.IsDirty)
                await _editor.CommitHeaderEditAsync();
        }
    }

    private void ExitHeaderTypingMode()
    {
        if (_editor?.SelectedStep is not null && WorkflowCanvas.IsStepListVisible)
        {
            WorkflowCanvas.FocusSelectedStep();
        }
        else if (WorkflowCanvas.CanFocusStepList)
        {
            WorkflowCanvas.FocusStepList();
        }
        else
        {
            Keyboard.ClearFocus();
        }
    }

    private void RunSettingsPopup_Opened(object sender, EventArgs e)
    {
        if (_editor?.RefreshTargetWindowsCommand.CanExecute(null) == true)
            _editor.RefreshTargetWindowsCommand.Execute(null);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.SelectedStep))
            PropertiesScroll?.ScrollToTop();
    }

    private void CommandPalette_PaletteClosedFocusRequested(object? sender, EventArgs e) =>
        WorkflowCanvas.FocusSelectedStep();

    /// <summary>Ctrl+F anywhere in the editor: jump to the step search box. Also handles live 3D Keycap binding.</summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor?.SelectedStep is KeyPressStepViewModel { IsRecordingKey: true } keyRecorder)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
            {
                keyRecorder.IsRecordingKey = false;
            }
            else
            {
                var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
                if (pressedKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
                    or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
                {
                    e.Handled = true;
                    return;
                }

                if (pressedKey == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                {
                    keyRecorder.IsRecordingKey = false;
                    e.Handled = true;
                    return;
                }

                var vk = KeyInterop.VirtualKeyFromKey(pressedKey);
                var scanCode = (int)Services.Input.NativeMethods.MapVirtualKey((uint)vk, 0);
                var isExtended = pressedKey is Key.Up or Key.Down or Key.Left or Key.Right
                    or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown
                    or Key.RightCtrl or Key.RightAlt or Key.LWin or Key.RWin or Key.Divide or Key.NumLock;

                var mods = Core.Models.KeyModifiers.None;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Core.Models.KeyModifiers.Control;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Core.Models.KeyModifiers.Shift;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Core.Models.KeyModifiers.Alt;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Core.Models.KeyModifiers.Windows;

                keyRecorder.ApplyRecordedKey(vk, scanCode, isExtended, mods);
                e.Handled = true;
                return;
            }
        }

        if (Keyboard.FocusedElement is TextBox { DataContext: TriggerItemViewModel }) return;
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && !IsTypingInto(e) && _editor?.HasMacro == true && MacroNameBox.IsVisible)
        {
            MacroNameBox.Focus();
            Keyboard.Focus(MacroNameBox);
            MacroNameBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && WorkflowCanvas.IsSearchBoxVisible)
        {
            WorkflowCanvas.FocusSearchBox();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.None && !IsTypingInto(e)
            && _editor?.StepOverCommand.CanExecute(null) == true)
        {
            _editor.StepOverCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Alt+1 / Alt+2 / Alt+3: quick-switch Inspector sub-tabs (Setup · Tune & Motion · Timing & Retry)
        // Alt+[ / Alt+]: step backward / forward through visible workflow steps in the Inspector
        if (Keyboard.Modifiers == ModifierKeys.Alt && _editor is not null)
        {
            var sysKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (sysKey == Key.OemOpenBrackets && _editor.SelectPreviousStepCommand.CanExecute(null))
            {
                _editor.SelectPreviousStepCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (sysKey == Key.OemCloseBrackets && _editor.SelectNextStepCommand.CanExecute(null))
            {
                _editor.SelectNextStepCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_editor.SelectedStep is { HasInspectorTabs: true } selectedStep)
            {
                var targetTab = sysKey switch
                {
                    Key.D1 or Key.NumPad1 => 0,
                    Key.D2 or Key.NumPad2 => selectedStep.HasTuneTab ? 1 : -1,
                    Key.D3 or Key.NumPad3 => 2,
                    _ => -1,
                };
                if (targetTab >= 0)
                {
                    selectedStep.InspectorTab = targetTab;
                    e.Handled = true;
                }
            }
        }
    }

    /// <summary>
    /// True when the key goes to something you type into: the element the key came from, or the element with the
    /// keyboard focus, is (inside) a text box, a password box, an editable combo box or a combo box whose list is open.
    /// </summary>
    internal static bool IsTypingInto(KeyEventArgs e) =>
        IsTextEntry(e.OriginalSource as DependencyObject) || IsTextEntry(Keyboard.FocusedElement as DependencyObject);

    private static bool IsTextEntry(DependencyObject? element)
    {
        for (var current = element; current is not null; current = ParentOf(current))
        {
            switch (current)
            {
                case System.Windows.Controls.Primitives.TextBoxBase:
                case PasswordBox:
                case ComboBox { IsEditable: true }:
                case ComboBox { IsDropDownOpen: true }:
                    return true;
                case ListBox:
                case UserControl:
                    return false; // reached the step list (or the editor itself) without passing a text input
            }
        }

        return false;
    }

    /// <summary>
    /// Responsive 3-column layout: when the window gets narrow, take the missing width from the Add step and Properties
    /// panels (in proportion to how much each can still give, never below their MinWidth), so the Workflow steps column
    /// in the middle keeps its MinWidth. Only fixed-pixel widths are touched; a size the user dragged wider is kept
    /// whenever there's room for it.
    /// </summary>
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.ColumnDefinitions.Count < 5) return;
        ColumnDefinition left = grid.ColumnDefinitions[0], middle = grid.ColumnDefinitions[2], right = grid.ColumnDefinitions[4];
        if (!left.Width.IsAbsolute || !right.Width.IsAbsolute) return;

        double gaps = grid.ColumnDefinitions[1].ActualWidth + grid.ColumnDefinitions[3].ActualWidth;
        double missing = left.Width.Value + right.Width.Value + middle.MinWidth + gaps - e.NewSize.Width;
        if (missing <= 0) return;

        double leftSpare = Math.Max(0, left.Width.Value - left.MinWidth);
        double rightSpare = Math.Max(0, right.Width.Value - right.MinWidth);
        double spare = leftSpare + rightSpare;
        if (spare <= 0) return;

        double cut = Math.Min(missing, spare);
        double leftCut = cut * leftSpare / spare;
        left.Width = new GridLength(left.Width.Value - leftCut);
        right.Width = new GridLength(right.Width.Value - (cut - leftCut));
    }

    private void Properties_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer panel) return;

        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, panel); node = ParentOf(node))
        {
            switch (node)
            {
                case ComboBox { IsDropDownOpen: true }:
                    return; // the open list scrolls itself
                case ComboBox:
                case Slider:
                    e.Handled = true;
                    // Hand the wheel to the panel instead, as if the drop-down / slider wasn't there.
                    panel.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                        Source = panel,
                    });
                    return;
            }
        }
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
