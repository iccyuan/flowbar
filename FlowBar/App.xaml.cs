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

    private Mutex? _mutex;
    private AppConfig _cfg = new();
    private NetSampler? _sampler;
    private TrafficLog? _traffic;
    private TaskbarWindow? _window;
    private EventWaitHandle? _exitEvent;
    private DispatcherTimer? _themeDebounce;

    public static new App Current => (App)Application.Current;
    public AppConfig Config => _cfg;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool wantExit = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (wantExit)
            {
                try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { }
            }
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

        _window = new TaskbarWindow(_cfg, _sampler, _traffic);
        _window.Initialize();

        // Theme / display changes come from a hidden broadcast window owned by SystemEvents, which is
        // the only way to hear WM_SETTINGCHANGE once we live as a child of the taskbar.
        _themeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _themeDebounce.Tick += (_, _) => { _themeDebounce.Stop(); ApplyTheme(); _window?.RefreshTheme(); };
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { _themeDebounce.Stop(); _themeDebounce.Start(); });
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => _window?.Relayout());

        // `FlowBar.exe --exit` from a second process asks us to quit cleanly (restores the taskbar).
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke(Shutdown), null, -1, true);

        // WPF start-up leaves a lot of touched-but-cold pages behind; hand them back once we're idle.
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        trim.Tick += (_, _) => { trim.Stop(); TrimWorkingSet(); };
        trim.Start();

        Log.Write($"started v{typeof(App).Assembly.GetName().Version}");
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
        Set("Accent", light ? "#2563EB" : "#60A5FA");    // download (blue → cyan in the chart)
        Set("AccentTx", light ? "#DB2777" : "#F472B6");  // upload (pink → violet in the chart)
    }

    private void Set(string key, string hex) => Resources[key] = new SolidColorBrush(Theme.Hex(hex));

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _window?.Shutdown();
            _sampler?.Dispose();
            _traffic?.Save();
            _cfg.Save();
        }
        catch (Exception ex) { Log.Write("exit: " + ex.Message); }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
