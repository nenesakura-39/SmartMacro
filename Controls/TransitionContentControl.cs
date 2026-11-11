using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartMacro.Services;
using SmartMacro.ViewModels;
using SmartMacro.Views;

namespace SmartMacro.Controls;

/// <summary>
/// Lightweight, zero-NuGet page host that caches instantiated Studio pages in a single visual tree
/// and plays a crisp 140ms CubicEase fade + 6px vertical slide whenever <see cref="ActivePage"/>
/// (or <see cref="ContentControl.Content"/>) switches between Studio pages.
/// Caching views eliminates the 1–2s WPF visual-tree rebuild and XAML resource dictionary re-parse on navigation.
/// Automatically skips animation when Windows "Animation effects" or SmartMacro's "Reduce motion" is active.
/// </summary>
public sealed class TransitionContentControl : ContentControl
{
    public static readonly DependencyProperty ActivePageProperty =
        DependencyProperty.Register(
            nameof(ActivePage),
            typeof(object),
            typeof(TransitionContentControl),
            new PropertyMetadata(null, OnActivePageChanged));

    private readonly TranslateTransform _translate = new();
    private readonly Grid _hostGrid = new();
    private readonly Dictionary<object, FrameworkElement> _viewCache = new();
    private bool _updatingHost;

    public TransitionContentControl()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        RenderTransform = _translate;
    }

    /// <summary>
    /// Bound to <c>CurrentPageViewModel</c> (or fallback via <see cref="ContentControl.Content"/>).
    /// Keeps the binding alive while <see cref="ContentControl.Content"/> permanently holds the cached page host Grid.
    /// </summary>
    public object? ActivePage
    {
        get => GetValue(ActivePageProperty);
        set => SetValue(ActivePageProperty, value);
    }

    /// <summary>Number of cached page views currently retained in memory (for diagnostics and tests).</summary>
    public int CachedViewCount => _viewCache.Count;

    /// <summary>
    /// Pre-creates and caches the view for a ViewModel in the collapsed state so the user's first navigation click
    /// to a heavy page (such as <see cref="EditorView"/> or <see cref="SettingsView"/>) is instantaneous.
    /// </summary>
    public FrameworkElement? PrewarmView(object? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _hostGrid)) return null;
        EnsureHostInstalled();

        if (_viewCache.TryGetValue(viewModel, out var existing))
            return existing;

        var view = CreateViewFor(viewModel);
        if (view is null) return null;

        view.DataContext = viewModel;
        view.Visibility = Visibility.Collapsed;
        _viewCache[viewModel] = view;
        _hostGrid.Children.Add(view);
        return view;
    }

    private static void OnActivePageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TransitionContentControl control)
            control.SwitchToPage(e.OldValue, e.NewValue);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (_updatingHost || ReferenceEquals(newContent, _hostGrid)) return;
        SwitchToPage(oldContent, newContent);
    }

    private void SwitchToPage(object? oldContent, object? newContent)
    {
        if (newContent is null || ReferenceEquals(newContent, _hostGrid))
            return;

        var pageView = GetOrCreateCachedView(newContent);
        if (pageView is null)
        {
            PlayTransition(oldContent, newContent);
            return;
        }

        EnsureHostInstalled();

        FrameworkElement? previousVisible = null;
        foreach (var kvp in _viewCache)
        {
            if (ReferenceEquals(kvp.Value, pageView))
            {
                if (kvp.Value.Visibility == Visibility.Visible)
                    previousVisible = kvp.Value;
                kvp.Value.Visibility = Visibility.Visible;
            }
            else if (kvp.Value.Visibility == Visibility.Visible)
            {
                previousVisible = kvp.Value;
                kvp.Value.Visibility = Visibility.Collapsed;
            }
        }

        if (!ReferenceEquals(previousVisible, pageView))
            PlayTransition(previousVisible, pageView);
    }

    private void EnsureHostInstalled()
    {
        if (ReferenceEquals(base.Content, _hostGrid)) return;
        _updatingHost = true;
        try
        {
            base.Content = _hostGrid;
        }
        finally
        {
            _updatingHost = false;
        }
    }

    private FrameworkElement? GetOrCreateCachedView(object viewModel)
    {
        if (_viewCache.TryGetValue(viewModel, out var cached))
            return cached;

        var created = CreateViewFor(viewModel);
        if (created is null) return null;

        created.DataContext = viewModel;
        created.Visibility = Visibility.Collapsed;
        _viewCache[viewModel] = created;
        _hostGrid.Children.Add(created);
        return created;
    }

    private static FrameworkElement? CreateViewFor(object viewModel) => viewModel switch
    {
        LibraryViewModel => new LibraryView(),
        EditorViewModel => new EditorView(),
        LogViewModel => new LogView(),
        SettingsViewModel => new SettingsView(),
        ManualViewModel => new ManualView(),
        FrameworkElement fe => fe,
        _ => null,
    };

    private void PlayTransition(object? oldItem, object? newItem)
    {
        if (ReferenceEquals(oldItem, newItem) || newItem is null || !IsLoaded)
            return;

        if (!ThemeManager.AreAnimationsEnabled)
        {
            BeginAnimation(OpacityProperty, null);
            _translate.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1.0;
            _translate.Y = 0.0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(140));

        var fade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };
        var slide = new DoubleAnimation(6.0, 0.0, duration) { EasingFunction = ease };

        BeginAnimation(OpacityProperty, fade);
        _translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }
}
