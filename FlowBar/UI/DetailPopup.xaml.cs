using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    public DetailPopup(AppConfig cfg, NetSampler sampler, TrafficLog traffic, Action<string> changed)
    {
        _cfg = cfg;
        _changed = changed;
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        QuitButton.Click += (_, _) => Application.Current.Shutdown();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    /// <summary>Positions the card next to <paramref name="anchor"/> (screen px) and shows it.</summary>
    public void ShowAt(RECT anchor, bool openBelow, bool verticalBar)
    {
        // No DWM corner/border attributes here: on a layered window Windows 11 would paint its own
        // rounded frame around the whole window rect, i.e. a second outline outside the card.
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();

        // A finished fade-in animation still holds Opacity at 1; release it before hiding again.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();

        double s = Native.DpiOf(_hwnd) / 96.0;
        int w = (int)Math.Round(ActualWidth * s), h = (int)Math.Round(ActualHeight * s);
        int shadow = (int)Math.Round(16 * s); // Card.Margin

        var mi = Native.MonitorInfoFor(ref anchor);
        int x, y;
        if (verticalBar)
        {
            // taskbar on the left/right edge: open beside it, vertically centred on the widget
            bool right = anchor.Left - mi.rcMonitor.Left < mi.rcMonitor.Right - anchor.Right;
            x = right ? anchor.Right - shadow + 2 : anchor.Left - w + shadow - 2;
            y = (anchor.Top + anchor.Bottom) / 2 - h / 2;
        }
        else
        {
            x = (anchor.Left + anchor.Right) / 2 - w / 2;
            y = openBelow ? anchor.Bottom - shadow + 2 : anchor.Top - h + shadow - 2;
        }
        x = Math.Clamp(x, mi.rcWork.Left - shadow + 4, Math.Max(mi.rcWork.Left, mi.rcWork.Right - w + shadow - 4));
        y = Math.Clamp(y, mi.rcWork.Top - shadow + 4, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - h + shadow - 4));

        // Snapshot what will be behind the card (we're fully transparent right now), then place it.
        FrostedGlass.Capture(new RECT { Left = x + shadow - 4, Top = y + shadow - 4, Right = x + w - shadow + 4, Bottom = y + h - shadow + 4 });
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
        FrostedGlass.Refresh(Glass);
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
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

        double max = 1024; // never flatter than 1 KB/s full scale
        for (int i = from; i < n; i++) max = Math.Max(max, Math.Max(_rx[i], _tx[i]));
        const double top = 18; // headroom so the labels along the top never sit on a spike

        var rx = new List<Point>(count);
        var tx = new List<Point>(count);
        double step = w / (window - 1);
        double x0 = w - (count - 1) * step;
        for (int i = 0; i < count; i++)
        {
            double x = x0 + i * step;
            rx.Add(new Point(x, h - 2 - (h - 2 - top) * _rx[from + i] / max));
            tx.Add(new Point(x, h - 2 - (h - 2 - top) * _tx[from + i] / max));
        }
        RxLine.Data = Smooth(rx, h, closed: false);
        RxFill.Data = Smooth(rx, h, closed: true);
        TxLine.Data = Smooth(tx, h, closed: false);
        TxFill.Data = Smooth(tx, h, closed: true);

        // Download: blue → cyan. Upload: pink → violet. Both glow slightly and fade into the baseline.
        bool light = Theme.AppsUseLight();
        Color rxA = Theme.Hex(light ? "#2563EB" : "#60A5FA"), rxB = Theme.Hex(light ? "#06B6D4" : "#22D3EE");
        Color txA = Theme.Hex(light ? "#DB2777" : "#F472B6"), txB = Theme.Hex(light ? "#8B5CF6" : "#A78BFA");
        Style(RxLine, RxFill, RxDot, rxA, rxB, rx, light);
        Style(TxLine, TxFill, TxDot, txA, txB, tx, light);
        var (mv, mu) = Formatting.Rate(max, _cfg.Unit);
        ChartMax.Text = $"峰值 {mv} {mu}";
        int span = Math.Max(1, (int)Math.Round(count * sampler.IntervalMs / 1000.0));
        ChartSpan.Text = span >= 60 ? $"最近 {span / 60} 分钟" : $"最近 {span} 秒";
    }

    /// <summary>Catmull-Rom spline through the samples, optionally closed down to the baseline.</summary>
    private static Geometry Smooth(List<Point> pts, double baseline, bool closed)
    {
        if (pts.Count < 2) return Geometry.Empty;
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = closed };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Point p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Count - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        if (closed)
        {
            fig.Segments.Add(new LineSegment(new Point(pts[^1].X, baseline), false));
            fig.Segments.Add(new LineSegment(new Point(pts[0].X, baseline), false));
            fig.IsClosed = true;
        }
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    private static void Style(System.Windows.Shapes.Path line, System.Windows.Shapes.Path fill, System.Windows.Shapes.Ellipse dot,
        Color a, Color b, List<Point> pts, bool light)
    {
        var stroke = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 0));
        stroke.Freeze();
        line.Stroke = stroke;
        line.Effect = new DropShadowEffect { Color = b, BlurRadius = 10, ShadowDepth = 0, Opacity = light ? 0.35 : 0.6 };

        var area = new LinearGradientBrush(
            Color.FromArgb(light ? (byte)0x55 : (byte)0x66, b.R, b.G, b.B), Color.FromArgb(0x00, b.R, b.G, b.B),
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
