using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlowBar.Core;

/// <summary>One network interface as seen by GetIfTable2, with the per-second rates we derived.</summary>
public sealed class IfaceStat
{
    public Guid Id;
    public string Name = "";
    public string Description = "";
    public uint Type;
    public bool Up;
    public bool Hardware;
    public bool Filter;
    public ulong InOctets;
    public ulong OutOctets;
    /// <summary>bytes per second since the previous sample</summary>
    public double Rx;
    public double Tx;

    public const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    public const uint IF_TYPE_TUNNEL = 131;
    public const uint IF_TYPE_PROP_VIRTUAL = 53;

    public bool IsLoopback => Type == IF_TYPE_SOFTWARE_LOOPBACK;
    public bool IsTunnel => Type == IF_TYPE_TUNNEL || Type == IF_TYPE_PROP_VIRTUAL;
    /// <summary>
    /// Interfaces worth showing to the user: physical adapters in any state, plus virtual ones
    /// (TUN, VPN, Hyper-V) that are up and have actually moved bytes. Filter-driver mirrors and
    /// loopback never show.
    /// </summary>
    public bool IsListable => !Filter && !IsLoopback && (Hardware || (Up && InOctets + OutOctets > 0));
}

public sealed class Snapshot
{
    public DateTime Time;
    public IReadOnlyList<IfaceStat> Interfaces = Array.Empty<IfaceStat>();
    /// <summary>Aggregate rates (bytes/s) for the currently selected interface set.</summary>
    public double Rx;
    public double Tx;
    /// <summary>Bytes moved since the previous sample for the selected set.</summary>
    public ulong DeltaRx;
    public ulong DeltaTx;
    public ulong SessionRx;
    public ulong SessionTx;
    public string SelectionName = "";
}

/// <summary>
/// Samples interface octet counters on a background timer and publishes rates.
/// Selection: null = automatic (physical adapters only), otherwise a specific interface GUID.
/// </summary>
public sealed class NetSampler : IDisposable
{
    public const int HistoryLength = 180;

    private readonly object _gate = new();
    private Timer? _timer;
    private int _intervalMs = 1000;
    private Dictionary<Guid, (ulong rx, ulong tx)> _prev = new();
    private long _prevTicks;
    private ulong _sessionRx, _sessionTx;
    private Guid? _selection;

    private readonly double[] _histRx = new double[HistoryLength];
    private readonly double[] _histTx = new double[HistoryLength];
    private int _histHead; // next write index
    private int _histCount;

    public event Action<Snapshot>? Sampled;

    public Guid? Selection
    {
        get { lock (_gate) return _selection; }
        set { lock (_gate) _selection = value; }
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            _intervalMs = Math.Clamp(value, 250, 10000);
            _timer?.Change(0, _intervalMs);
        }
    }

    public void Start(int intervalMs)
    {
        _intervalMs = Math.Clamp(intervalMs, 250, 10000);
        _prevTicks = Stopwatch.GetTimestamp();
        _timer = new Timer(_ => Tick(), null, 0, _intervalMs);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>Copies the rate history (oldest first) into the given spans. Returns the count.</summary>
    public int CopyHistory(Span<double> rx, Span<double> tx)
    {
        lock (_gate)
        {
            int n = Math.Min(_histCount, Math.Min(rx.Length, tx.Length));
            int start = (_histHead - n + HistoryLength) % HistoryLength;
            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % HistoryLength;
                rx[i] = _histRx[idx];
                tx[i] = _histTx[idx];
            }
            return n;
        }
    }

    private void Tick()
    {
        List<IfaceStat> rows;
        try { rows = ReadTable(); }
        catch (Exception ex) { Log.Write("GetIfTable2 failed: " + ex.Message); return; }

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _prevTicks) / (double)Stopwatch.Frequency;
        if (seconds <= 0.05) seconds = _intervalMs / 1000.0;

        var snap = new Snapshot { Time = DateTime.Now, Interfaces = rows };
        var next = new Dictionary<Guid, (ulong, ulong)>(rows.Count);
        Guid? sel;
        lock (_gate) sel = _selection;

        // Decide which rows count towards the aggregate.
        var chosen = Choose(rows, sel, out snap.SelectionName);

        ulong dRx = 0, dTx = 0;
        foreach (var r in rows)
        {
            next[r.Id] = (r.InOctets, r.OutOctets);
            if (_prev.TryGetValue(r.Id, out var p) && r.InOctets >= p.rx && r.OutOctets >= p.tx)
            {
                ulong drx = r.InOctets - p.rx, dtx = r.OutOctets - p.tx;
                r.Rx = drx / seconds;
                r.Tx = dtx / seconds;
                if (chosen.Contains(r)) { dRx += drx; dTx += dtx; }
            }
        }

        bool first = _prev.Count == 0;
        _prev = next;
        _prevTicks = now;
        if (first) return; // no rates yet

        _sessionRx += dRx;
        _sessionTx += dTx;
        snap.DeltaRx = dRx;
        snap.DeltaTx = dTx;
        snap.Rx = dRx / seconds;
        snap.Tx = dTx / seconds;
        snap.SessionRx = _sessionRx;
        snap.SessionTx = _sessionTx;

        lock (_gate)
        {
            _histRx[_histHead] = snap.Rx;
            _histTx[_histHead] = snap.Tx;
            _histHead = (_histHead + 1) % HistoryLength;
            if (_histCount < HistoryLength) _histCount++;
        }

        Sampled?.Invoke(snap);
    }

    /// <summary>
    /// Automatic mode sums every hardware adapter that is up. Filter-driver mirrors (WFP, QoS, AV
    /// drivers) report the same octets as the adapter they sit on and must never be counted.
    /// If no hardware adapter is up we fall back to all real, non-tunnel interfaces.
    /// </summary>
    public static HashSet<IfaceStat> Choose(IReadOnlyList<IfaceStat> rows, Guid? selection, out string name)
    {
        var set = new HashSet<IfaceStat>();
        if (selection is Guid g)
        {
            var r = rows.FirstOrDefault(x => x.Id == g);
            if (r != null) { set.Add(r); name = r.Name; return set; }
            name = "(网卡不可用)";
            return set;
        }

        foreach (var r in rows)
            if (r.Up && r.Hardware && !r.Filter && !r.IsLoopback) set.Add(r);
        if (set.Count == 0)
            foreach (var r in rows)
                if (r.Up && !r.Filter && !r.IsLoopback && !r.IsTunnel) set.Add(r);

        name = set.Count == 1 ? set.First().Name : set.Count == 0 ? "无网卡" : $"自动（{set.Count} 块网卡）";
        return set;
    }

    // ---- GetIfTable2 -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags; // bit0 HardwareInterface, bit1 FilterInterface, bit2 ConnectorPresent ...
        public uint OperStatus;                  // 1 = up
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos, InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors, OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    private static unsafe List<IfaceStat> ReadTable()
    {
        Debug.Assert(sizeof(MIB_IF_ROW2) == 1352, "MIB_IF_ROW2 layout drifted");
        uint err = Native.GetIfTable2(out var table);
        if (err != 0) throw new InvalidOperationException($"error {err}");
        try
        {
            uint count = *(uint*)table;
            var rows = (MIB_IF_ROW2*)((byte*)table + 8);
            var list = new List<IfaceStat>((int)count);
            for (uint i = 0; i < count; i++)
            {
                MIB_IF_ROW2* r = &rows[i];
                list.Add(new IfaceStat
                {
                    Id = r->InterfaceGuid,
                    Name = new string(r->Alias),
                    Description = new string(r->Description),
                    Type = r->Type,
                    Up = r->OperStatus == 1,
                    Hardware = (r->InterfaceAndOperStatusFlags & 1) != 0,
                    Filter = (r->InterfaceAndOperStatusFlags & 2) != 0,
                    InOctets = r->InOctets,
                    OutOctets = r->OutOctets,
                });
            }
            return list;
        }
        finally { Native.FreeMibTable(table); }
    }
}
