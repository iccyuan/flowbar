using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlowBar.Core;

namespace FlowBar.UI;

/// <summary>
/// The widget itself: two small lines of text (upload / download) that live inside the taskbar.
/// Built in code because the layout (one or two lines, column widths) depends on the taskbar
/// height and the font, which we only know at runtime.
/// </summary>
public sealed class TaskbarWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly NetSampler _sampler;
    private readonly TrafficLog _traffic;
    private readonly TaskbarHost _host = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private IntPtr _hwnd;
    private bool _embedded;
    private int _lengthPx;          // widget length along the taskbar axis, physical px
    private int _thickPx;           // widget thickness, physical px
    private bool _twoLines = true;
    private double _fontSize = 10;
    private readonly SolidColorBrush _fgUp = new(Colors.White);
    private readonly SolidColorBrush _fgDown = new(Colors.White);
    private bool _darkBackdrop = true;
    private static readonly SolidColorBrush HitTestBrush = new(Color.FromArgb(1, 0, 0, 0));
    private readonly Grid _root = new();
    private readonly Border _pill = new();
    private TextBlock? _rxValue, _rxUnit, _txValue, _txUnit;
    private DetailPopup? _popup;
    private Snapshot? _last;
    private bool _dragging;
    private POINT _dragStart;
    private RECT _dragRect;
    private bool _hiddenForFullscreen;

    public TaskbarWindow(AppConfig cfg, NetSampler sampler, TrafficLog traffic)
    {
        _cfg = cfg;
        _sampler = sampler;
        _traffic = traffic;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        Width = 100; Height = 30;

        _pill.Child = _root;
        _pill.Background = Brushes.Transparent;
        Content = _pill;
        // Layered windows hit-test by pixel alpha, so a fully transparent background would let clicks
        // between the two lines fall through to the taskbar. Alpha 1 is invisible but solid to the mouse.
        _root.Background = HitTestBrush;

        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftUp;
        MouseMove += OnMouseMove;
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        // The tooltip shows ~0.5s after enter; grab its backdrop now, while nothing of ours is there.
        MouseEnter += (_, _) =>
        {
            Native.GetWindowRect(_hwnd, out var r);
            FrostedGlass.CaptureAround((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, 400, 160, 260);
        };
        ToolTipService.SetInitialShowDelay(_root, 500);
        ToolTipService.SetShowDuration(_root, 8000);

        _sampler.Sampled += snap => Dispatcher.BeginInvoke(() => OnSample(snap));
        _tick.Tick += (_, _) => Tick();
    }

    // ---- lifecycle -------------------------------------------------------

    public void Initialize()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _host.Attach(_hwnd);
        ApplyPlacement(initial: true);
        _tick.Start();
    }

    public void Shutdown()
    {
        _tick.Stop();
        _popup?.Close();
        _host.Detach();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private double Scale => Native.DpiOf(_hwnd) / 96.0;

    /// <summary>Switches between embedded and floating modes according to config.</summary>
    public void ApplyPlacement(bool initial = false)
    {
        bool wantEmbed = _cfg.Placement != Placement.Floating;
        _host.Placement = _cfg.Placement;

        if (wantEmbed)
        {
            if (!_host.Locate())
            {
                Log.Write("taskbar not found, falling back to floating");
                wantEmbed = false;
            }
        }

        if (wantEmbed)
        {
            if (!_embedded)
            {
                if (IsVisible) Hide();
                long ex = Native.GetExStyle(_hwnd);
                Native.SetExStyle(_hwnd, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_TOPMOST);
                _host.Embed();
                _embedded = true;
                Topmost = false;
            }
            Relayout();
            if (!IsVisible) Show();
            _host.Maintain(_lengthPx, force: true);
            RefreshTheme();
        }
        else
        {
            if (_embedded)
            {
                _host.Detach();
                _embedded = false;
            }
            long ex = Native.GetExStyle(_hwnd);
            Native.SetExStyle(_hwnd, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Relayout();
            RefreshTheme();
            if (!IsVisible) Show();
            PlaceFloating();
        }
    }

    private void PlaceFloating()
    {
        double s = Scale;
        int w = (int)Math.Round(Width * s), h = (int)Math.Round(Height * s);
        var tray = _host.TrayRect;
        int x = _cfg.FloatX, y = _cfg.FloatY;
        if (x < 0 || y < 0)
        {
            // default: bottom-right of the work area, above the taskbar
            var probe = new RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
            var mi = Native.MonitorInfoFor(ref probe);
            x = mi.rcWork.Right - w - 24;
            y = mi.rcWork.Bottom - h - 24;
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    // ---- layout ----------------------------------------------------------

    /// <summary>Recomputes font size, column widths and the window size from the taskbar geometry.</summary>
    public void Relayout()
    {
        double s = Scale;
        double thickDip;
        if (_embedded)
        {
            _thickPx = _host.Thickness;
            thickDip = _thickPx / s;
        }
        else
        {
            thickDip = 34;
            _thickPx = (int)Math.Round(thickDip * s);
        }

        _twoLines = _cfg.Layout switch
        {
            Layout.TwoLines => true,
            Layout.OneLine => false,
            _ => thickDip >= 24 && _host.IsHorizontal || !_host.IsHorizontal,
        };
        if (_embedded && !_host.IsHorizontal) _twoLines = true;

        _fontSize = _cfg.FontSize > 0 ? _cfg.FontSize
            : _twoLines ? Math.Clamp(Math.Floor((thickDip - 3) / 2 / 1.3), 9, 13)
            : Math.Clamp(Math.Floor(thickDip * 0.46), 9, 14);

        var weight = FontDialog.WeightOf(_cfg.FontWeight);
        var typeface = new Typeface(new FontFamily(_cfg.FontFamily + ", Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        double ppd = s;
        double Measure(string t) => new FormattedText(t, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, _fontSize, Brushes.Black, ppd).WidthIncludingTrailingWhitespace;

        double arrowW = _cfg.ShowArrows ? Math.Ceiling(Measure("↑")) : 0;
        double valueW = Math.Ceiling(Measure(Formatting.WidestValue(_cfg.Unit)));
        double unitW = Math.Ceiling(Measure(Formatting.WidestUnit(_cfg.Unit)));
        const double gap = 3, pad = 7;

        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.ColumnDefinitions.Clear();

        double lineH = Math.Ceiling(_fontSize * 1.3);
        TextBlock Make(string text, double w, TextAlignment align, Brush fg, double opacity = 1)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = typeface.FontFamily,
                FontWeight = weight,
                FontSize = _fontSize,
                Foreground = fg,
                Opacity = opacity,
                TextAlignment = align,
                VerticalAlignment = VerticalAlignment.Center,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = lineH,
                Width = w > 0 ? w : double.NaN,
            };
            RenderOptions.SetClearTypeHint(tb, ClearTypeHint.Auto);
            System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular); // digits don't jitter as values change
            return tb;
        }

        double contentW;
        if (_twoLines)
        {
            _root.RowDefinitions.Add(new RowDefinition());
            _root.RowDefinitions.Add(new RowDefinition());
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(arrowW > 0 ? arrowW + gap : 0) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueW) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(unitW + gap) });
            void Row(int r, string arrow, Brush fg, out TextBlock val, out TextBlock unit)
            {
                if (arrowW > 0) { var a = Make(arrow, arrowW, TextAlignment.Left, fg, 0.72); Grid.SetRow(a, r); _root.Children.Add(a); }
                val = Make("0", valueW, TextAlignment.Right, fg); Grid.SetRow(val, r); Grid.SetColumn(val, 1); _root.Children.Add(val);
                unit = Make("", unitW, TextAlignment.Left, fg); unit.Margin = new Thickness(gap, 0, 0, 0); Grid.SetRow(unit, r); Grid.SetColumn(unit, 2); _root.Children.Add(unit);
            }
            Row(0, "↑", _fgUp, out _txValue, out _txUnit);
            Row(1, "↓", _fgDown, out _rxValue, out _rxUnit);
            contentW = (arrowW > 0 ? arrowW + gap : 0) + valueW + gap + unitW;
        }
        else
        {
            _root.RowDefinitions.Add(new RowDefinition());
            const double mid = 10;
            void Cols()
            {
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(arrowW > 0 ? arrowW + gap : 0) });
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueW) });
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(unitW + gap) });
            }
            Cols();
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mid) });
            Cols();
            void Group(int c0, string arrow, Brush fg, out TextBlock val, out TextBlock unit)
            {
                if (arrowW > 0) { var a = Make(arrow, arrowW, TextAlignment.Left, fg, 0.72); Grid.SetColumn(a, c0); _root.Children.Add(a); }
                val = Make("0", valueW, TextAlignment.Right, fg); Grid.SetColumn(val, c0 + 1); _root.Children.Add(val);
                unit = Make("", unitW, TextAlignment.Left, fg); unit.Margin = new Thickness(gap, 0, 0, 0); Grid.SetColumn(unit, c0 + 2); _root.Children.Add(unit);
            }
            Group(0, "↑", _fgUp, out _txValue, out _txUnit);
            Group(4, "↓", _fgDown, out _rxValue, out _rxUnit);
            contentW = 2 * ((arrowW > 0 ? arrowW + gap : 0) + valueW + gap + unitW) + mid;
        }

        double wDip = Math.Ceiling(contentW + 2 * pad);
        if (_embedded && !_host.IsHorizontal)
        {
            // vertical taskbar: the widget spans the bar's width and takes two lines of height
            _lengthPx = (int)Math.Round((lineH * 2 + 8) * s);
            Width = Math.Round(_thickPx / s);
            Height = Math.Round(_lengthPx / s);
        }
        else
        {
            _lengthPx = (int)Math.Round(wDip * s);
            Width = Math.Round(_lengthPx / s);
            Height = Math.Round(_thickPx / s);
        }
        _pill.Padding = new Thickness(pad, 0, pad, 0);

        if (!_embedded)
        {
            _pill.CornerRadius = new CornerRadius(8);
            _pill.Background = new SolidColorBrush(Theme.AppsUseLight() ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB4, 0x20, 0x20, 0x20));
            _pill.BorderBrush = new SolidColorBrush(Theme.AppsUseLight() ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            _pill.BorderThickness = new Thickness(1);
        }
        else
        {
            _pill.CornerRadius = new CornerRadius(0);
            _pill.Background = Brushes.Transparent;
            _pill.BorderThickness = new Thickness(0);
        }

        if (_last != null) Render(_last);
        if (_embedded) _host.Maintain(_lengthPx, force: true);
    }

    /// <summary>Picks a text colour that reads against whatever is behind the widget.</summary>
    public void RefreshTheme()
    {
        if (_cfg.TextTone == TextTone.Light) _darkBackdrop = true;
        else if (_cfg.TextTone == TextTone.Dark) _darkBackdrop = false;
        else if (!_embedded) _darkBackdrop = !Theme.AppsUseLight();
        else
        {
            // Sample the taskbar underneath us; hide for one frame so our own text isn't measured.
            var rect = _host.PlacedScreenRect;
            bool wasVisible = Native.IsWindowVisible(_hwnd);
            if (wasVisible) Native.ShowWindow(_hwnd, Native.SW_HIDE);
            double lum = Theme.SampleLuminance(rect);
            if (wasVisible) Native.ShowWindow(_hwnd, Native.SW_SHOWNA);
            _darkBackdrop = lum <= 0.5;
        }
        var (up, down) = Theme.SchemeColors(_cfg.ColorScheme, _darkBackdrop);
        _fgUp.Color = up;
        _fgDown.Color = down;
        if (!_embedded) Relayout();
    }

    // ---- data ------------------------------------------------------------

    private void OnSample(Snapshot snap)
    {
        _last = snap;
        Render(snap);
        if (_popup is { IsVisible: true }) _popup.Update(snap, _sampler, _traffic);
        if (snap.Time.Second % 5 == 0) UpdateToolTip(snap);
    }

    private void Render(Snapshot snap)
    {
        if (_rxValue == null) return;
        var (rv, ru) = Formatting.Rate(snap.Rx, _cfg.Unit);
        var (tv, tu) = Formatting.Rate(snap.Tx, _cfg.Unit);
        _rxValue!.Text = rv; _rxUnit!.Text = ru;
        _txValue!.Text = tv; _txUnit!.Text = tu;
    }

    private void UpdateToolTip(Snapshot snap)
    {
        var (dRx, dTx) = _traffic.Today();
        _root.ToolTip = $"{snap.SelectionName}\n今日  ↓ {Formatting.Bytes(dRx)}   ↑ {Formatting.Bytes(dTx)}\n本次  ↓ {Formatting.Bytes(snap.SessionRx)}   ↑ {Formatting.Bytes(snap.SessionTx)}";
    }

    private void Tick()
    {
        if (_embedded)
        {
            if (_host.Maintain(_lengthPx)) Relayout();
        }
        else if (_cfg.FloatHideWhenFullscreen)
        {
            bool fs = ForegroundIsFullscreen();
            if (fs != _hiddenForFullscreen)
            {
                _hiddenForFullscreen = fs;
                Native.ShowWindow(_hwnd, fs ? Native.SW_HIDE : Native.SW_SHOWNA);
            }
        }
    }

    private bool ForegroundIsFullscreen()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;
        string cls = Native.GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        Native.GetWindowRect(fg, out var r);
        var mi = Native.MonitorInfoFor(ref r);
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ---- interaction -------------------------------------------------------

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_embedded) return;
        _dragging = true;
        Native.GetCursorPos(out _dragStart);
        Native.GetWindowRect(_hwnd, out _dragRect);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Native.GetCursorPos(out var p);
        int dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 3 && !_dragMoved) return;
        _dragMoved = true;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _dragRect.Left + dx, _dragRect.Top + dy, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
    private bool _dragMoved;

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            if (_dragMoved)
            {
                _dragMoved = false;
                Native.GetWindowRect(_hwnd, out var r);
                _cfg.FloatX = r.Left; _cfg.FloatY = r.Top;
                _cfg.Save();
                return;
            }
        }
        TogglePopup();
    }

    public void OpenPopup()
    {
        if (_popup is { IsVisible: true }) return;
        TogglePopup();
    }

    private void TogglePopup()
    {
        if (_popup is { IsVisible: true }) { _popup.Hide(); return; }
        _popup ??= new DetailPopup(_cfg, _sampler, _traffic, OnSettingsChanged);
        if (_last != null) _popup.Update(_last, _sampler, _traffic);
        Native.GetWindowRect(_hwnd, out var anchor);
        bool openBelow = _embedded ? _host.TaskbarAtStartEdge : anchor.Top < 200;
        _popup.ShowAt(anchor, openBelow, _embedded && !_host.IsHorizontal);
    }

    /// <summary>Called by the popup / menu after they changed the config.</summary>
    private void OnSettingsChanged(string what)
    {
        _cfg.Save();
        switch (what)
        {
            case "adapter":
                _sampler.Selection = _cfg.Adapter;
                break;
            case "interval":
                _sampler.IntervalMs = _cfg.IntervalMs;
                break;
            case "placement":
                ApplyPlacement();
                break;
            case "tone":
                App.Current.ApplyTheme(); // chart + legend colours follow the scheme
                RefreshTheme();
                break;
            default:
                Relayout();
                if (_cfg.Placement == Placement.Floating) PlaceFloating();
                break;
        }
    }

    private void ShowMenu()
    {
        // We never take focus, so the popup wouldn't notice this click; close it explicitly.
        if (_popup is { IsVisible: true }) _popup.Hide();
        Native.GetCursorPos(out var cur);
        FrostedGlass.CaptureAround(cur.X, cur.Y, 800, 300, 900);
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        MenuItem Item(string header, Action? click = null, bool? checked_ = null)
        {
            var mi = new MenuItem { Header = header };
            if (checked_ is bool b) mi.IsChecked = b;
            if (click != null) mi.Click += (_, _) => click();
            return mi;
        }
        MenuItem Choice<T>(string header, T current, T value, Action<T> set, string what)
            => Item(header, () => { set(value); OnSettingsChanged(what); }, Equals(current, value));

        // Adapter
        var adapters = new MenuItem { Header = "网卡" };
        adapters.Items.Add(Choice("自动（物理网卡）", _cfg.Adapter, (Guid?)null, v => _cfg.Adapter = v, "adapter"));
        if (_last != null)
        {
            adapters.Items.Add(new Separator());
            foreach (var i in _last.Interfaces.Where(x => x.IsListable).OrderByDescending(x => x.Up).ThenBy(x => x.Name))
            {
                var mi = Choice(i.Up ? i.Name : i.Name + "（未连接）", _cfg.Adapter, (Guid?)i.Id, v => _cfg.Adapter = v, "adapter");
                mi.ToolTip = i.Description;
                adapters.Items.Add(mi);
            }
        }
        menu.Items.Add(adapters);

        var place = new MenuItem { Header = "显示位置" };
        place.Items.Add(Choice("任务栏 · 靠托盘", _cfg.Placement, Placement.Tray, v => _cfg.Placement = v, "placement"));
        place.Items.Add(Choice("任务栏 · 靠左", _cfg.Placement, Placement.Left, v => _cfg.Placement = v, "placement"));
        place.Items.Add(Choice("悬浮窗（可拖动）", _cfg.Placement, Placement.Floating, v => _cfg.Placement = v, "placement"));
        menu.Items.Add(place);

        var layout = new MenuItem { Header = "布局" };
        layout.Items.Add(Choice("自动", _cfg.Layout, Layout.Auto, v => _cfg.Layout = v, "layout"));
        layout.Items.Add(Choice("两行", _cfg.Layout, Layout.TwoLines, v => _cfg.Layout = v, "layout"));
        layout.Items.Add(Choice("单行", _cfg.Layout, Layout.OneLine, v => _cfg.Layout = v, "layout"));
        layout.Items.Add(new Separator());
        layout.Items.Add(Item("显示箭头", () => { _cfg.ShowArrows = !_cfg.ShowArrows; OnSettingsChanged("layout"); }, _cfg.ShowArrows));
        menu.Items.Add(layout);

        var unit = new MenuItem { Header = "单位" };
        unit.Items.Add(Choice("字节  12.3 KB/s", _cfg.Unit, UnitStyle.Bytes, v => _cfg.Unit = v, "unit"));
        unit.Items.Add(Choice("紧凑  12.3K", _cfg.Unit, UnitStyle.Compact, v => _cfg.Unit = v, "unit"));
        unit.Items.Add(Choice("比特  98.4 Mb/s", _cfg.Unit, UnitStyle.Bits, v => _cfg.Unit = v, "unit"));
        menu.Items.Add(unit);

        var font = new MenuItem { Header = "字体与字号" };
        font.Items.Add(Choice("自动", _cfg.FontSize, 0.0, v => _cfg.FontSize = v, "font"));
        foreach (var fs in new[] { 9.0, 10, 11, 12, 13, 14 })
            font.Items.Add(Choice(fs.ToString(CultureInfo.InvariantCulture), _cfg.FontSize, fs, v => _cfg.FontSize = v, "font"));
        font.Items.Add(new Separator());
        font.Items.Add(Item("字体…", OpenFontDialog));
        menu.Items.Add(font);

        var tone = new MenuItem { Header = "文字颜色" };
        bool plain = string.IsNullOrEmpty(_cfg.ColorScheme);
        MenuItem Tone(string header, TextTone t) => Item(header,
            () => { _cfg.TextTone = t; _cfg.ColorScheme = ""; OnSettingsChanged("tone"); }, plain && _cfg.TextTone == t);
        tone.Items.Add(Tone("自动（按任务栏明暗）", TextTone.Auto));
        tone.Items.Add(Tone("浅色文字", TextTone.Light));
        tone.Items.Add(Tone("深色文字", TextTone.Dark));
        tone.Items.Add(new Separator());
        foreach (var s in Theme.Schemes)
        {
            if (s.UpOnDark == s.DownOnDark && tone.Items[^1] is not Separator && Theme.Schemes.First(x => x.UpOnDark == x.DownOnDark) == s)
                tone.Items.Add(new Separator()); // two-colour schemes above, single-colour below
            var (up, down) = Theme.SchemeColors(s.Key, _darkBackdrop); // dots: first = upload, second = download
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(Swatch(up));
            header.Children.Add(Swatch(down));
            header.Children.Add(new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            var key = s.Key;
            tone.Items.Add(Item("", () => { _cfg.ColorScheme = key; OnSettingsChanged("tone"); }, _cfg.ColorScheme == key));
            ((MenuItem)tone.Items[^1]).Header = header;
        }
        menu.Items.Add(tone);

        var interval = new MenuItem { Header = "刷新间隔" };
        foreach (var (label, ms) in new[] { ("0.5 秒", 500), ("1 秒", 1000), ("2 秒", 2000), ("3 秒", 3000) })
            interval.Items.Add(Choice(label, _cfg.IntervalMs, ms, v => _cfg.IntervalMs = v, "interval"));
        menu.Items.Add(interval);

        menu.Items.Add(new Separator());
        bool auto = AppConfig.AutostartEnabled;
        menu.Items.Add(Item("开机自启", () => AppConfig.AutostartEnabled = !auto, auto));
        menu.Items.Add(Item("打开配置文件夹", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppConfig.Dir) { UseShellExecute = true })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出 FlowBar", () => Application.Current.Shutdown()));

        menu.IsOpen = true;
    }

    private FontDialog? _fontDialog;

    public void OpenFontDialog()
    {
        if (_fontDialog is { IsVisible: true }) { _fontDialog.Activate(); return; }
        if (_popup is { IsVisible: true }) _popup.Hide();
        _fontDialog = new FontDialog(_cfg, () => OnSettingsChanged("font"));
        _fontDialog.Closed += (_, _) => _fontDialog = null;
        Native.GetWindowRect(_hwnd, out var anchor);
        bool openBelow = _embedded ? _host.TaskbarAtStartEdge : anchor.Top < 200;
        _fontDialog.ShowAt(anchor, openBelow);
    }

    private static Border Swatch(Color c) => new()
    {
        Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 3, 0),
        Background = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center,
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1),
    };
}
