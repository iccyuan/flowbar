using System.Runtime.InteropServices;

namespace FlowBar.Core;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    public bool Same(in RECT o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>Thin P/Invoke layer. Everything here is physical pixels.</summary>
internal static partial class Native
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_CHILD = 0x40000000;
    public const long WS_POPUP = 0x80000000;
    public const long WS_VISIBLE = 0x10000000;
    public const long WS_CLIPSIBLINGS = 0x04000000;

    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
    public const int MA_NOACTIVATE = 3;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int cmd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    public static partial int MapWindowPoints(IntPtr from, IntPtr to, ref RECT rect, uint count);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint cmd);
    public const uint GW_HWNDPREV = 3;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_ESCAPE = 0x1B;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(IntPtr hWnd, Span<char> buffer, int max);

    public static string GetClassName(IntPtr hWnd)
    {
        Span<char> buf = stackalloc char[256];
        int n = GetClassNameW(hWnd, buf, buf.Length);
        return n > 0 ? new string(buf[..n]) : string.Empty;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT pt);

    [LibraryImport("gdi32.dll")]
    public static partial uint GetPixel(IntPtr hdc, int x, int y);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIfTable2(out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(IntPtr table);

    // ---- processes -------------------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr h, uint ms);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, Span<char> buffer, ref uint size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;

    /// <summary>Full image path of a process, or "" when it is gone or protected.</summary>
    public static string ProcessImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            Span<char> buf = stackalloc char[1024];
            uint n = (uint)buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref n) ? new string(buf[..(int)n]) : "";
        }
        finally { CloseHandle(h); }
    }

    /// <summary>False once the process has exited. Processes we may not open count as alive.</summary>
    public static bool ProcessAlive(int pid)
    {
        IntPtr h = OpenProcess(SYNCHRONIZE, false, (uint)pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5; // ERROR_ACCESS_DENIED: exists, protected
        try { return WaitForSingleObject(h, 0) != 0; }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFOW info, uint size, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// The 16px shell icon of a file; the caller destroys it. With <paramref name="byExtension"/> the
    /// file need not exist: the icon is the stock one for its extension (e.g. "app.exe").
    /// </summary>
    public static unsafe IntPtr SmallIconOf(string path, bool byExtension = false)
    {
        var info = new SHFILEINFOW();
        uint flags = 0x100 | 0x1; // SHGFI_ICON | SHGFI_SMALLICON
        if (byExtension) flags |= 0x10; // SHGFI_USEFILEATTRIBUTES
        SHGetFileInfoW(path, byExtension ? 0x80u /* FILE_ATTRIBUTE_NORMAL */ : 0u, ref info, (uint)sizeof(SHFILEINFOW), flags);
        return info.hIcon;
    }

    // ---- connection tables -----------------------------------------------

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedUdpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    // ---- helpers -------------------------------------------------------

    public static long GetStyle(IntPtr h) => GetWindowLongPtr(h, GWL_STYLE).ToInt64();
    public static long GetExStyle(IntPtr h) => GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
    public static void SetStyle(IntPtr h, long v) => SetWindowLongPtr(h, GWL_STYLE, new IntPtr(v));
    public static void SetExStyle(IntPtr h, long v) => SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(v));

    /// <summary>Window rect expressed in the client coordinates of <paramref name="relativeTo"/>.</summary>
    public static RECT RectIn(IntPtr hWnd, IntPtr relativeTo)
    {
        GetWindowRect(hWnd, out var r);
        MapWindowPoints(IntPtr.Zero, relativeTo, ref r, 2);
        return r;
    }

    public static uint DpiOf(IntPtr hWnd)
    {
        uint d = hWnd != IntPtr.Zero ? GetDpiForWindow(hWnd) : 0;
        if (d != 0) return d;
        var m = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(m, 0, out var x, out _) == 0 ? x : 96;
    }

    public static MONITORINFO MonitorInfoFor(ref RECT rect)
    {
        var m = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(m, ref mi);
        return mi;
    }
}
