using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SmartMacro.ViewModels;

namespace SmartMacro.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
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
