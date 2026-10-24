using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Views;

public partial class EditorView : UserControl
{
    // Drag-and-drop reordering state (the rules for what may move where live in Core: StepListEditing).
    private const string DragFormat = "SmartMacro.Steps";
    private Point? _dragStart;
    private ListBoxItem? _pressedItem;
    private bool _keepSelectionOnPress;
    private InsertionAdorner? _insertionMarker;
    private EditorViewModel? _editor;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.SelectStepsRequested -= OnSelectStepsRequested;
                _editor.RunningStepChanged -= OnRunningStepChanged;
                _editor.RevealStepRequested -= OnRevealStepRequested;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }

            _editor = DataContext as EditorViewModel;
            if (_editor is not null)
            {
                _editor.SelectStepsRequested += OnSelectStepsRequested;
                _editor.RunningStepChanged += OnRunningStepChanged;
                _editor.RevealStepRequested += OnRevealStepRequested;
                _editor.PropertyChanged += OnEditorPropertyChanged;
            }
        };
        PreviewKeyDown += Editor_PreviewKeyDown;
    }

    // ---------------- Ctrl+K Command Palette ----------------

    /// <summary>The palette opened: put the cursor in its search box. Closed: back to the step list.</summary>
    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorViewModel.IsCommandPaletteOpen) || _editor is null) return;
        if (_editor.IsCommandPaletteOpen)
        {
            // After the overlay became visible (it can't take focus while still collapsed).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PaletteBox.Focus();
                Keyboard.Focus(PaletteBox);
                PaletteBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        else if (PaletteBox.IsKeyboardFocusWithin)
        {
            FocusSelectedStep();
        }
    }

    /// <summary>Palette search box: ↑ / ↓ choose, Enter runs, Esc closes.</summary>
    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor is null) return;
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                _editor.MovePaletteSelection(e.Key == Key.Down ? +1 : -1);
                if (_editor.SelectedPaletteItem is { } item) PaletteList.ScrollIntoView(item);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_editor.ExecutePaletteItemCommand.CanExecute(null)) _editor.ExecutePaletteItemCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _editor.CloseCommandPaletteCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>A click on a palette row runs it.</summary>
    private void PaletteList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_editor is null || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(PaletteList, source) is ListBoxItem { DataContext: Core.Workflow.StudioCommand command })
        {
            _editor.ExecutePaletteItemCommand.Execute(command);
            e.Handled = true;
        }
    }

    /// <summary>A click on the dimmed editor around the palette closes it.</summary>
    private void PaletteBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _editor?.CloseCommandPaletteCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Search navigation (↑ ↓ / F3): scroll the match into view.</summary>
    private void OnRevealStepRequested(object? sender, StepViewModel step)
    {
        if (!StepList.IsVisible) return;
        StepList.UpdateLayout(); // a block that was just unfolded must have its rows before scrolling to one of them
        StepList.ScrollIntoView(step);
    }

    /// <summary>Ctrl+F anywhere in the editor: jump to the step search box.</summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && SearchBox.IsVisible)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Search box: Enter = next match, Shift+Enter = previous, Esc = clear and go back to the list.</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor is null) return;
        switch (e.Key)
        {
            case Key.Enter:
            {
                var command = Keyboard.Modifiers == ModifierKeys.Shift ? _editor.PreviousMatchCommand : _editor.NextMatchCommand;
                if (command.CanExecute(null)) command.Execute(null);
                e.Handled = true;
                break;
            }
            case Key.Escape:
                if (_editor.ClearSearchCommand.CanExecute(null)) _editor.ClearSearchCommand.Execute(null);
                FocusSelectedStep();
                e.Handled = true;
                break;
            case Key.Down when _editor.SelectedStep is not null:
                FocusSelectedStep();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Step list shortcuts (instead of key bindings, so they can check where the keyboard is):
    /// Space = turn the selected steps (or the whole If / Repeat block) on / off, Left / Right = fold / unfold a selected
    /// If / Repeat, Delete = delete, Ctrl+D = duplicate, Ctrl+C / Ctrl+V = copy / paste, Ctrl+Shift+I / R = wrap in If /
    /// Repeat, Alt+↑ / ↓ / Home / End = move. Never while the keyboard is in a text box, an editable or open combo box or
    /// a password box: those keys then type / edit text as usual. (F5 / Shift+F5 / Esc stay global.)
    /// </summary>
    private void StepList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor is null || e.Handled || IsTypingInto(e)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        ICommand? command = null;
        object? parameter = null;

        switch (modifiers)
        {
            case ModifierKeys.None:
                switch (key)
                {
                    case Key.Space:
                        command = _editor.ToggleEnabledCommand;
                        break;
                    case Key.Delete:
                        command = _editor.DeleteStepCommand;
                        break;
                    case Key.Left when _editor.SelectedStep is { CanCollapse: true, IsCollapsed: false } open:
                        command = _editor.ToggleCollapseCommand;
                        parameter = open;
                        break;
                    case Key.Right when _editor.SelectedStep is { CanCollapse: true, IsCollapsed: true } folded:
                        command = _editor.ToggleCollapseCommand;
                        parameter = folded;
                        break;
                }

                break;
            case ModifierKeys.Control:
                command = key switch
                {
                    Key.D => _editor.DuplicateStepCommand,
                    Key.C => _editor.CopySelectedStepCommand,
                    Key.V => _editor.PasteStepCommand,
                    _ => null,
                };
                break;
            case ModifierKeys.Control | ModifierKeys.Shift:
                command = key switch
                {
                    Key.I => _editor.WrapInIfCommand,
                    Key.R => _editor.WrapInRepeatCommand,
                    _ => null,
                };
                break;
            case ModifierKeys.Alt:
                command = key switch
                {
                    Key.Up => _editor.MoveUpCommand,
                    Key.Down => _editor.MoveDownCommand,
                    Key.Home => _editor.MoveToTopCommand,
                    Key.End => _editor.MoveToBottomCommand,
                    _ => null,
                };
                break;
        }

        if (command is null || !command.CanExecute(parameter)) return;
        command.Execute(parameter);
        e.Handled = true;
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

    private void FocusSelectedStep()
    {
        if (_editor?.SelectedStep is not { } step) return;
        StepList.ScrollIntoView(step);
        if (StepList.ItemContainerGenerator.ContainerFromItem(step) is ListBoxItem item) item.Focus();
        else StepList.Focus();
    }

    /// <summary>While playing: keep the running step (the one with the "▶ Running" badge) in view.</summary>
    private void OnRunningStepChanged(object? sender, StepViewModel step)
    {
        if (StepList.IsVisible) StepList.ScrollIntoView(step);
    }

    /// <summary>
    /// Mouse wheel over the Properties panel always scrolls the panel. Without this, a closed drop-down (or a slider)
    /// under the cursor grabbed the wheel and silently changed its value while the user was only scrolling past it.
    /// An OPEN drop-down list still scrolls its own items.
    /// </summary>
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

    // Keep the selected step visible after adding, moving or duplicating steps, and tell the editor which steps
    // are selected (Shift / Ctrl+click can select several, e.g. for "Wrap in Repeat").
    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is EditorViewModel editor)
            editor.SetSelection(StepList.SelectedItems.OfType<StepViewModel>());

        if (StepList.SelectedItem is { } item)
            StepList.ScrollIntoView(item);
    }

    /// <summary>After a move: select exactly the moved steps again and show them.</summary>
    private void OnSelectStepsRequested(object? sender, IReadOnlyList<StepViewModel> steps)
    {
        if (steps.Count == 0) return;
        StepList.SelectedItems.Clear();
        foreach (var step in steps) StepList.SelectedItems.Add(step);
        StepList.ScrollIntoView(steps[^1]);
        StepList.ScrollIntoView(steps[0]);
        (StepList.ItemContainerGenerator.ContainerFromItem(steps[0]) as ListBoxItem)?.Focus();
    }

    // ---------------- Drag and drop ----------------

    private void StepList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedItem = ItemUnder(e.OriginalSource as DependencyObject);
        if (_pressedItem is null) return;

        _dragStart = e.GetPosition(StepList);

        // Pressing on one of several selected steps (without Shift / Ctrl) normally drops the other selections
        // straight away. Keep them, so the whole group can be dragged; a plain click (no drag) selects just this one
        // on release.
        _keepSelectionOnPress = _pressedItem.IsSelected && StepList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None;
        if (_keepSelectionOnPress) e.Handled = true;
    }

    private void StepList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_keepSelectionOnPress && _pressedItem?.DataContext is StepViewModel clicked)
        {
            StepList.SelectedItems.Clear();
            StepList.SelectedItem = clicked;
        }

        _keepSelectionOnPress = false;
        _dragStart = null;
        _pressedItem = null;
    }

    private void StepList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _pressedItem is null) return;

        var now = e.GetPosition(StepList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Make sure the step that was grabbed is part of what's dragged.
        if (!_pressedItem.IsSelected)
        {
            StepList.SelectedItems.Clear();
            _pressedItem.IsSelected = true;
        }

        _dragStart = null;
        _keepSelectionOnPress = false;
        try
        {
            DragDrop.DoDragDrop(StepList, new DataObject(DragFormat, true), DragDropEffects.Move);
        }
        finally
        {
            RemoveInsertionMarker();
            _pressedItem = null;
        }
    }

    private void StepList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        AutoScroll(e.GetPosition(StepList));

        var (item, after) = DropTarget(e);
        ShowInsertionMarker(item, after);
    }

    private void StepList_DragLeave(object sender, DragEventArgs e) => RemoveInsertionMarker();

    private void StepList_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertionMarker();
        if (!e.Data.GetDataPresent(DragFormat) || DataContext is not EditorViewModel editor) return;

        var (item, after) = DropTarget(e);
        var index = item?.DataContext is StepViewModel step ? editor.Steps.IndexOf(step) + (after ? 1 : 0) : editor.Steps.Count;
        if (index < 0) return;
        editor.MoveSelectionTo(index);
        e.Handled = true;
    }

    /// <summary>The row under the mouse and whether the drop goes after it (lower half) or before it (upper half).</summary>
    private (ListBoxItem? Item, bool After) DropTarget(DragEventArgs e)
    {
        var item = ItemUnder(StepList.InputHitTest(e.GetPosition(StepList)) as DependencyObject);
        if (item is null) return (null, true); // below the last row = at the end
        var y = e.GetPosition(item).Y;
        return (item, y > item.ActualHeight / 2);
    }

    private ListBoxItem? ItemUnder(DependencyObject? element)
    {
        while (element is not null && element is not ListBoxItem)
        {
            if (ReferenceEquals(element, StepList)) return null;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }

    /// <summary>
    /// Scrolls the list while dragging near its top or bottom edge: the closer to the edge, the faster (6–36 px per drag
    /// event). The list scrolls by pixels (ScrollUnit="Pixel"), so this stays smooth with the recycling virtualization.
    /// </summary>
    private void AutoScroll(Point position)
    {
        if (FindScrollViewer(StepList) is not { } scroller) return;
        const double edge = 40;
        const double minStep = 6;
        const double maxStep = 36;
        double depth;
        if (position.Y < edge) depth = (edge - Math.Max(0, position.Y)) / edge;
        else if (position.Y > StepList.ActualHeight - edge) depth = -(position.Y - (StepList.ActualHeight - edge)) / edge;
        else return;

        var amount = minStep + (maxStep - minStep) * Math.Min(1, Math.Abs(depth));
        var target = scroller.VerticalOffset + (depth > 0 ? -amount : amount);
        scroller.ScrollToVerticalOffset(Math.Clamp(target, 0, scroller.ScrollableHeight));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }

        return null;
    }

    private void ShowInsertionMarker(ListBoxItem? item, bool after)
    {
        // At the end of the list: mark the bottom of the last row.
        if (item is null && StepList.Items.Count > 0)
        {
            item = StepList.ItemContainerGenerator.ContainerFromIndex(StepList.Items.Count - 1) as ListBoxItem;
            after = true;
        }

        if (item is null)
        {
            RemoveInsertionMarker();
            return;
        }

        if (_insertionMarker is not null && ReferenceEquals(_insertionMarker.AdornedElement, item))
        {
            _insertionMarker.After = after;
            return;
        }

        RemoveInsertionMarker();
        if (AdornerLayer.GetAdornerLayer(item) is not { } layer) return;
        _insertionMarker = new InsertionAdorner(item) { After = after };
        layer.Add(_insertionMarker);
    }

    private void RemoveInsertionMarker()
    {
        if (_insertionMarker is null) return;
        AdornerLayer.GetAdornerLayer(_insertionMarker.AdornedElement)?.Remove(_insertionMarker);
        _insertionMarker = null;
    }

    /// <summary>A thin accent line above or below a row: where the dragged steps will land.</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly Pen LinePen = CreatePen();
        private bool _after;

        public InsertionAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public bool After
        {
            get => _after;
            set
            {
                if (_after == value) return;
                _after = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            var y = _after ? size.Height - 1 : 1;
            drawingContext.DrawLine(LinePen, new Point(0, y), new Point(size.Width, y));
        }

        private static Pen CreatePen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 3);
            pen.Freeze();
            return pen;
        }
    }
}
