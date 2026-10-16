using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartMacro.Services;

public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Pop-ups and file pickers behind an interface so view models don't touch WPF windows directly.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);

    SaveChoice AskSaveChanges(string macroName);

    void ShowError(string title, string message);

    void ShowWarning(string title, string message);

    void ShowInfo(string title, string message);

    /// <summary>Returns the chosen files, or null if cancelled.</summary>
    string[]? PickFilesToOpen(string title, string filter);

    /// <summary>Returns the chosen path, or null if cancelled.</summary>
    string? PickSaveFile(string title, string filter, string defaultFileName);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        DarkMessageBox.Show(title, message, DialogKind.Question, "Yes", "No") == 0;

    public SaveChoice AskSaveChanges(string macroName) =>
        DarkMessageBox.Show("Unsaved changes", $"\"{macroName}\" has unsaved changes. Do you want to save them?",
                DialogKind.Warning, "Save", "Don't save", "Cancel") switch
            {
                0 => SaveChoice.Save,
                1 => SaveChoice.Discard,
                _ => SaveChoice.Cancel,
            };

    public void ShowError(string title, string message) => DarkMessageBox.Show(title, message, DialogKind.Error, "OK");

    public void ShowWarning(string title, string message) => DarkMessageBox.Show(title, message, DialogKind.Warning, "OK");

    public void ShowInfo(string title, string message) => DarkMessageBox.Show(title, message, DialogKind.Info, "OK");

    public string[]? PickFilesToOpen(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
        return dialog.ShowDialog(VisibleOwner()) == true ? dialog.FileNames : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, AddExtension = true };
        return dialog.ShowDialog(VisibleOwner()) == true ? dialog.FileName : null;
    }

    private static Window? VisibleOwner() =>
        Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } owner ? owner : null;
}

public enum DialogKind
{
    Info,
    Question,
    Warning,
    Error,
}

/// <summary>A dark pop-up that matches the app. Enter picks the first button, Esc the last one.</summary>
public sealed class DarkMessageBox : Window
{
    private int _choice;

    private DarkMessageBox(string title, string message, DialogKind kind, string[] buttons)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Background = ThemeBrush("SurfaceBrush", Color.FromRgb(0x13, 0x1C, 0x2E));
        BorderBrush = ThemeBrush("BorderBrush", Color.FromRgb(0x26, 0x32, 0x4A));
        BorderThickness = new Thickness(1);
        Foreground = ThemeBrush("TextBrush", Colors.White);
        FontFamily = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        FontSize = 13;
        _choice = buttons.Length - 1; // closing the window = last button (Cancel / No / OK)

        var (glyph, color) = kind switch
        {
            DialogKind.Error => ("", ThemeBrush("ErrorBrush", Colors.IndianRed)),
            DialogKind.Warning => ("", ThemeBrush("WarningBrush", Colors.Orange)),
            DialogKind.Question => ("", ThemeBrush("AccentBrush", Colors.DodgerBlue)),
            _ => ("", ThemeBrush("AccentBrush", Colors.DodgerBlue)),
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
            FontSize = 20,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        header.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var body = new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, LineHeight = 19 },
        };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = i == buttons.Length - 1,
            };
            if (i == 0 && TryFindResource("PrimaryButton") is Style primary) button.Style = primary;
            button.Click += (_, _) => { _choice = index; Close(); };
            buttonRow.Children.Add(button);
        }

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MinWidth = 340 };
        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(buttonRow);
        Content = root;

        // Borderless window: drag it by its background.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>Shows the dialog and returns the index of the clicked button.</summary>
    public static int Show(string title, string message, DialogKind kind, params string[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { "OK" };

        try
        {
            var dialog = new DarkMessageBox(title, message, kind, buttons);
            var owner = Application.Current?.MainWindow;
            if (owner is { IsVisible: true, WindowState: not WindowState.Minimized } && !ReferenceEquals(owner, dialog))
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Topmost = true; // SmartMacro may be minimized; make sure the message is seen
            }

            dialog.ShowDialog();
            return dialog._choice;
        }
        catch (InvalidOperationException)
        {
            // E.g. during shutdown: fall back to the standard Windows message box.
            var result = MessageBox.Show(message, title, buttons.Length switch
            {
                1 => MessageBoxButton.OK,
                2 => MessageBoxButton.YesNo,
                _ => MessageBoxButton.YesNoCancel,
            });
            return result switch
            {
                MessageBoxResult.Yes or MessageBoxResult.OK => 0,
                MessageBoxResult.No => 1,
                _ => buttons.Length - 1,
            };
        }
    }

    private Brush ThemeBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
