using System.Windows;
using System.Windows.Media.Animation;

namespace SmartMacro.Views;

/// <summary>
/// Pure abstract cybernetic startup loading screen (intentionally free of any software name or brand logo).
/// Displays geometric telemetry rings, an oscillating laser scanline, and smooth stage progression before
/// morphing into the main studio window.
/// </summary>
public partial class SplashLoadingWindow : Window
{
    private double _currentProgress;

    public SplashLoadingWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (TryFindResource("ReactorSpinStoryboard") is Storyboard sb)
            sb.Begin(this, isControllable: true);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var scaleIn = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(OpacityProperty, fadeIn);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleIn);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleIn);
    }

    public void ReportStage(double progress01, string stageCode)
    {
        _currentProgress = Math.Clamp(progress01, 0.0, 1.0);
        StageText.Text = stageCode;
        PercentText.Text = $"{(int)Math.Round(_currentProgress * 100)}%";

        var trackWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 356.0;
        var targetWidth = Math.Max(6.0, trackWidth * _currentProgress);

        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ProgressFill.BeginAnimation(WidthProperty, anim);
    }

    public Task CompleteAndCloseAsync()
    {
        ReportStage(1.0, "SYSTEM READY");
        var tcs = new TaskCompletionSource();

        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(240))
        {
            BeginTime = TimeSpan.FromMilliseconds(90),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var scaleOut = new DoubleAnimation(1.0, 1.04, TimeSpan.FromMilliseconds(240))
        {
            BeginTime = TimeSpan.FromMilliseconds(90),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        fadeOut.Completed += (_, _) =>
        {
            try
            {
                if (Resources["ReactorSpinStoryboard"] is Storyboard sb)
                    sb.Stop(this);
                if (IsLoaded)
                    Close();
            }
            catch
            {
                // Window was already closed before the fade-out animation finished.
            }
            finally
            {
                tcs.TrySetResult();
            }
        };

        Closed += (_, _) => tcs.TrySetResult();

        BeginAnimation(OpacityProperty, fadeOut);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleOut);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleOut);
        return tcs.Task;
    }
}
