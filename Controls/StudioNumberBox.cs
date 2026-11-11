using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartMacro.Controls;

/// <summary>
/// Tactile Studio X Ultra numeric control with horizontal drag-to-scrub, inline unit badge,
/// micro stepper buttons, keyboard arrow stepping, and optional 1-click preset chips.
/// </summary>
public sealed class StudioNumberBox : Border
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(int),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(int.MinValue, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(int.MaxValue, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(int),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(10));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit),
        typeof(string),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(string.Empty, OnUnitChanged));

    public static readonly DependencyProperty PresetsProperty = DependencyProperty.Register(
        nameof(Presets),
        typeof(string),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(string.Empty, OnPresetsChanged));

    public static readonly DependencyProperty ShowStepperProperty = DependencyProperty.Register(
        nameof(ShowStepper),
        typeof(bool),
        typeof(StudioNumberBox),
        new FrameworkPropertyMetadata(true, OnShowStepperChanged));

    private readonly Border _inputShell;
    private readonly Border _scrubHandle;
    private readonly TextBox _textBox;
    private readonly TextBlock _unitBlock;
    private readonly StackPanel _stepperPanel;
    private readonly WrapPanel _presetsPanel;
    private bool _updatingText;
    private bool _isScrubbing;
    private System.Windows.Point _scrubStartPoint;
    private int _scrubStartValue;
    private int _focusStartValue;

    private static readonly Brush DefaultBg = Frozen(Color.FromRgb(0x0B, 0x0F, 0x1A));
    private static readonly Brush HoverBg = Frozen(Color.FromRgb(0x11, 0x17, 0x26));
    private static readonly Brush ScrubGripBg = Frozen(Color.FromArgb(0x22, 0x38, 0xBD, 0xF8));
    private static readonly Brush ScrubGripActiveBg = Frozen(Color.FromArgb(0x45, 0x22, 0xD3, 0xEE));
    private static readonly Brush DefaultBorder = Frozen(Color.FromRgb(0x28, 0x32, 0x4E));
    private static readonly Brush HoverBorder = Frozen(Color.FromRgb(0x3B, 0x4A, 0x6B));
    private static readonly Brush ActiveBorder = Frozen(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly Brush TextPrimary = Frozen(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly Brush TextMuted = Frozen(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush ChipBg = Frozen(Color.FromArgb(0x24, 0x1E, 0x29, 0x3B));
    private static readonly Brush ChipHoverBg = Frozen(Color.FromArgb(0x44, 0x38, 0xBD, 0xF8));
    private static readonly Brush ChipBorder = Frozen(Color.FromArgb(0x44, 0x33, 0x41, 0x55));

    public StudioNumberBox()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var rootStack = new StackPanel { Orientation = Orientation.Vertical };

        _inputShell = new Border
        {
            Background = DefaultBg,
            BorderBrush = DefaultBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            MinHeight = 32,
            SnapsToDevicePixels = true,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Scrub grip
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // TextBox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Unit suffix
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Stepper (- / +)

        // Left drag-to-scrub grip
        _scrubHandle = new Border
        {
            Background = ScrubGripBg,
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            Padding = new Thickness(5, 0, 5, 0),
            Cursor = Cursors.SizeWE,
            ToolTip = "Drag left/right to scrub value (Shift = 10×, Ctrl = 1×)",
            Child = new TextBlock
            {
                Text = "⟷",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _scrubHandle.MouseLeftButtonDown += OnScrubMouseDown;
        _scrubHandle.MouseMove += OnScrubMouseMove;
        _scrubHandle.MouseLeftButtonUp += OnScrubMouseUp;
        _scrubHandle.LostMouseCapture += OnScrubLostCapture;
        Grid.SetColumn(_scrubHandle, 0);
        grid.Children.Add(_scrubHandle);

        // Editable numeric TextBox (minimal template so global TextBox padding/borders never squeeze digits)
        var textBoxTemplate = new ControlTemplate(typeof(TextBox));
        var contentHostFactory = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        contentHostFactory.SetValue(FocusableProperty, false);
        contentHostFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        contentHostFactory.SetValue(MarginProperty, new Thickness(0));
        textBoxTemplate.VisualTree = contentHostFactory;

        _textBox = new TextBox
        {
            Template = textBoxTemplate,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TextPrimary,
            CaretBrush = ActiveBorder,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 2, 2, 2),
            MinWidth = 34,
            Text = "0",
        };
        _textBox.TextChanged += OnTextBoxTextChanged;
        _textBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
        _textBox.PreviewMouseLeftButtonDown += OnTextBoxPreviewMouseLeftButtonDown;
        _textBox.GotKeyboardFocus += OnTextBoxGotFocus;
        _textBox.LostKeyboardFocus += OnTextBoxLostFocus;
        Grid.SetColumn(_textBox, 1);
        grid.Children.Add(_textBox);

        // Inline unit badge (also draggable to scrub)
        _unitBlock = new TextBlock
        {
            Foreground = TextMuted,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 4, 0),
            Cursor = Cursors.SizeWE,
            Visibility = Visibility.Collapsed,
            ToolTip = "Drag left/right to scrub value",
        };
        _unitBlock.MouseLeftButtonDown += OnScrubMouseDown;
        _unitBlock.MouseMove += OnScrubMouseMove;
        _unitBlock.MouseLeftButtonUp += OnScrubMouseUp;
        _unitBlock.LostMouseCapture += OnScrubLostCapture;
        Grid.SetColumn(_unitBlock, 2);
        grid.Children.Add(_unitBlock);

        // Micro - / + steppers
        _stepperPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 2, 2),
        };
        var minusBtn = CreateStepButton("−", -1, "Decrease value (Shift = 10×)");
        var plusBtn = CreateStepButton("+", 1, "Increase value (Shift = 10×)");
        _stepperPanel.Children.Add(minusBtn);
        _stepperPanel.Children.Add(plusBtn);
        Grid.SetColumn(_stepperPanel, 3);
        grid.Children.Add(_stepperPanel);

        _inputShell.Child = grid;
        _inputShell.MouseEnter += (_, _) => UpdateShellVisual();
        _inputShell.MouseLeave += (_, _) => UpdateShellVisual();
        _inputShell.PreviewMouseWheel += OnInputPreviewMouseWheel;

        rootStack.Children.Add(_inputShell);

        // Optional preset chips bar
        _presetsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        rootStack.Children.Add(_presetsPanel);

        Child = rootStack;
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Presets
    {
        get => (string)GetValue(PresetsProperty);
        set => SetValue(PresetsProperty, value);
    }

    public bool ShowStepper
    {
        get => (bool)GetValue(ShowStepperProperty);
        set => SetValue(ShowStepperProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == AutomationProperties.NameProperty && e.NewValue is string name && _textBox is not null)
        {
            AutomationProperties.SetName(_textBox, name);
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (StudioNumberBox)d;
        var val = (int)e.NewValue;
        box.SyncTextFromValue(val);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (StudioNumberBox)d;
        var clamped = box.Clamp(box.Value);
        if (clamped != box.Value)
            box.Value = clamped;
    }

    private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (StudioNumberBox)d;
        var unit = e.NewValue as string ?? string.Empty;
        box._unitBlock.Text = unit;
        box._unitBlock.Visibility = string.IsNullOrWhiteSpace(unit) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(box.Presets))
            box.RebuildPresetChips(box.Presets);
    }

    private static void OnShowStepperChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (StudioNumberBox)d;
        box._stepperPanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnPresetsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (StudioNumberBox)d;
        box.RebuildPresetChips(e.NewValue as string ?? string.Empty);
    }

    private void RebuildPresetChips(string rawPresets)
    {
        _presetsPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(rawPresets))
        {
            _presetsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = rawPresets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var token = part;
            var isRelative = token.StartsWith('+') || (token.StartsWith('-') && token.Length > 1);
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                continue;

            var label = isRelative
                ? token
                : string.IsNullOrEmpty(Unit) ? token : $"{token}{Unit}";

            var chip = new Border
            {
                Background = ChipBg,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
                    FontSize = 10.5,
                    Foreground = TextMuted,
                },
            };

            chip.MouseEnter += (_, _) =>
            {
                chip.Background = ChipHoverBg;
                chip.BorderBrush = AccentBrush;
                if (chip.Child is TextBlock tb) tb.Foreground = TextPrimary;
            };
            chip.MouseLeave += (_, _) =>
            {
                chip.Background = ChipBg;
                chip.BorderBrush = ChipBorder;
                if (chip.Child is TextBlock tb) tb.Foreground = TextMuted;
            };
            chip.MouseLeftButtonDown += (_, args) =>
            {
                args.Handled = true;
                if (isRelative && token != "0")
                    SetClampedValue(Value + parsed);
                else
                    SetClampedValue(parsed);
            };

            _presetsPanel.Children.Add(chip);
        }

        _presetsPanel.Visibility = _presetsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border CreateStepButton(string symbol, int direction, string tooltip)
    {
        var text = new TextBlock
        {
            Text = symbol,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var btn = new Border
        {
            Width = 19,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = text,
        };

        btn.MouseEnter += (_, _) =>
        {
            btn.Background = ChipHoverBg;
            text.Foreground = TextPrimary;
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = Brushes.Transparent;
            text.Foreground = TextMuted;
        };
        btn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            StepBy(direction);
        };

        return btn;
    }

    private void StepBy(int direction)
    {
        var baseStep = Math.Max(1, Step);
        var mult = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 :
                   (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 1 : 1;
        var delta = (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? direction
            : direction * baseStep * mult;
        SetClampedValue(Value + delta);
    }

    private void SetClampedValue(long candidate)
    {
        var clamped = Clamp(candidate);
        Value = clamped;
        SyncTextFromValue(clamped);
    }

    private int Clamp(long candidate)
    {
        var min = (long)Minimum;
        var max = (long)Maximum;
        if (min > max) (min, max) = (max, min);
        return (int)Math.Clamp(candidate, min, max);
    }

    private void SyncTextFromValue(int val)
    {
        if (_updatingText) return;
        _updatingText = true;
        try
        {
            var formatted = val.ToString(CultureInfo.InvariantCulture);
            if (_textBox.Text != formatted)
                _textBox.Text = formatted;
        }
        finally
        {
            _updatingText = false;
        }
    }

    private void OnTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText) return;
        if (int.TryParse(_textBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            _updatingText = true;
            try
            {
                Value = Clamp(parsed);
            }
            finally
            {
                _updatingText = false;
            }
        }
    }

    private void OnTextBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            _textBox.Focus();
        }
    }

    private void OnTextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _focusStartValue = Value;
        _textBox.SelectAll();
        UpdateShellVisual();
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        SyncTextFromValue(Value);
        UpdateShellVisual();
    }

    private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up)
        {
            e.Handled = true;
            StepBy(1);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            StepBy(-1);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SyncTextFromValue(Value);
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetClampedValue(_focusStartValue);
            Keyboard.ClearFocus();
        }
    }

    private void OnInputPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_textBox.IsKeyboardFocused && !_isScrubbing)
            return;

        e.Handled = true;
        StepBy(e.Delta > 0 ? 1 : -1);
    }

    private void OnScrubMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement el) return;
        e.Handled = true;
        _isScrubbing = true;
        _scrubStartPoint = e.GetPosition(this);
        _scrubStartValue = Value;
        el.CaptureMouse();
        UpdateShellVisual();
    }

    private void OnScrubMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _scrubStartPoint.X;
        var ticks = (int)(dx / 5.0);
        var baseStep = Math.Max(1, Step);

        long stepAmount = (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? 1
            : (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                ? (long)baseStep * 10
                : baseStep;

        SetClampedValue(_scrubStartValue + ticks * stepAmount);
    }

    private void OnScrubMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        if (sender is UIElement el)
            el.ReleaseMouseCapture();
        UpdateShellVisual();
    }

    private void OnScrubLostCapture(object sender, MouseEventArgs e)
    {
        _isScrubbing = false;
        UpdateShellVisual();
    }

    private void UpdateShellVisual()
    {
        if (_isScrubbing || _textBox.IsKeyboardFocused)
        {
            _inputShell.BorderBrush = ActiveBorder;
            _inputShell.Background = HoverBg;
            _scrubHandle.Background = ScrubGripActiveBg;
        }
        else if (_inputShell.IsMouseOver)
        {
            _inputShell.BorderBrush = HoverBorder;
            _inputShell.Background = HoverBg;
            _scrubHandle.Background = ScrubGripBg;
        }
        else
        {
            _inputShell.BorderBrush = DefaultBorder;
            _inputShell.Background = DefaultBg;
            _scrubHandle.Background = ScrubGripBg;
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
