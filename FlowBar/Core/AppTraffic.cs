using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Media;
using System.Windows.Threading;

namespace FlowBar.Core;

/// <summary>One process as the apps view sees it.</summary>
public sealed class AppRow
{
    public int Pid;
    public string Name = "";
    public string Path = "";
    public ImageSource? Icon;
    /// <summary>bytes per second over the last tick</summary>
    public double Rx, Tx;
    /// <summary>bytes since FlowBar started</summary>
    public ulong SessionRx, SessionTx;
    public int Connections;
}

/// <summary>
/// Per-program bookkeeping: drains <see cref="EtwNetMonitor"/> once a second into rates, session
/// totals and the daily <see cref="AppTrafficLog"/>, and reads the connection table while someone
/// is looking at it. Runs on the UI thread (DispatcherTimer); the work per tick is a few dictionary
/// passes.
/// </summary>
public sealed class AppTraffic : IDisposable
{
    public static bool IsElevated { get; } = CheckElevated();

    private readonly ProcessInfo _procs = new();
    private readonly Dictionary<int, AppRow> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private EtwNetMonitor? _etw;
    private long _lastTicks;
    private bool _wantConns;
    private Dictionary<ConnKey, (double rx, double tx)> _flowRates = new();
    private Dictionary<(bool tcp, Endpoint ep), (double rx, double tx)> _endpointRates = new();
    private Dictionary<(bool tcp, ushort port), (double rx, double tx)> _portRates = new();

    public AppTrafficLog Log { get; } = new();
    public bool RatesAvailable => _etw is { Running: true };
    /// <summary>Why per-program rates are missing, or null when they work.</summary>
    public string? Unavailable => RatesAvailable ? null : !IsElevated ? "需要管理员权限" : _etw?.Error ?? "未启动";
    public IReadOnlyList<ConnectionRow> Connections { get; private set; } = Array.Empty<ConnectionRow>();
    public IEnumerable<AppRow> Rows => _rows.Values;
    public event Action? Updated;

    public AppTraffic()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (IsElevated)
        {
            _etw = new EtwNetMonitor();
            if (!_etw.Start()) Core.Log.Write("etw unavailable: " + _etw.Error);
        }
        _lastTicks = Stopwatch.GetTimestamp();
        if (RatesAvailable) _timer.Start();
    }

    /// <summary>True while a window shows connections; the table is only read then.</summary>
    public bool WantConnections
    {
        get => _wantConns;
        set
        {
            _wantConns = value;
            if (value)
            {
                if (!_timer.IsEnabled) { _lastTicks = Stopwatch.GetTimestamp(); _timer.Start(); }
                Tick();
            }
            else if (!RatesAvailable) _timer.Stop();
        }
    }

    public ProcessEntry ProcessOf(int pid) => _procs.Get(pid);

    /// <summary>Icon for a program name: from a live process when there is one, else a guess.</summary>
    public ImageSource? IconFor(string name)
    {
        foreach (var r in _rows.Values)
            if (r.Icon != null && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return r.Icon;
        return _procs.IconForName(name);
    }

    /// <summary>Current rate of one connection, matched against the ETW flows seen in the last tick.</summary>
    public (double rx, double tx) RateOf(ConnectionRow c)
    {
        if (c.Tcp)
        {
            // ETW lists (daddr, saddr); which one is "local" depends on direction, so try both.
            _flowRates.TryGetValue(new ConnKey(true, c.Remote, c.Local), out var r1);
            _flowRates.TryGetValue(new ConnKey(true, c.Local, c.Remote), out var r2);
            return (r1.rx + r2.rx, r1.tx + r2.tx);
        }
        // UDP rows carry no remote end: match on the local endpoint, or on the port alone for wildcard binds.
        if (!c.Local.Unspecified) { _endpointRates.TryGetValue((false, c.Local), out var r); return r; }
        _portRates.TryGetValue((false, c.Local.Port), out var pr);
        return pr;
    }

    private void Tick()
    {
        try { TickCore(); }
        catch (Exception ex) { Core.Log.Write("app traffic tick: " + ex); }
    }

    private void TickCore()
    {
        long now = Stopwatch.GetTimestamp();
        double sec = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        if (sec <= 0.1) sec = 1;

        NetDelta? delta = RatesAvailable ? _etw!.Take() : null;
        List<ConnectionRow>? conns = _wantConns ? Core.Connections.Read() : null;

        Dictionary<int, int>? counts = null;
        if (conns != null)
        {
            counts = new();
            foreach (var c in conns) { counts.TryGetValue(c.Pid, out int n); counts[c.Pid] = n + 1; }
        }

        var seen = new HashSet<int>();
        foreach (var r in _rows.Values) { r.Rx = r.Tx = 0; if (counts != null) r.Connections = 0; }

        if (delta != null)
        {
            foreach (var (pid, d) in delta.ByPid)
            {
                var row = Row(pid);
                seen.Add(pid);
                row.Rx = d.rx / sec;
                row.Tx = d.tx / sec;
                row.SessionRx += d.rx;
                row.SessionTx += d.tx;
                Log.Add(row.Name, d.rx, d.tx);
            }
            RebuildFlowRates(delta, sec);
        }
        if (counts != null)
            foreach (var (pid, n) in counts) { Row(pid).Connections = n; seen.Add(pid); }

        // Idle rows stay while the process lives (they carry session totals); drop the rest.
        List<int>? gone = null;
        foreach (var (pid, row) in _rows)
        {
            if (seen.Contains(pid)) continue;
            bool empty = row.SessionRx + row.SessionTx == 0 && row.Connections == 0;
            if (empty || !Native.ProcessAlive(pid)) (gone ??= new()).Add(pid);
        }
        if (gone != null) foreach (var pid in gone) _rows.Remove(pid);
        foreach (var pid in _rows.Keys) seen.Add(pid);
        _procs.Prune(seen);

        if (conns != null) Connections = conns;
        Updated?.Invoke();
    }

    private AppRow Row(int pid)
    {
        if (_rows.TryGetValue(pid, out var row)) return row;
        var p = _procs.Get(pid);
        row = new AppRow { Pid = pid, Name = p.Name, Path = p.Path, Icon = p.Icon };
        _rows[pid] = row;
        return row;
    }

    private void RebuildFlowRates(NetDelta delta, double sec)
    {
        var flows = new Dictionary<ConnKey, (double rx, double tx)>(delta.ByConn.Count);
        var eps = new Dictionary<(bool, Endpoint), (double rx, double tx)>();
        var ports = new Dictionary<(bool, ushort), (double rx, double tx)>();
        foreach (var (k, v) in delta.ByConn)
        {
            var r = (v.rx / sec, v.tx / sec);
            flows[k] = r;
            if (k.Tcp) continue;
            // UDP: either end may be the local one; credit both, nobody lists the remote one as local anyway.
            Add(eps, (false, k.A), r); Add(eps, (false, k.B), r);
            Add(ports, (false, k.A.Port), r); Add(ports, (false, k.B.Port), r);
        }
        _flowRates = flows; _endpointRates = eps; _portRates = ports;

        static void Add<T>(Dictionary<T, (double rx, double tx)> d, T key, (double rx, double tx) r) where T : notnull
        {
            d.TryGetValue(key, out var cur);
            d[key] = (cur.rx + r.rx, cur.tx + r.tx);
        }
    }

    private static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _etw?.Dispose();
        _etw = null;
        Log.Save();
    }
}
