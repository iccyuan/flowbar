namespace FlowBar.Core;

/// <summary>
/// Puts our widget inside Explorer's taskbar (Shell_TrayWnd) as a child window, the way
/// TrafficMonitor does. Two taskbar flavours are handled:
///  * classic (Windows 10, StartAllBack, ExplorerPatcher): ReBarWindow32 holds the task buttons and
///    is shrunk to make room, so the widget never overlaps running apps;
///  * Windows 11 XAML taskbar: there is nothing to shrink, the widget overlays the bar next to the
///    notification area (or after Start).
/// All coordinates are physical pixels in Shell_TrayWnd client space.
/// </summary>
public sealed class TaskbarHost
{
    private IntPtr _hwnd;
    private IntPtr _tray, _rebar, _notify, _start;
    private bool _classic;
    private bool _horizontal = true;
    private RECT _trayRect;
    private RECT _lastPlaced;
    private bool _embedded;

    public Placement Placement { get; set; } = Placement.Tray;

    public bool IsHorizontal => _horizontal;
    public bool TaskbarFound => _tray != IntPtr.Zero && Native.IsWindow(_tray);
    public IntPtr TrayHwnd => _tray;
    /// <summary>Taskbar thickness across its main axis, physical pixels.</summary>
    public int Thickness => _horizontal ? _trayRect.Height : _trayRect.Width;
    /// <summary>Taskbar rect in screen coordinates.</summary>
    public RECT TrayRect => _trayRect;
    /// <summary>Where the widget currently sits, screen coordinates.</summary>
    public RECT PlacedScreenRect
    {
        get { Native.GetWindowRect(_hwnd, out var r); return r; }
    }
    /// <summary>True when the taskbar hugs the top or left edge of its monitor (popups open the other way).</summary>
    public bool TaskbarAtStartEdge
    {
        get
        {
            var mi = Native.MonitorInfoFor(ref _trayRect);
            return _horizontal
                ? _trayRect.Top - mi.rcMonitor.Top < mi.rcMonitor.Bottom - _trayRect.Bottom
                : _trayRect.Left - mi.rcMonitor.Left < mi.rcMonitor.Right - _trayRect.Right;
        }
    }

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Finds the taskbar windows. Returns false when Explorer isn't running.</summary>
    public bool Locate()
    {
        _tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (_tray == IntPtr.Zero) return false;
        Native.GetWindowRect(_tray, out _trayRect);
        _horizontal = _trayRect.Width >= _trayRect.Height;
        _rebar = Native.FindWindowExW(_tray, IntPtr.Zero, "ReBarWindow32", null);
        _notify = Native.FindWindowExW(_tray, IntPtr.Zero, "TrayNotifyWnd", null);
        _start = Native.FindWindowExW(_tray, IntPtr.Zero, "Start", null);
        var xaml = Native.FindWindowExW(_tray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        // StartAllBack / ExplorerPatcher keep the Win10 rebar alive and visible; stock Win11 hides it.
        _classic = _rebar != IntPtr.Zero && Native.IsWindowVisible(_rebar) && xaml == IntPtr.Zero;
        if (!_classic && _rebar != IntPtr.Zero && Native.IsWindowVisible(_rebar) && xaml != IntPtr.Zero)
        {
            // Both present: trust the rebar only if it actually spans the bar.
            var rr = Native.RectIn(_rebar, _tray);
            int span = _horizontal ? rr.Width : rr.Height;
            _classic = span > Thickness * 3;
        }
        return true;
    }

    /// <summary>Converts our top-level WPF window into a child of the taskbar.</summary>
    public bool Embed()
    {
        if (_hwnd == IntPtr.Zero || !TaskbarFound) return false;
        long style = Native.GetStyle(_hwnd);
        style &= ~Native.WS_POPUP;
        style |= Native.WS_CHILD | Native.WS_CLIPSIBLINGS;
        Native.SetStyle(_hwnd, style);
        long ex = Native.GetExStyle(_hwnd);
        ex &= ~Native.WS_EX_TOPMOST;
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetExStyle(_hwnd, ex);
        Native.SetParent(_hwnd, _tray);
        _embedded = true;
        _lastPlaced = default;
        return true;
    }

    /// <summary>Detaches from the taskbar (used when switching to floating mode or on exit).</summary>
    public void Detach()
    {
        if (!_embedded) return;
        RestoreRebar();
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
        {
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            Native.SetParent(_hwnd, IntPtr.Zero);
            long style = Native.GetStyle(_hwnd);
            style &= ~Native.WS_CHILD;
            style |= Native.WS_POPUP;
            Native.SetStyle(_hwnd, style);
        }
        _embedded = false;
    }

    /// <summary>
    /// Called every tick. Re-finds the taskbar after an Explorer restart, re-shrinks the rebar when
    /// Explorer has grown it back, and keeps the widget where it belongs.
    /// Returns true when the taskbar geometry changed (caller may want to re-layout).
    /// </summary>
    public bool Maintain(int lengthPx, bool force = false)
    {
        if (_hwnd == IntPtr.Zero) return false;

        if (!TaskbarFound || Native.GetParent(_hwnd) != _tray)
        {
            if (!Locate()) { Native.ShowWindow(_hwnd, Native.SW_HIDE); return false; }
            Embed();
            force = true;
        }

        Native.GetWindowRect(_tray, out var tr);
        bool geometryChanged = !tr.Same(_trayRect);
        if (geometryChanged)
        {
            _trayRect = tr;
            _horizontal = tr.Width >= tr.Height;
        }

        Place(lengthPx, force || geometryChanged);
        Raise();
        return geometryChanged;
    }

    /// <summary>
    /// Puts the widget back on top of its taskbar siblings. Explorer's Windows 11 XAML island raises
    /// itself on activation changes; if it ends up above us, clicks in our spot go to the taskbar.
    /// </summary>
    public void Raise()
    {
        if (!_embedded || _hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd)) return;
        IntPtr above = Native.GetWindow(_hwnd, Native.GW_HWNDPREV);
        while (above != IntPtr.Zero && !Native.IsWindowVisible(above)) above = Native.GetWindow(above, Native.GW_HWNDPREV);
        if (above == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void Place(int len, bool force)
    {
        int thick = Thickness;
        var notify = _notify != IntPtr.Zero ? Native.RectIn(_notify, _tray) : default;
        var rebar = _rebar != IntPtr.Zero ? Native.RectIn(_rebar, _tray) : default;
        var start = _start != IntPtr.Zero ? Native.RectIn(_start, _tray) : default;
        bool haveNotify = _notify != IntPtr.Zero;

        int trayLen = _horizontal ? _trayRect.Width : _trayRect.Height;
        int notifyStart = haveNotify ? (_horizontal ? notify.Left : notify.Top) : trayLen;
        int startEnd = _start != IntPtr.Zero && Native.IsWindowVisible(_start) ? (_horizontal ? start.Right : start.Bottom) : 0;
        // Classic taskbars anchor the rebar right after Start; keep whatever Explorer chose.
        int anchor = _classic ? Math.Max(startEnd, 0) : startEnd;

        int pos; // along the main axis
        if (Placement == Placement.Left)
        {
            pos = anchor;
            if (_classic) ShrinkRebar(rebar, anchor + len, notifyStart);
        }
        else
        {
            pos = notifyStart - len;
            if (_classic) ShrinkRebar(rebar, anchor, pos);
        }

        RECT target = _horizontal
            ? new RECT { Left = pos, Top = 0, Right = pos + len, Bottom = thick }
            : new RECT { Left = 0, Top = pos, Right = thick, Bottom = pos + len };

        if (!force && target.Same(_lastPlaced) && Native.IsWindowVisible(_hwnd)) return;
        _lastPlaced = target;
        Native.SetWindowPos(_hwnd, Native.HWND_TOP, target.Left, target.Top, target.Width, target.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Resizes ReBarWindow32 so it spans [from, to) along the main axis.</summary>
    private void ShrinkRebar(RECT rebar, int from, int to)
    {
        if (_rebar == IntPtr.Zero || to <= from) return;
        int curFrom = _horizontal ? rebar.Left : rebar.Top;
        int curTo = _horizontal ? rebar.Right : rebar.Bottom;
        if (curFrom == from && curTo == to) return;
        if (_horizontal)
            Native.SetWindowPos(_rebar, IntPtr.Zero, from, rebar.Top, to - from, rebar.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        else
            Native.SetWindowPos(_rebar, IntPtr.Zero, rebar.Left, from, rebar.Width, to - from, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Gives the rebar its full span back.</summary>
    public void RestoreRebar()
    {
        if (!_classic || _rebar == IntPtr.Zero || !Native.IsWindow(_rebar) || !TaskbarFound) return;
        var rebar = Native.RectIn(_rebar, _tray);
        var notify = _notify != IntPtr.Zero ? Native.RectIn(_notify, _tray) : default;
        var start = _start != IntPtr.Zero ? Native.RectIn(_start, _tray) : default;
        int trayLen = _horizontal ? _trayRect.Width : _trayRect.Height;
        int to = _notify != IntPtr.Zero ? (_horizontal ? notify.Left : notify.Top) : trayLen;
        int from = _start != IntPtr.Zero && Native.IsWindowVisible(_start) ? (_horizontal ? start.Right : start.Bottom) : (_horizontal ? rebar.Left : rebar.Top);
        ShrinkRebar(rebar, from, to);
    }
}
