using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Matdol.Controls;
using Matdol.Core;
using Matdol.Views;

namespace Matdol;

public partial class App : Application
{
    public static readonly TimeSpan MinSplashTime = AppInfo.SplashTime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private static Mutex? _instanceMark;

    private static bool AnotherInstanceRunning()
    {
        try
        {
            var m = _instanceMark = new Mutex(false, $"matdol.instance.{Environment.UserName}");
            bool first = false;
            var answered = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                bool got;
                try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; }
                first = got;
                answered.Set();
                if (!got)
                {
                    try { m.WaitOne(); } catch (AbandonedMutexException) { } catch { return; }
                }
                Thread.Sleep(Timeout.Infinite);
            }) { IsBackground = true, Name = "matdol instance mark" };
            holder.Start();
            if (!answered.Wait(500)) return false;
            return !first;
        }
        catch { return false; }
    }

    private static bool OtherMatdolWindowExists()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id) continue;
                    if (!OperatingSystem.IsWindows() || p.MainWindowHandle != IntPtr.Zero) return true;
                }
            }
            return false;
        }
        catch { return true; }
    }

    private static void RunWithoutSplash(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var win = new MainWindow();
        if (desktop.Args is { Length: > 0 } args && System.IO.File.Exists(args[0]))
            win.OpenFileOnStartup = args[0];
        double scale;
        try { scale = win.Screens.Primary?.Scaling ?? 1; } catch { scale = 1; }

        AppInfo.PreloadAsync(scale).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            desktop.MainWindow = win;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            win.Show();
            win.Activate();
            StartupTrace.Mark("main window shown (no splash)"); StartupTrace.Flush();
        }));
    }

    private static void LoadAndRun(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash, double scale)
    {
        var splashFrames = Task.Run(() =>
        {
            var f = AppInfo.LoadSplashFrames();
            try { f.Prepare(scale); } catch { }
            return f;
        });
        var rest = AppInfo.PreloadAsync(scale);

        Task.WhenAll(splashFrames, rest).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            FrameSet frames;
            try { frames = splashFrames.Result; } catch { frames = AppInfo.LoadSplashFrames(); }

            var win = new MainWindow();
            if (desktop.Args is { Length: > 0 } args && System.IO.File.Exists(args[0]))
                win.OpenFileOnStartup = args[0];

            DispatcherTimer.RunOnce(() =>
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                splash.StartAnimation(frames);
                var started = System.Diagnostics.Stopwatch.StartNew();
                var swap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                swap.Tick += (_, _) =>
                {
                    if (splash.Elapsed < MinSplashTime && started.Elapsed < MinSplashTime + TimeSpan.FromSeconds(2)) return;
                    swap.Stop();
                    splash.Close();
                    desktop.MainWindow = win;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    win.Show();
                    win.Activate();
                    StartupTrace.Mark("main window shown"); StartupTrace.Flush();
                };
                swap.Start();
            }, TimeSpan.FromMilliseconds(30));
        }, DispatcherPriority.Background));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace.Mark("Avalonia ready");
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool another = AnotherInstanceRunning() && OtherMatdolWindowExists();
            bool showSplash = AppSettings.Current.ShowSplash;
            StartupTrace.Mark($"another instance: {another}, show splash option: {showSplash}");
            if (another || !showSplash)
            {
                RunWithoutSplash(desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var splash = new SplashWindow();
            double scale = splash.ExpectedScaling;
            splash.SetStill(AppInfo.LoadSplashStill(scale));

            bool loading = false;
            void Begin()
            {
                if (loading) return;
                loading = true;
                DispatcherTimer.RunOnce(() => LoadAndRun(desktop, splash, scale), TimeSpan.FromMilliseconds(30));
            }
            splash.FirstRendered += () => { StartupTrace.Mark("splash first frame"); Begin(); };
            splash.Opened += (_, _) => DispatcherTimer.RunOnce(Begin, TimeSpan.FromMilliseconds(500));
            splash.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
