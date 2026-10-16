using System.Windows.Controls;
using System.Windows.Input;
using SmartMacro.ViewModels;

namespace SmartMacro.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    // Double-clicking a row opens it. Wired per item so double-clicking empty space does nothing.
    private void MacroItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is LibraryViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }
}
