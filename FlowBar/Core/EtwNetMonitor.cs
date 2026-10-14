using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlowBar.Core;

/// <summary>Bytes moved since the previous <see cref="EtwNetMonitor.Take"/>, per process and per flow.</summary>
public sealed class NetDelta
{
    public readonly Dictionary<int, (ulong rx, ulong tx)> ByPid = new();
    public readonly Dictionary<ConnKey, (ulong rx, ulong tx)> ByConn = new();
}

/// <summary>
/// Real-time consumer of the Microsoft-Windows-Kernel-Network ETW provider, the same source Task
/// Manager and Resource Monitor use for per-process network usage. Every TCP/UDP send and receive
/// arrives as one event carrying the owning pid, the byte count and both endpoints. Starting a trace
/// session needs administrator rights; <see cref="Start"/> reports failure instead of throwing.
/// </summary>
public sealed unsafe class EtwNetMonitor : IDisposable
{
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const string SessionName = "FlowBar-KernelNetwork";
    private const ulong KeywordTcpIp = 0x10, KeywordUdpIp = 0x20;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x20000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const ulong INVALID_HANDLE = ulong.MaxValue;

    private static EtwNetMonitor? s_instance;

    private readonly object _gate = new();
    private NetDelta _delta = new();
    private ulong _session;
    private ulong _trace = INVALID_HANDLE;
    private IntPtr _props;
    private IntPtr _nameBuf;
    private Thread? _thread;

    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public long Events;

    /// <summary>Starts the session. Returns false (with <see cref="Error"/> set) when ETW refused us.</summary>
    public bool Start()
    {
        if (Running) return true;
        try
        {
            s_instance = this;
            _props = AllocProperties();
            // A session with our name may survive a crash; it belongs to nobody now.
            ControlTraceW(0, SessionName, _props, EVENT_TRACE_CONTROL_STOP);
            uint err = StartTraceW(out _session, SessionName, _props);
            if (err != 0) { Error = Describe(err); Cleanup(); return false; }
            err = EnableTraceEx2(_session, in KernelNetwork, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 5, KeywordTcpIp | KeywordUdpIp, 0, 0, IntPtr.Zero);
            if (err != 0) { Error = Describe(err); Stop(); return false; }

            _nameBuf = Marshal.StringToHGlobalUni(SessionName);
            var log = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = _nameBuf,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventCallback = (IntPtr)(delegate* unmanaged[Stdcall]<EVENT_RECORD*, void>)&OnEvent,
            };
            _trace = OpenTraceW(ref log);
            if (_trace == INVALID_HANDLE) { Error = Describe((uint)Marshal.GetLastWin32Error()); Stop(); return false; }

            _thread = new Thread(() =>
            {
                ulong h = _trace;
                uint r = ProcessTrace(ref h, 1, IntPtr.Zero, IntPtr.Zero);
                if (r != 0 && Running) Log.Write($"etw ProcessTrace ended: {r}");
            }) { IsBackground = true, Name = "etw-net" };
            Running = true;
            _thread.Start();
            Log.Write("etw network session started");
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("etw start failed: " + ex);
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        bool was = Running;
        Running = false;
        if (_session != 0)
        {
            ControlTraceW(_session, null, _props, EVENT_TRACE_CONTROL_STOP);
            _session = 0;
        }
        if (_trace != INVALID_HANDLE)
        {
            CloseTrace(_trace);
            _trace = INVALID_HANDLE;
        }
        _thread?.Join(1500);
        _thread = null;
        Cleanup();
        if (was) Log.Write("etw network session stopped");
    }

    public void Dispose() => Stop();

    /// <summary>Hands over everything counted since the previous call and starts a fresh tally.</summary>
    public NetDelta Take()
    {
        lock (_gate)
        {
            var d = _delta;
            _delta = new NetDelta();
            return d;
        }
    }

    private void Cleanup()
    {
        if (_props != IntPtr.Zero) { Marshal.FreeHGlobal(_props); _props = IntPtr.Zero; }
        if (_nameBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_nameBuf); _nameBuf = IntPtr.Zero; }
        if (s_instance == this) s_instance = null;
    }

    private static string Describe(uint err) => err switch
    {
        5 => "需要管理员权限",
        1314 => "需要管理员权限（缺少 SeSystemProfilePrivilege）",
        183 => "同名跟踪会话已存在",
        _ => $"ETW 错误 {err}",
    };

    // ---- event callback ---------------------------------------------------

    // Microsoft-Windows-Kernel-Network event ids. Payload (v4): PID u32, size u32, daddr u32, saddr u32,
    // dport u16, sport u16, ... ; (v6): PID, size, daddr[16], saddr[16], dport, sport, ...
    private const ushort TcpSend4 = 10, TcpRecv4 = 11, TcpSend6 = 26, TcpRecv6 = 27;
    private const ushort UdpSend4 = 42, UdpRecv4 = 43, UdpSend6 = 58, UdpRecv6 = 59;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnEvent(EVENT_RECORD* rec)
    {
        try
        {
            var self = s_instance;
            if (self == null || rec->EventHeader.ProviderId != KernelNetwork) return;
            ushort id = rec->EventHeader.EventDescriptor.Id;
            bool tcp, v6, send;
            switch (id)
            {
                case TcpSend4: tcp = true; v6 = false; send = true; break;
                case TcpRecv4: tcp = true; v6 = false; send = false; break;
                case TcpSend6: tcp = true; v6 = true; send = true; break;
                case TcpRecv6: tcp = true; v6 = true; send = false; break;
                case UdpSend4: tcp = false; v6 = false; send = true; break;
                case UdpRecv4: tcp = false; v6 = false; send = false; break;
                case UdpSend6: tcp = false; v6 = true; send = true; break;
                case UdpRecv6: tcp = false; v6 = true; send = false; break;
                default: return;
            }
            byte* p = (byte*)rec->UserData;
            int need = v6 ? 44 : 20;
            if (p == null || rec->UserDataLength < need) return;
            int pid = (int)*(uint*)p;
            uint size = *(uint*)(p + 4);
            Endpoint a, b;
            if (v6)
            {
                a = Endpoint.From16(p + 8, *(ushort*)(p + 40));
                b = Endpoint.From16(p + 24, *(ushort*)(p + 42));
            }
            else
            {
                a = Endpoint.From4(*(uint*)(p + 8), *(ushort*)(p + 16));
                b = Endpoint.From4(*(uint*)(p + 12), *(ushort*)(p + 18));
            }
            var key = new ConnKey(tcp, a, b);
            lock (self._gate)
            {
                var d = self._delta;
                d.ByPid.TryGetValue(pid, out var pp);
                d.ByPid[pid] = send ? (pp.rx, pp.tx + size) : (pp.rx + size, pp.tx);
                d.ByConn.TryGetValue(key, out var cc);
                d.ByConn[key] = send ? (cc.rx, cc.tx + size) : (cc.rx + size, cc.tx);
                self.Events++;
            }
        }
        catch
        {
            // Never let an exception escape into ETW's consumer thread.
        }
    }

    // ---- native -------------------------------------------------------------

    private static IntPtr AllocProperties()
    {
        int size = sizeof(EVENT_TRACE_PROPERTIES) + 2 * 1024 * sizeof(char);
        IntPtr mem = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)mem, size).Clear();
        var p = (EVENT_TRACE_PROPERTIES*)mem;
        p->Wnode.BufferSize = (uint)size;
        p->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        p->Wnode.ClientContext = 1; // QPC timestamps
        p->LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
        p->BufferSize = 64;        // KB
        p->MinimumBuffers = 4;
        p->MaximumBuffers = 16;
        p->FlushTimer = 1;         // deliver at least once a second even when buffers stay half empty
        p->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
        p->LogFileNameOffset = 0;  // real-time only, no file
        return mem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint BufferContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public fixed byte TimeZone[172]; // TIME_ZONE_INFORMATION
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_LOGFILEW
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public uint BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong handle, string name, IntPtr props);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong handle, string? name, IntPtr props, uint code);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint code, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ref ulong handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);
}
