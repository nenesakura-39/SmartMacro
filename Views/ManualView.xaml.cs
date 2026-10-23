using System.Windows.Controls;
using System.Windows.Input;
using SmartMacro.ViewModels;

namespace SmartMacro.Views;

/// <summary>The In-App Manual page. Ctrl+F (or just opening the page) puts the cursor in the search box; Esc clears it.</summary>
public partial class ManualView : UserControl
{
    public ManualView()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ManualViewModel manual) return;
        if (manual.ClearSearchCommand.CanExecute(null)) manual.ClearSearchCommand.Execute(null);
        e.Handled = true;
    }
}
