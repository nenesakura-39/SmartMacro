using System.IO.Packaging;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using SmartMacro.Converters;

namespace SmartMacro.AppTests;

/// <summary>
/// Hosts one persistent WPF STA dispatcher thread for headless UI, ViewModel, and resource-dependent tests.
/// </summary>
internal static class StaTestRunner
{
    private static readonly object InitGate = new();
    private static Dispatcher? _dispatcher;
    private static bool _appInitialized;

    private static Dispatcher EnsureDispatcher()
    {
        if (_dispatcher is not null)
            return _dispatcher;

        lock (InitGate)
        {
            if (_dispatcher is not null)
                return _dispatcher;

            using var ready = new ManualResetEventSlim(false);
            Dispatcher? created = null;
            var thread = new Thread(() =>
            {
                created = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(created));
                EnsureApplicationInitialized();
                ready.Set();
                Dispatcher.Run();
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "SmartMacro.App.Tests.STA";
            thread.Start();
            ready.Wait();
            _dispatcher = created!;
        }

        return _dispatcher;
    }

    private static void EnsureApplicationInitialized()
    {
        if (_appInitialized)
            return;

        if (!UriParser.IsKnownScheme("pack"))
            _ = PackUriHelper.UriSchemePack;

        Application.ResourceAssembly ??= typeof(global::SmartMacro.App).Assembly;

        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        if (!app.Resources.Contains("BoolToVisibility"))
        {
            string[] themeUris =
            [
                "pack://application:,,,/SmartMacro;component/Themes/Colors.xaml",
                "pack://application:,,,/SmartMacro;component/Themes/Tokens.xaml",
                "pack://application:,,,/SmartMacro;component/Themes/Icons.xaml",
                "pack://application:,,,/SmartMacro;component/Themes/Controls.xaml",
                "pack://application:,,,/SmartMacro;component/Themes/Brand.xaml",
            ];

            foreach (var uri in themeUris)
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });

            app.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            app.Resources["InverseBoolToVisibility"] = new InverseBoolToVisibilityConverter();
            app.Resources["NullToVisibility"] = new NullToVisibilityConverter();
            app.Resources["InverseBool"] = new InverseBoolConverter();
            app.Resources["PageToBool"] = new PageToBoolConverter();
            app.Resources["LevelToBrush"] = new LevelToBrushConverter();
            app.Resources["LevelToGlyph"] = new LevelToGlyphConverter();
        }

        _appInitialized = true;
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Run<object?>(() =>
        {
            action();
            return null;
        });
    }

    public static T Run<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var dispatcher = EnsureDispatcher();
        if (dispatcher.CheckAccess())
            return func();

        Exception? captured = null;
        var result = default(T)!;
        dispatcher.Invoke(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();

        return result;
    }

    public static Task RunAsync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return RunAsync<object?>(async () =>
        {
            await func();
            return null;
        });
    }

    public static Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var dispatcher = EnsureDispatcher();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var value = await func();
                tcs.TrySetResult(value);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    public static void DoEvents()
    {
        var dispatcher = EnsureDispatcher();
        if (dispatcher.CheckAccess())
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            return;
        }

        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    public static Dispatcher CurrentDispatcher => EnsureDispatcher();
}
