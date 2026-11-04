using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartMacro.Core.Models;
using SmartMacro.Services;
using SmartMacro.ViewModels;

namespace SmartMacro.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayOpeningChoreography();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsLoaded)
            PlayOpeningChoreography();
    }

    /// <summary>
    /// Signature Settings opening choreography:
    /// 1) Cinematic zoom-settle (1.08 -> 1.0) on the KuroLogic custom banner.
    /// 2) Diagonal neon holographic light-sweep shimmer across the banner.
    /// 3) Staggered 35 ms cascade entrance across all settings cards.
    /// </summary>
    public void PlayOpeningChoreography()
    {
        PlayBannerShowcaseAnimation();

        if (SettingsCardsStack is null) return;

        if (!ThemeManager.AreAnimationsEnabled)
        {
            foreach (UIElement child in SettingsCardsStack.Children)
            {
                child.BeginAnimation(OpacityProperty, null);
                child.Opacity = 1;
                child.RenderTransform = null;
            }
            return;
        }

        var index = 0;
        foreach (UIElement child in SettingsCardsStack.Children)
        {
            var translate = new TranslateTransform(0, 16);
            child.RenderTransform = translate;
            child.Opacity = 0;

            var delay = TimeSpan.FromMilliseconds(index * 35);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            child.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
            index++;
        }
    }

    private void PlayBannerShowcaseAnimation()
    {
        if (!ThemeManager.AreAnimationsEnabled)
        {
            if (BannerScale is not null)
            {
                BannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                BannerScale.ScaleX = 1.0;
                BannerScale.ScaleY = 1.0;
            }
            return;
        }

        if (BannerScale is not null)
        {
            var zoomAnim = new DoubleAnimation(1.085, 1.0, TimeSpan.FromMilliseconds(540))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoomAnim);
            BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoomAnim);
        }

        if (BannerSweepTranslate is not null)
        {
            var sweepAnim = new DoubleAnimation(-360, 920, TimeSpan.FromMilliseconds(720))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            BannerSweepTranslate.BeginAnimation(TranslateTransform.XProperty, sweepAnim);
        }
    }

    private void BannerHero_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ThemeManager.AreAnimationsEnabled || BannerHeroStage is null || BannerParallax is null || BannerScale is null) return;
        var pos = e.GetPosition(BannerHeroStage);
        var w = Math.Max(1.0, BannerHeroStage.ActualWidth);
        var h = Math.Max(1.0, BannerHeroStage.ActualHeight);

        var nx = (pos.X / w - 0.5) * 2.0; // -1..1
        var ny = (pos.Y / h - 0.5) * 2.0; // -1..1

        var tx = new DoubleAnimation(-nx * 8.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var ty = new DoubleAnimation(-ny * 5.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var scale = new DoubleAnimation(1.028, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        BannerParallax.BeginAnimation(TranslateTransform.XProperty, tx);
        BannerParallax.BeginAnimation(TranslateTransform.YProperty, ty);
        BannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void BannerHero_MouseLeave(object sender, MouseEventArgs e)
    {
        if (BannerParallax is null || BannerScale is null) return;

        var reset = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var scaleReset = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BannerParallax.BeginAnimation(TranslateTransform.XProperty, reset);
        BannerParallax.BeginAnimation(TranslateTransform.YProperty, reset);
        BannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleReset);
        BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleReset);
    }

    private void BannerHero_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PlayBannerShowcaseAnimation();
    }

    // Records the key combination pressed while a shortcut box has focus.
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string which } || DataContext is not SettingsViewModel vm) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Let Tab move focus normally, and wait while only modifier keys are held.
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
        {
            e.Handled = true;
            return;
        }

        var modifiers = KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= KeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= KeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= KeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= KeyModifiers.Windows;

        vm.CaptureHotkey(which, new HotkeyBinding(modifiers, KeyInterop.VirtualKeyFromKey(key)));
        e.Handled = true;
    }
}
