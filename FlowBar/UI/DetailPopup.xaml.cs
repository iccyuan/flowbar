using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlowBar.Core;

namespace FlowBar.UI;

/// <summary>Click-to-open card with the live chart, totals and the adapter chooser.</summary>
public partial class DetailPopup : Window
{
    private readonly AppConfig _cfg;
    private readonly Action<string> _changed;
    private readonly double[] _rx = new double[NetSampler.HistoryLength];
    private readonly double[] _tx = new double[NetSampler.HistoryLength];
    private IntPtr _hwnd;
    private string _adapterKey = "";

    /// <summary>Set by the owner: opens the per-program window.</summary>
    public Action? OpenApps { get; set; }

    public DetailPopup(AppConfig cfg, NetSampler sampler, TrafficLog traffic, Action<string> changed)
    {
        _cfg = cfg;
        _changed = changed;
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        QuitButton.Click += (_, _) => Application.Current.Shutdown();
        AppsButton.Click += (_, _) => { Hide(); OpenApps?.Invoke(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    private RECT _anchor;
    private bool _openBelow, _vertical, _placed;

    /// <summary>Positions the card next to <paramref name="anchor"/> (screen px) and shows it.</summary>
    public void ShowAt(RECT anchor, bool openBelow, bool verticalBar)
    {
        // No DWM corner/border attributes here: on a layered window Windows 11 would paint its own
        // rounded frame around the whole window rect, i.e. a second outline outside the card.
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            SizeChanged += (_, _) => Reposition();
        }
        _anchor = anchor; _openBelow = openBelow; _vertical = verticalBar;

        // A finished fade-in animation still holds Opacity at 1; release it before hiding again.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        _placed = false;
        Show();
        UpdateLayout();

        var (x, y, w, h, shadow) = Layout();
        // Snapshot generously around the card: it grows when adapter rows arrive, and the tooltip may
        // capture its own small region later; the element keeps whichever snapshot still covers it.
        var mi = Native.MonitorInfoFor(ref _anchor);
        FrostedGlass.Capture(new RECT
        {
            Left = Math.Max(mi.rcMonitor.Left, x + shadow - 200),
            Top = Math.Max(mi.rcMonitor.Top, y + shadow - 400),
            Right = Math.Min(mi.rcMonitor.Right, x + w - shadow + 200),
            Bottom = Math.Min(mi.rcMonitor.Bottom, y + h - shadow + 400),
        });
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
        _placed = true;
        FrostedGlass.Refresh(Glass);
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>Window rect (screen px) for the current content size, hugging the anchor and the work area.</summary>
    private (int x, int y, int w, int h, int shadow) Layout()
    {
        double s = Native.DpiOf(_hwnd) / 96.0;
        int w = (int)Math.Round(ActualWidth * s), h = (int)Math.Round(ActualHeight * s);
        int shadow = (int)Math.Round(16 * s); // outer Grid.Margin
        var mi = Native.MonitorInfoFor(ref _anchor);
        int x, y;
        if (_vertical)
        {
            bool right = _anchor.Left - mi.rcMonitor.Left < mi.rcMonitor.Right - _anchor.Right;
            x = right ? _anchor.Right - shadow + 2 : _anchor.Left - w + shadow - 2;
            y = (_anchor.Top + _anchor.Bottom) / 2 - h / 2;
        }
        else
        {
            x = (_anchor.Left + _anchor.Right) / 2 - w / 2;
            y = _openBelow ? _anchor.Bottom - shadow + 2 : _anchor.Top - h + shadow - 2;
        }
        x = Math.Clamp(x, mi.rcWork.Left - shadow + 4, Math.Max(mi.rcWork.Left, mi.rcWork.Right - w + shadow - 4));
        y = Math.Clamp(y, mi.rcWork.Top - shadow + 4, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - h + shadow - 4));
        return (x, y, w, h, shadow);
    }

    /// <summary>Content grew or shrank after showing: keep the card anchored and re-slice the glass.</summary>
    private void Reposition()
    {
        if (!_placed || !IsVisible) return;
        var (x, y, w, h, _) = Layout();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        FrostedGlass.Refresh(Glass);
    }

    public void Update(Snapshot snap, NetSampler sampler, TrafficLog traffic)
    {
        AdapterText.Text = snap.SelectionName;
        var (rv, ru) = Formatting.Rate(snap.Rx, _cfg.Unit);
        var (tv, tu) = Formatting.Rate(snap.Tx, _cfg.Unit);
        RxBig.Text = rv; RxUnit.Text = ru;
        TxBig.Text = tv; TxUnit.Text = tu;

        var (dRx, dTx) = traffic.Today();
        var (mRx, mTx) = traffic.Month();
        TodayRx.Text = Formatting.Bytes(dRx); TodayTx.Text = Formatting.Bytes(dTx);
        MonthRx.Text = Formatting.Bytes(mRx); MonthTx.Text = Formatting.Bytes(mTx);
        SessRx.Text = Formatting.Bytes(snap.SessionRx); SessTx.Text = Formatting.Bytes(snap.SessionTx);

        DrawChart(sampler);
        RebuildAdapters(snap);
    }

    private void DrawChart(NetSampler sampler)
    {
        int n = sampler.CopyHistory(_rx, _tx);
        const int window = 120;
        int from = Math.Max(0, n - window);
        int count = n - from;
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        if (w <= 0 || h <= 0) { w = 270; h = 76; }
        EnsureGrid(w, h);

        // Smooth over the past few samples only, so drawn history never changes as new samples arrive.
        var srx = Causal(_rx, from, count);
        var stx = Causal(_tx, from, count);
        double peak = 0, max = 1024; // never flatter than 1 KB/s full scale
        for (int i = from; i < n; i++) peak = Math.Max(peak, Math.Max(_rx[i], _tx[i]));
        for (int i = 0; i < count; i++) max = Math.Max(max, Math.Max(srx[i], stx[i]));
        const double top = 18; // headroom so the labels along the top never sit on a spike

        double step = w / (window - 1);
        double x0 = w - (count - 1) * step;
        var rx = Points(srx, x0, step, h, top, max);
        var tx = Points(stx, x0, step, h, top, max);
        RxLine.Data = Curve(rx, h, closed: false);
        RxFill.Data = Curve(rx, h, closed: true);
        TxLine.Data = Curve(tx, h, closed: false);
        TxFill.Data = Curve(tx, h, closed: true);

        // Colours follow the taskbar text scheme (see Theme.ChartColors); thin lines over a fade to the baseline.
        bool light = Theme.AppsUseLight();
        Color rxA = ((SolidColorBrush)FindResource("ChartRx")).Color, rxB = ((SolidColorBrush)FindResource("ChartRxEnd")).Color;
        Color txA = ((SolidColorBrush)FindResource("ChartTx")).Color, txB = ((SolidColorBrush)FindResource("ChartTxEnd")).Color;
        Style(RxLine, RxFill, RxDot, rxA, rxB, rx, light);
        Style(TxLine, TxFill, TxDot, txA, txB, tx, light);
        var (mv, mu) = Formatting.Rate(Math.Max(peak, 1024), _cfg.Unit);
        ChartMax.Text = $"峰值 {mv} {mu}";
        int span = Math.Max(1, (int)Math.Round(count * sampler.IntervalMs / 1000.0));
        ChartSpan.Text = span >= 60 ? $"最近 {span / 60} 分钟" : $"最近 {span} 秒";
    }

    /// <summary>Samples that feed each smoothed point: itself and the ones just before it.</summary>
    private const int SmoothSamples = 5;

    /// <summary>
    /// Triangular-weighted average of each sample and the <see cref="SmoothSamples"/> - 1 before it (newest heaviest).
    /// Unlike a centred filter it never looks ahead, so a point is final the moment it is drawn.
    /// </summary>
    private static double[] Causal(double[] v, int from, int count)
    {
        var o = new double[count];
        for (int i = 0; i < count; i++)
        {
            double sum = 0, weight = 0;
            for (int j = 0; j < SmoothSamples && from + i - j >= 0; j++)
            {
                double k = SmoothSamples - j;
                sum += v[from + i - j] * k; weight += k;
            }
            o[i] = sum / weight;
        }
        return o;
    }

    private static List<Point> Points(double[] v, double x0, double step, double h, double top, double max)
    {
        var pts = new List<Point>(v.Length);
        for (int i = 0; i < v.Length; i++) pts.Add(new Point(x0 + i * step, h - 2 - (h - 2 - top) * v[i] / max));
        return pts;
    }

    /// <summary>Smooth curve through the samples, optionally closed down to the baseline.</summary>
    private static Geometry Curve(List<Point> pts, double baseline, bool closed)
    {
        if (pts.Count < 2) return Geometry.Empty;
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = closed };
        Monotone(fig, pts);
        if (closed)
        {
            fig.Segments.Add(new LineSegment(new Point(pts[^1].X, baseline), false));
            fig.Segments.Add(new LineSegment(new Point(pts[0].X, baseline), false));
            fig.IsClosed = true;
        }
        return Freeze(fig);
    }

    private static Geometry Freeze(PathFigure fig)
    {
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    /// <summary>
    /// Appends a monotone cubic (PCHIP) curve through <paramref name="pts"/>: smooth like a spline but it never
    /// overshoots between samples, so the curve never dips below the baseline next to a spike.
    /// </summary>
    private static void Monotone(PathFigure fig, List<Point> pts)
    {
        int n = pts.Count;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (pts[i + 1].Y - pts[i].Y) / (pts[i + 1].X - pts[i].X);
        var m = new double[n];
        m[0] = d[0]; m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            if (d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
            double h0 = pts[i].X - pts[i - 1].X, h1 = pts[i + 1].X - pts[i].X;
            m[i] = 3 * (h0 + h1) / ((2 * h1 + h0) / d[i - 1] + (h1 + 2 * h0) / d[i]);
        }
        for (int i = 0; i < n - 1; i++)
        {
            double dx = (pts[i + 1].X - pts[i].X) / 3;
            var c1 = new Point(pts[i].X + dx, pts[i].Y + m[i] * dx);
            var c2 = new Point(pts[i + 1].X - dx, pts[i + 1].Y - m[i + 1] * dx);
            fig.Segments.Add(new BezierSegment(c1, c2, pts[i + 1], true));
        }
    }

    private static void Style(System.Windows.Shapes.Path line, System.Windows.Shapes.Path fill, System.Windows.Shapes.Ellipse dot,
        Color a, Color b, List<Point> pts, bool light)
    {
        var stroke = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 0));
        stroke.Freeze();
        line.Stroke = stroke;

        var area = new LinearGradientBrush(
            Color.FromArgb(light ? (byte)0x38 : (byte)0x48, b.R, b.G, b.B), Color.FromArgb(0x00, b.R, b.G, b.B),
            new Point(0, 0), new Point(0, 1));
        area.Freeze();
        fill.Fill = area;

        if (pts.Count == 0) { dot.Visibility = Visibility.Collapsed; return; }
        var last = pts[^1];
        dot.Visibility = Visibility.Visible;
        dot.Fill = new SolidColorBrush(b);
        dot.Stroke = new SolidColorBrush(light ? Colors.White : Theme.Hex("#1F1F1F"));
        Canvas.SetLeft(dot, last.X - dot.Width / 2);
        Canvas.SetTop(dot, last.Y - dot.Height / 2);
    }

    private double _gridW, _gridH;

    /// <summary>Faint dotted guide lines at 25/50/75% plus a solid baseline; rebuilt only on resize.</summary>
    private void EnsureGrid(double w, double h)
    {
        if (Math.Abs(w - _gridW) < 0.5 && Math.Abs(h - _gridH) < 0.5) return;
        _gridW = w; _gridH = h;
        GridLines.Children.Clear();
        var brush = (Brush)FindResource("MenuBorder");
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h - 2 - (h - 2 - 18) * i / 4) + 0.5;
            GridLines.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }, Opacity = 0.8,
            });
        }
        GridLines.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w, Y1 = h - 1.5, Y2 = h - 1.5, Stroke = brush, StrokeThickness = 1,
        });
    }

    private void RebuildAdapters(Snapshot snap)
    {
        var list = snap.Interfaces.Where(i => i.IsListable).OrderByDescending(i => i.Up).ThenBy(i => i.Name).ToList();
        string key = string.Join("|", list.Select(i => i.Id + (i.Up ? "1" : "0"))) + "#" + _cfg.Adapter;
        if (key != _adapterKey)
        {
            _adapterKey = key;
            AdapterList.Children.Clear();
            AdapterList.Children.Add(MakeRow(null, "自动（物理网卡）", _cfg.Adapter == null));
            foreach (var i in list)
                AdapterList.Children.Add(MakeRow(i.Id, i.Up ? i.Name : i.Name + "（未连接）", _cfg.Adapter == i.Id, i.Description));
        }
        // refresh the little per-adapter rate labels
        foreach (RadioButton rb in AdapterList.Children)
        {
            if (rb.Tag is Guid g && rb.Content is Grid grid && grid.Children[1] is TextBlock rate)
            {
                var i = list.FirstOrDefault(x => x.Id == g);
                if (i == null) continue;
                var (rv, ru) = Formatting.Rate(i.Rx, _cfg.Unit);
                var (tv, tu) = Formatting.Rate(i.Tx, _cfg.Unit);
                rate.Text = i.Up ? $"↓ {rv} {ru}   ↑ {tv} {tu}" : "";
            }
        }
    }

    private RadioButton MakeRow(Guid? id, string name, bool checked_, string? tooltip = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameTb = new TextBlock { Text = name, Style = (Style)FindResource("Body"), TextTrimming = TextTrimming.CharacterEllipsis };
        var rateTb = new TextBlock { Style = (Style)FindResource("Subtle"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rateTb, 1);
        grid.Children.Add(nameTb);
        grid.Children.Add(rateTb);
        var rb = new RadioButton
        {
            Content = grid,
            GroupName = "adapter",
            IsChecked = checked_,
            Tag = id ?? (object)"auto",
            Style = (Style)FindResource("AdapterRadio"),
            ToolTip = tooltip,
        };
        rb.Checked += (_, _) =>
        {
            if (_cfg.Adapter == id) return;
            _cfg.Adapter = id;
            _changed("adapter");
        };
        return rb;
    }
}
