using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FlowBar.Core;
using FlowBar.UI;
using Microsoft.Win32;

namespace FlowBar;

public partial class App : Application
{
    private const string MutexName = "FlowBar.SingleInstance";
    private const string ExitEventName = "FlowBar.ExitRequest";
    private const string ShowEventName = "FlowBar.ShowRequest";
    private const string AppsEventName = "FlowBar.AppsRequest";

    private Mutex? _mutex;
    private AppConfig _cfg = null!; // assigned in OnStartup, after the config directory is known
    private NetSampler? _sampler;
    private TrafficLog? _traffic;
    private AppTraffic? _apps;
    private TaskbarWindow? _window;
    private EventWaitHandle? _exitEvent;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _appsEvent;
    private DispatcherTimer? _themeDebounce;

    public static new App Current => (App)Application.Current;
    public AppConfig Config => _cfg;
    public AppTraffic AppTraffic => _apps!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // `--dir <path>` is the command-line twin of FLOWBAR_DIR (the environment does not survive a
        // UAC relaunch). Must run before anything touches AppConfig or Log.
        int di = Array.FindIndex(e.Args, a => a.Equals("--dir", StringComparison.OrdinalIgnoreCase));
        if (di >= 0 && di + 1 < e.Args.Length) Environment.SetEnvironmentVariable("FLOWBAR_DIR", e.Args[di + 1]);

        bool wantExit = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));
        // `--wait-for <pid>`: we were launched to replace that instance (elevation restart); let it
        // release the mutex and the taskbar first.
        int wi = Array.FindIndex(e.Args, a => a.Equals("--wait-for", StringComparison.OrdinalIgnoreCase));
        if (wi >= 0 && wi + 1 < e.Args.Length && int.TryParse(e.Args[wi + 1], out int waitPid))
        {
            try { System.Diagnostics.Process.GetProcessById(waitPid).WaitForExit(8000); } catch { }
        }
        // A test instance (FLOWBAR_DIR set) gets its own mutex and signals, so it never talks to the real one.
        string suffix = Environment.GetEnvironmentVariable("FLOWBAR_DIR") is { Length: > 0 } ? ".test" : "";
        _mutex = new Mutex(true, MutexName + suffix, out bool first);
        if (!first)
        {
            // Another FlowBar owns the taskbar. Hand the request over instead of vanishing silently:
            // `--exit` asks it to quit, `--apps` to open the per-program window, a plain double-click
            // to show its detail popup.
            bool wantApps = e.Args.Any(a => a.Equals("--apps", StringComparison.OrdinalIgnoreCase));
            string name = (wantExit ? ExitEventName : wantApps ? AppsEventName : ShowEventName) + suffix;
            bool delivered = false;
            try { EventWaitHandle.OpenExisting(name).Set(); delivered = true; } catch { }
            if (!delivered && !wantExit)
                MessageBox.Show("FlowBar 已经在运行。", "FlowBar", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (wantExit) { Shutdown(); return; }

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Write("unhandled: " + a.Exception);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("fatal: " + a.ExceptionObject);

        _cfg = AppConfig.Load();
        ApplyTheme();

        _traffic = new TrafficLog();
        _sampler = new NetSampler { Selection = _cfg.Adapter };
        _sampler.Sampled += snap => _traffic.Add(snap.DeltaRx, snap.DeltaTx);
        _sampler.Start(_cfg.IntervalMs);
        _apps = new AppTraffic();
        _apps.Start();

        CreateWindow();
        var w = _window!;

        // Debug hooks: open a surface right away so it can be screenshotted without synthetic input.
        if (e.Args.Contains("--font")) Dispatcher.BeginInvoke(w.OpenFontDialog, DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--popup")) Dispatcher.BeginInvoke(w.OpenPopup, DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--menu")) Dispatcher.BeginInvoke(w.DebugOpenMenu, DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--tip")) Dispatcher.BeginInvoke(w.DebugShowTip, DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--apps")) Dispatcher.BeginInvoke(w.OpenApps, DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--conns")) Dispatcher.BeginInvoke(() => w.OpenApps(connections: true), DispatcherPriority.ApplicationIdle);
        if (Array.Find(e.Args, a => a.StartsWith("--stats", StringComparison.OrdinalIgnoreCase)) is { } st)
            Dispatcher.BeginInvoke(() => w.OpenStats(st.Length > 8 && int.TryParse(st[8..], out int d) ? d : 0), DispatcherPriority.ApplicationIdle);

        // Theme / display changes come from a hidden broadcast window owned by SystemEvents, which is
        // the only way to hear WM_SETTINGCHANGE once we live as a child of the taskbar.
        _themeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _themeDebounce.Tick += (_, _) => { _themeDebounce.Stop(); ApplyTheme(); _window?.RefreshTheme(); };
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { _themeDebounce.Stop(); _themeDebounce.Start(); });
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => _window?.Relayout());

        // `FlowBar.exe --exit` from a second process asks us to quit cleanly (restores the taskbar).
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName + suffix);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke(Shutdown), null, -1, true);
        // A second launch (double-clicking the exe again) asks us to open the detail popup.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + suffix);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => _window?.OpenPopup()), null, -1, false);
        _appsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppsEventName + suffix);
        ThreadPool.RegisterWaitForSingleObject(_appsEvent, (_, _) => Dispatcher.BeginInvoke(() => _window?.OpenApps()), null, -1, false);

        // WPF start-up leaves a lot of touched-but-cold pages behind; hand them back once we're idle.
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        trim.Tick += (_, _) => { trim.Stop(); TrimWorkingSet(); };
        trim.Start();

        Log.Write($"started v{typeof(App).Assembly.GetName().Version}" + (AppTraffic.IsElevated ? " (elevated)" : ""));
    }

    /// <summary>
    /// Relaunches FlowBar through UAC and quits. The new instance waits for us to exit, then opens
    /// the apps window straight away since that is what the user was after.
    /// </summary>
    public void RestartElevated()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--wait-for {Environment.ProcessId} --apps"
                + (Environment.GetEnvironmentVariable("FLOWBAR_DIR") is { Length: > 0 } dir ? $" --dir \"{dir}\"" : ""),
        };
        try { System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Log.Write("elevate: " + ex.Message); return; } // UAC declined
        Shutdown();
    }

    private void CreateWindow()
    {
        _window = new TaskbarWindow(_cfg, _sampler!, _traffic!);
        _window.Died += OnWindowDied;
        _window.Initialize();
    }

    /// <summary>
    /// Explorer restarted and destroyed the taskbar together with our embedded child window. The
    /// old TaskbarWindow is unusable now; tear it down and attach a new one to the new taskbar.
    /// </summary>
    private void OnWindowDied()
    {
        Log.Write("widget window destroyed (Explorer restart?), recreating");
        var old = _window;
        _window = null;
        if (old != null)
        {
            old.Died -= OnWindowDied;
            try { old.Shutdown(); } catch (Exception ex) { Log.Write("old window shutdown: " + ex.Message); }
        }
        // Give the new Explorer a moment to build Shell_TrayWnd; TaskbarHost falls back to floating
        // and keeps re-probing every tick if it is still not there.
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        retry.Tick += (_, _) => { retry.Stop(); CreateWindow(); };
        retry.Start();
    }

    public static void TrimWorkingSet()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }

    public void ApplyTheme()
    {
        bool light = Theme.AppsUseLight();
        Set("MenuBg", light ? "#F9F9F9" : "#2B2B2B");
        Set("MenuFg", light ? "#1B1B1B" : "#F3F4F6");
        Set("MenuSubtle", light ? "#6B7280" : "#9CA3AF");
        Set("MenuHover", light ? "#0F000000" : "#17FFFFFF");
        Set("MenuBorder", light ? "#20000000" : "#30FFFFFF");
        Set("GlassTint", light ? "#B8FFFFFF" : "#C0202020");
        Set("Accent", light ? "#2563EB" : "#60A5FA");
        Set("AccentTx", light ? "#DB2777" : "#F472B6");
        var (rx, rxEnd, tx, txEnd) = Theme.ChartColors(_cfg.ColorScheme, light);
        Resources["ChartRx"] = new SolidColorBrush(rx);
        Resources["ChartRxEnd"] = new SolidColorBrush(rxEnd);
        Resources["ChartTx"] = new SolidColorBrush(tx);
        Resources["ChartTxEnd"] = new SolidColorBrush(txEnd);
    }

    private void Set(string key, string hex) => Resources[key] = new SolidColorBrush(Theme.Hex(hex));

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _window?.Shutdown();
            _apps?.Dispose();
            _sampler?.Dispose();
            _traffic?.Save();
            // No config save here on purpose: every setting change already saves itself, and a
            // whole-file save at exit let a stale instance (one that was started earlier and quit
            // later) overwrite choices made in another one.
        }
        catch (Exception ex) { Log.Write("exit: " + ex.Message); }
        _mutex?.Dispose();
        base.OnExit(e);
        // WPF sometimes never finishes shutting down when a popup (context menu, detail popup) was
        // open at the time: everything above has run and the mutex is released, but the dispatcher
        // keeps pumping and the process lingers. Nothing is left to clean up, so don't let it.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(2000);
            Log.Write("dispatcher did not stop after OnExit, forcing exit");
            Environment.Exit(e.ApplicationExitCode);
        }) { IsBackground = true, Name = "exit-watchdog" };
        watchdog.Start();
    }
}
