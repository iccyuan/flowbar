using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace FlowBar.Core;

/// <summary>
/// One socket endpoint: 16 bytes of address (an IPv4 address occupies the first four, the rest is
/// zero) and the port, both exactly as the kernel stores them (network byte order). Keeping the raw
/// form lets rows from the connection table and ETW events be compared without conversion.
/// </summary>
public readonly record struct Endpoint(ulong A0, ulong A1, ushort Port)
{
    public static Endpoint From4(uint addr, ushort port) => new(addr, 0, port);
    public static unsafe Endpoint From16(byte* p, ushort port) => new(*(ulong*)p, *(ulong*)(p + 8), port);
    public bool Unspecified => A0 == 0 && A1 == 0;
    public int HostPort => BinaryPrimitives.ReverseEndianness(Port);

    public string Text(bool v6)
    {
        if (!v6) return $"{new IPAddress((uint)A0)}:{HostPort}";
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(b, A0);
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], A1);
        return $"[{new IPAddress(b)}]:{HostPort}";
    }
}

/// <summary>A flow as ETW reports it: protocol plus the two endpoints in payload order (daddr, saddr).</summary>
public readonly record struct ConnKey(bool Tcp, Endpoint A, Endpoint B);

public sealed class ConnectionRow
{
    public int Pid;
    public bool Tcp;
    public bool V6;
    public uint State;
    public Endpoint Local;
    public Endpoint Remote;

    public bool Established => Tcp && State == 5;
    public bool Listening => Tcp && State == 2;

    public string StateText => !Tcp ? "" : State switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RCVD", 5 => "ESTABLISHED", 6 => "FIN_WAIT1",
        7 => "FIN_WAIT2", 8 => "CLOSE_WAIT", 9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB",
        _ => State.ToString(),
    };
    public string ProtoText => (Tcp ? "TCP" : "UDP") + (V6 ? "6" : "");
    public string LocalText => Local.Text(V6);
    public string RemoteText => Tcp && State != 2 ? Remote.Text(V6) : "";
}

/// <summary>Reads the TCP/UDP tables with owning pids (GetExtendedTcpTable / GetExtendedUdpTable). No privileges needed.</summary>
public static class Connections
{
    private const uint AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private static bool _loggedError;

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP4 { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TCP6 { public fixed byte LocalAddr[16]; public uint LocalScope, LocalPort; public fixed byte RemoteAddr[16]; public uint RemoteScope, RemotePort, State, Pid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UDP4 { public uint LocalAddr, LocalPort, Pid; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UDP6 { public fixed byte LocalAddr[16]; public uint LocalScope, LocalPort, Pid; }

    public static List<ConnectionRow> Read()
    {
        var list = new List<ConnectionRow>(256);
        ReadTable(list, tcp: true, v6: false);
        ReadTable(list, tcp: true, v6: true);
        ReadTable(list, tcp: false, v6: false);
        ReadTable(list, tcp: false, v6: true);
        return list;
    }

    private static unsafe void ReadTable(List<ConnectionRow> list, bool tcp, bool v6)
    {
        uint family = v6 ? AF_INET6 : AF_INET;
        uint size = 0;
        uint err = Query(IntPtr.Zero, ref size, tcp, family);
        if (err != ERROR_INSUFFICIENT_BUFFER && err != 0) { Fail(err); return; }
        size += 8192; // room for connections that appear between the two calls
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            err = Query(buf, ref size, tcp, family);
            if (err != 0) { Fail(err); return; }
            byte* p = (byte*)buf;
            uint n = *(uint*)p;
            p += 4;
            for (uint i = 0; i < n; i++)
            {
                var row = new ConnectionRow { Tcp = tcp, V6 = v6 };
                if (tcp && !v6)
                {
                    var r = (TCP4*)p;
                    row.State = r->State; row.Pid = (int)r->Pid;
                    row.Local = Endpoint.From4(r->LocalAddr, (ushort)r->LocalPort);
                    row.Remote = Endpoint.From4(r->RemoteAddr, (ushort)r->RemotePort);
                    p += sizeof(TCP4);
                }
                else if (tcp)
                {
                    var r = (TCP6*)p;
                    row.State = r->State; row.Pid = (int)r->Pid;
                    row.Local = Endpoint.From16(r->LocalAddr, (ushort)r->LocalPort);
                    row.Remote = Endpoint.From16(r->RemoteAddr, (ushort)r->RemotePort);
                    p += sizeof(TCP6);
                }
                else if (!v6)
                {
                    var r = (UDP4*)p;
                    row.Pid = (int)r->Pid;
                    row.Local = Endpoint.From4(r->LocalAddr, (ushort)r->LocalPort);
                    p += sizeof(UDP4);
                }
                else
                {
                    var r = (UDP6*)p;
                    row.Pid = (int)r->Pid;
                    row.Local = Endpoint.From16(r->LocalAddr, (ushort)r->LocalPort);
                    p += sizeof(UDP6);
                }
                list.Add(row);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static uint Query(IntPtr buf, ref uint size, bool tcp, uint family) => tcp
        ? Native.GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0)
        : Native.GetExtendedUdpTable(buf, ref size, false, family, UDP_TABLE_OWNER_PID, 0);

    private static void Fail(uint err)
    {
        if (_loggedError) return;
        _loggedError = true;
        Log.Write($"connection table failed: error {err}");
    }
}
