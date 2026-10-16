using System.Windows.Controls;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
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
}
