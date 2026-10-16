using System.Windows.Controls;

namespace SmartMacro.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
    }

    // Keep the selected step visible after adding, moving or duplicating steps.
    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepList.SelectedItem is { } item)
            StepList.ScrollIntoView(item);
    }
}
