using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartMacro.ViewModels;

namespace SmartMacro.Views.EditorPanes;

public partial class CommandPaletteOverlay : UserControl
{
    private EditorViewModel? _editor;

    public CommandPaletteOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_editor is not null)
                _editor.PropertyChanged -= OnEditorPropertyChanged;

            _editor = DataContext as EditorViewModel;
            if (_editor is not null)
                _editor.PropertyChanged += OnEditorPropertyChanged;
        };
    }

    /// <summary>Raised when the palette closes while keyboard focus was inside it, so the host can refocus the step list.</summary>
    public event EventHandler? PaletteClosedFocusRequested;

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
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
            PaletteClosedFocusRequested?.Invoke(this, EventArgs.Empty);
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
}
