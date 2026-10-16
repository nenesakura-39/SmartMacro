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
            if (_editor is not null) _editor.SelectStepsRequested -= OnSelectStepsRequested;
            _editor = DataContext as EditorViewModel;
            if (_editor is not null) _editor.SelectStepsRequested += OnSelectStepsRequested;
        };
    }

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

    /// <summary>Scrolls the list while dragging near its top or bottom edge.</summary>
    private void AutoScroll(Point position)
    {
        if (FindScrollViewer(StepList) is not { } scroller) return;
        const double edge = 28;
        if (position.Y < edge) scroller.LineUp();
        else if (position.Y > StepList.ActualHeight - edge) scroller.LineDown();
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
