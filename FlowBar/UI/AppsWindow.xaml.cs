using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FlowBar.Core;

namespace FlowBar.UI;

/// <summary>Per-program traffic and the connection table, refreshed once a second while visible.</summary>
public partial class AppsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly AppTraffic _traffic;
    private readonly ObservableCollection<AppItem> _apps = new();
    private readonly ObservableCollection<ConnItem> _conns = new();
    private readonly Dictionary<string, AppItem> _appItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, AppItem> _pidItems = new();
    private readonly Dictionary<ConnId, ConnItem> _connItems = new();
    private readonly Dictionary<string, ConnItem> _connGroups = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Programs whose per-process / per-connection rows are shown, per page.</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _connExpanded = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hwnd;
    private string _filter = "";

    private readonly record struct ConnId(int Pid, bool Tcp, bool V6, Endpoint Local, Endpoint Remote);

    private const double BarMax = 84; // px, the faint bar behind the busiest rate

    // Column sorting: header text → key; a click picks the column, a second click flips the direction.
    private static readonly Dictionary<string, (string key, bool numeric)> SortKeys = new()
    {
        ["程序"] = ("name", false), ["下载"] = ("rx", true), ["上传"] = ("tx", true), ["今日下载"] = ("todayRx", true),
        ["今日上传"] = ("todayTx", true), ["连接"] = ("conns", true), ["协议"] = ("proto", false),
        ["本地地址"] = ("local", false), ["远程地址"] = ("remote", false), ["状态"] = ("state", false),
    };
    private readonly Dictionary<GridViewColumn, string> _headerText = new();
    private (string key, bool desc)? _appSort, _connSort;

    public AppsWindow(AppConfig cfg, AppTraffic traffic)
    {
        _cfg = cfg;
        _traffic = traffic;
        InitializeComponent();
        AppList.ItemsSource = _apps;
        ConnList.ItemsSource = _conns;

        CloseButton.Click += (_, _) => Close();
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        ElevateButton.Click += (_, _) => App.Current.RestartElevated();
        TabApps.Checked += (_, _) => SwitchTab();
        TabConns.Checked += (_, _) => SwitchTab();
        TabStats.Checked += (_, _) => SwitchTab();
        RangeDay.Checked += (_, _) => { _statsDays = 1; _rankKey = ""; Refresh(); };
        RangeWeek.Checked += (_, _) => { _statsDays = 7; _rankKey = ""; Refresh(); };
        RangeMonth.Checked += (_, _) => { _statsDays = 30; _rankKey = ""; Refresh(); };
        DayChart.SizeChanged += (_, _) => { _dayKey = ""; DrawDays(); };
        ActiveOnly.Checked += (_, _) => Refresh();
        ActiveOnly.Unchecked += (_, _) => Refresh();
        FilterBox.TextChanged += (_, _) => { _filter = FilterBox.Text.Trim(); _rankKey = ""; Refresh(); };
        KeyDown += OnKeyDown;
        AppList.SizeChanged += (_, _) => FitColumns();
        ConnList.SizeChanged += (_, _) => FitColumns();
        foreach (var col in ((GridView)AppList.View).Columns.Concat(((GridView)ConnList.View).Columns))
            _headerText[col] = (string)col.Header;
        AppList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) => OnHeaderClick(e, apps: true)));
        ConnList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) => OnHeaderClick(e, apps: false)));
        AppList.PreviewMouseLeftButtonDown += (_, e) => OnListClick(AppList, e, _expanded, RefreshApps);
        ConnList.PreviewMouseLeftButtonDown += (_, e) => OnListClick(ConnList, e, _connExpanded, RefreshConns);
        AppList.KeyDown += (_, e) => OnListKey(AppList, e, _expanded, RefreshApps);
        ConnList.KeyDown += (_, e) => OnListKey(ConnList, e, _connExpanded, RefreshConns);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            ApplyBackdrop();
        };
        IsVisibleChanged += (_, _) =>
        {
            _traffic.WantConnections = IsVisible;
            if (IsVisible) Refresh();
        };
        _traffic.Updated += Refresh;
        Closed += (_, _) =>
        {
            _traffic.Updated -= Refresh;
            _traffic.WantConnections = false;
        };

        if (!_traffic.RatesAvailable)
        {
            Hint.Visibility = Visibility.Visible;
            HintText.Text = AppTraffic.IsElevated
                ? $"无法统计各程序的网速：{_traffic.Unavailable}。连接列表不受影响。"
                : "按程序统计网速需要管理员权限（Windows 只向管理员开放内核网络事件）。连接列表不受影响。";
            ElevateButton.Visibility = AppTraffic.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        }
        SwitchTab();
    }

    /// <summary>
    /// Mica behind the whole window (Windows 11 22H2+). The frame is extended over the client area so
    /// DWM paints the backdrop wherever WPF leaves pixels transparent; older builds keep the flat colour.
    /// </summary>
    private void ApplyBackdrop()
    {
        int dark = Theme.AppsUseLight() ? 0 : 1;
        Native.DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        int backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
        if (Native.DwmSetWindowAttribute(_hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref backdrop, sizeof(int)) != 0) return;
        if (HwndSource.FromHwnd(_hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        Background = Brushes.Transparent;
    }

    public void ShowConnections() => TabConns.IsChecked = true;
    /// <summary>Debug hook (--apps=expand): keep every multi-process program expanded.</summary>
    public void ExpandAll() { _expandAll = true; Refresh(); }
    private bool _expandAll;
    public void ShowStats(int days = 0)
    {
        if (days == 7) RangeWeek.IsChecked = true; else if (days == 30) RangeMonth.IsChecked = true;
        TabStats.IsChecked = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (FilterBox.Text.Length > 0) FilterBox.Text = "";
            else Close();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { FilterBox.Focus(); FilterBox.SelectAll(); }
    }

    // ---- expand / collapse ---------------------------------------------------

    private static void OnListClick(ListView list, MouseButtonEventArgs e, HashSet<string> expanded, Action refresh)
    {
        if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListViewItem { DataContext: RowItem it })
            Toggle(it, expanded, refresh);
    }

    private static void OnListKey(ListView list, KeyEventArgs e, HashSet<string> expanded, Action refresh)
    {
        if (list.SelectedItem is not RowItem it) return;
        if (e.Key == Key.Right && it.IsGroup && !it.Expanded || e.Key == Key.Left && it.IsGroup && it.Expanded
            || (e.Key == Key.Space || e.Key == Key.Enter) && it.IsGroup)
        {
            Toggle(it, expanded, refresh);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && it.IsChild)
        {
            // jump back to the parent row
            var parent = list.Items.OfType<RowItem>().FirstOrDefault(a => a.IsGroup && a.Name.Equals(it.Parent, StringComparison.OrdinalIgnoreCase));
            if (parent != null) { list.SelectedItem = parent; list.ScrollIntoView(parent); }
            e.Handled = true;
        }
    }

    private static void Toggle(RowItem it, HashSet<string> expanded, Action refresh)
    {
        if (!it.IsGroup) return;
        if (!expanded.Remove(it.Name)) expanded.Add(it.Name);
        refresh();
    }

    // ---- sorting -----------------------------------------------------------

    private void OnHeaderClick(RoutedEventArgs e, bool apps)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col } || !_headerText.TryGetValue(col, out var text)) return;
        if (!SortKeys.TryGetValue(text, out var k)) return;
        ref var sort = ref (apps ? ref _appSort : ref _connSort);
        // Numbers start descending (biggest first), text ascending; a repeat click flips.
        sort = sort is { } cur && cur.key == k.key ? (k.key, !cur.desc) : (k.key, k.numeric);
        var view = (GridView)(apps ? AppList : ConnList).View;
        foreach (var c in view.Columns)
        {
            string t = _headerText[c];
            c.Header = SortKeys.TryGetValue(t, out var ck) && ck.key == sort.Value.key ? t + (sort.Value.desc ? " ▾" : " ▴") : t;
        }
        Refresh();
    }

    private static int Dir(bool desc, int c) => desc ? -c : c;
    private static int Str(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    private bool Matches(string s) => _filter.Length == 0 || s.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private void SwitchTab()
    {
        bool apps = TabApps.IsChecked == true, conns = TabConns.IsChecked == true, stats = TabStats.IsChecked == true;
        AppCard.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        ConnCard.Visibility = conns ? Visibility.Visible : Visibility.Collapsed;
        StatsView.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        ActiveOnly.Visibility = conns ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void FitColumns()
    {
        double w = AppList.ActualWidth;
        if (w > 0) NameCol.Width = Math.Max(160, w - (104 * 4 + 64) - 26);
        double cw = ConnList.ActualWidth;
        if (cw > 0) ProcCol.Width = Math.Max(120, cw - (60 + 156 + 168 + 112 + 88 * 2) - 26);
    }

    private string Rate(double v)
    {
        if (v < 1) return "";
        var (val, unit) = Formatting.Rate(v, _cfg.Unit);
        return val + " " + unit;
    }

    private string RateOrZero(double v)
    {
        var (val, unit) = Formatting.Rate(v, _cfg.Unit);
        return val + " " + unit;
    }

    private static string Total(ulong v) => v == 0 ? "" : Formatting.Bytes(v);

    private void Refresh()
    {
        if (!IsVisible) return;
        if (TabApps.IsChecked == true) RefreshApps();
        else if (TabConns.IsChecked == true) RefreshConns();
        else RefreshStats();
    }

    // ---- statistics ----------------------------------------------------------

    private int _statsDays = 1;
    private string? _statsFocus;
    private string _rankKey = "", _dayKey = "";
    private List<(DateTime day, ulong rx, ulong tx)> _perDay = new();
    private readonly Dictionary<string, RankRow> _rankRows = new(StringComparer.OrdinalIgnoreCase);

    private sealed class RankRow
    {
        public Border Root = null!;
        public ColumnDefinition Rx = null!, Tx = null!, Rest = null!;
        public TextBlock RxText = null!, TxText = null!;
    }

    private void RefreshStats()
    {
        var snap = _traffic.Log.Snapshot();
        var today = DateTime.Today;
        var perName = new Dictionary<string, (ulong rx, ulong tx)>(StringComparer.OrdinalIgnoreCase);
        var perDay = new List<(DateTime day, ulong rx, ulong tx)>(_statsDays);
        for (int i = _statsDays - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            ulong drx = 0, dtx = 0;
            if (snap.TryGetValue(d.ToString("yyyy-MM-dd"), out var m))
            {
                foreach (var (n, v) in m)
                {
                    if (!Matches(n)) continue;
                    perName.TryGetValue(n, out var t);
                    perName[n] = (t.rx + v.rx, t.tx + v.tx);
                    if (_statsFocus == null || n.Equals(_statsFocus, StringComparison.OrdinalIgnoreCase)) { drx += v.rx; dtx += v.tx; }
                }
            }
            perDay.Add((d, drx, dtx));
        }
        if (_statsFocus != null && !perName.ContainsKey(_statsFocus)) _statsFocus = null;

        ulong totRx = 0, totTx = 0;
        foreach (var v in perName.Values) { totRx += v.rx; totTx += v.tx; }
        StatRx.Text = Formatting.Bytes(totRx);
        StatTx.Text = Formatting.Bytes(totTx);
        StatApps.Text = perName.Count.ToString();
        bool empty = perName.Count == 0;
        StatsEmptyCard.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RankSection.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        StatsFocusText.Text = _statsFocus == null ? "" : $"只看 {_statsFocus}  ·  再点一次取消";

        var ranked = perName.OrderByDescending(kv => kv.Value.rx + kv.Value.tx).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        ulong max = ranked.Count > 0 ? Math.Max(1, ranked[0].Value.rx + ranked[0].Value.tx) : 1;
        string key = string.Join("|", ranked.Select(kv => kv.Key)) + "#" + _statsFocus;
        if (key != _rankKey)
        {
            // Order or selection changed: rebuild. Otherwise the rows are updated in place so a
            // hovered row stays hovered while the numbers tick.
            _rankKey = key;
            RankList.Children.Clear();
            _rankRows.Clear();
            foreach (var (name, _) in ranked) RankList.Children.Add(MakeRankRow(name).Root);
        }
        foreach (var (name, t) in ranked)
        {
            var row = _rankRows[name];
            row.Rx.Width = new GridLength(t.rx, GridUnitType.Star);
            row.Tx.Width = new GridLength(t.tx, GridUnitType.Star);
            row.Rx.MinWidth = t.rx > 0 ? 6 : 0; // a dot still shows there was something
            row.Tx.MinWidth = t.tx > 0 ? 6 : 0;
            row.Rest.Width = new GridLength(Math.Max(0, (double)max - t.rx - t.tx), GridUnitType.Star);
            row.RxText.Text = Formatting.Bytes(t.rx);
            row.TxText.Text = Formatting.Bytes(t.tx);
        }

        DaySection.Visibility = _statsDays > 1 && !empty ? Visibility.Visible : Visibility.Collapsed;
        DayTitle.Text = _statsFocus == null ? "每日流量" : $"每日流量 · {_statsFocus}";
        _perDay = perDay;
        DrawDays();
        Summary.Text = (_statsDays == 1 ? "今日按程序累计" : $"最近 {_statsDays} 天按程序累计") + (_filter.Length > 0 ? $"   ·   筛选 “{_filter}”" : "");
    }

    private RankRow MakeRankRow(string name)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

        var icon = new Image { Source = _traffic.IconFor(name), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        var label = new TextBlock { Text = name, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        // A thin track the full width of the column; download fills from the left, upload continues
        // after it. Both ends are rounded and the upload piece overlaps the download end by its radius
        // so the seam is invisible and a lone download still ends in a round cap.
        var row = new RankRow { Rx = new ColumnDefinition(), Tx = new ColumnDefinition(), Rest = new ColumnDefinition() };
        var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        bar.ColumnDefinitions.Add(row.Rx);
        bar.ColumnDefinitions.Add(row.Tx);
        bar.ColumnDefinitions.Add(row.Rest);
        var track = new Border { Background = (Brush)FindResource("SegmentBg"), CornerRadius = new CornerRadius(3) };
        Grid.SetColumnSpan(track, 3);
        var rxBar = new Border { Background = Gradient("ChartRx", "ChartRxEnd"), CornerRadius = new CornerRadius(3) };
        var txBar = new Border { Background = Gradient("ChartTx", "ChartTxEnd"), CornerRadius = new CornerRadius(3), Margin = new Thickness(-3, 0, 0, 0) };
        Grid.SetColumn(txBar, 1);
        bar.Children.Add(track);
        bar.Children.Add(rxBar);
        bar.Children.Add(txBar);
        Grid.SetColumn(bar, 2);
        grid.Children.Add(bar);

        row.RxText = new TextBlock { Style = (Style)FindResource("Num"), Foreground = (Brush)FindResource("ChartRx"), VerticalAlignment = VerticalAlignment.Center };
        row.TxText = new TextBlock { Style = (Style)FindResource("Num"), Foreground = (Brush)FindResource("ChartTx"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(row.RxText, 3);
        Grid.SetColumn(row.TxText, 4);
        grid.Children.Add(row.RxText);
        grid.Children.Add(row.TxText);

        bool focused = name.Equals(_statsFocus, StringComparison.OrdinalIgnoreCase);
        var root = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(-8, 0, -8, 0),
            Background = focused ? (Brush)FindResource("AccentSoft") : Brushes.Transparent, Cursor = Cursors.Hand,
        };
        root.MouseEnter += (_, _) => { if (!name.Equals(_statsFocus, StringComparison.OrdinalIgnoreCase)) root.Background = (Brush)FindResource("MenuHover"); };
        root.MouseLeave += (_, _) => { if (!name.Equals(_statsFocus, StringComparison.OrdinalIgnoreCase)) root.Background = Brushes.Transparent; };
        root.MouseLeftButtonUp += (_, _) =>
        {
            _statsFocus = name.Equals(_statsFocus, StringComparison.OrdinalIgnoreCase) ? null : name;
            _rankKey = ""; _dayKey = "";
            Refresh();
        };
        row.Root = root;
        _rankRows[name] = row;
        return row;
    }

    /// <summary>Horizontal gradient between two theme colours.</summary>
    private LinearGradientBrush Gradient(string from, string to)
    {
        var a = ((SolidColorBrush)FindResource(from)).Color;
        var b = ((SolidColorBrush)FindResource(to)).Color;
        var br = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 0));
        br.Freeze();
        return br;
    }

    /// <summary>Stacked bars, one per day: download at the bottom, upload on top.</summary>
    private void DrawDays()
    {
        if (DaySection.Visibility != Visibility.Visible) return;
        double w = DayChart.ActualWidth, h = DayChart.ActualHeight;
        if (w <= 0 || h <= 0 || _perDay.Count == 0) return;
        string key = $"{w:0}x{h:0}|" + string.Join(",", _perDay.Select(d => d.rx + ":" + d.tx));
        if (key == _dayKey) return;
        _dayKey = key;

        DayChart.Children.Clear();
        const double labelH = 20, top = 8, axisW = 48;
        double plotH = h - labelH - top, plotW = w - axisW, baseY = h - labelH;
        ulong peak = 1;
        foreach (var d in _perDay) peak = Math.Max(peak, d.rx + d.tx);
        DayMax.Text = $"最高 {Formatting.Bytes(peak)} / 天";
        double max = NiceCeil(peak);

        // Baseline plus a dashed guide at half and full scale, labelled on the left.
        var border = (Brush)FindResource("MenuBorder");
        var subtle = (Style)FindResource("Subtle");
        for (int g = 0; g <= 2; g++)
        {
            double y = Math.Round(baseY - plotH * g / 2) + 0.5;
            DayChart.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = axisW, X2 = w, Y1 = y, Y2 = y, Stroke = border, StrokeThickness = 1,
                StrokeDashArray = g == 0 ? null : new DoubleCollection { 2, 4 }, Opacity = g == 0 ? 1 : 0.7,
            });
            if (g == 0) continue;
            var tick = new TextBlock { Text = Tick(max * g / 2), Style = subtle, FontSize = 10.5, Width = axisW - 10, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(tick, 0); Canvas.SetTop(tick, y - 8);
            DayChart.Children.Add(tick);
        }

        int n = _perDay.Count;
        double slot = plotW / n;
        double barW = Math.Clamp(slot * 0.5, 6, 28);
        double radius = Math.Min(5, barW / 3);
        // One gradient over the whole plot height: tall days reach the light end, short days stay deep.
        var rxBrush = PlotGradient("ChartRx", "ChartRxEnd", baseY, top);
        var txBrush = PlotGradient("ChartTx", "ChartTxEnd", baseY, top);
        var today = (Brush)FindResource("MenuFg");
        int labelEvery = n <= 7 ? 1 : n <= 14 ? 2 : 5;
        var bars = new List<UIElement>(n);
        for (int i = 0; i < n; i++)
        {
            var (day, rx, tx) = _perDay[i];
            double x = axisW + i * slot + (slot - barW) / 2;
            bool last = i == n - 1;
            UIElement bar;
            if (rx + tx == 0)
            {
                bar = new System.Windows.Shapes.Path { Data = new RectangleGeometry(new Rect(x, baseY - 3, barW, 3), 1.5, 1.5), Fill = border };
            }
            else
            {
                // The column is clipped to one rounded shape; upload sits on download with a 1px seam
                // once it is tall enough to spare it.
                double total = Math.Max(3, plotH * (rx + tx) / max);
                double txH = tx == 0 ? 0 : Math.Max(2, total * tx / (rx + tx));
                double rxH = total - txH;
                var col = new Canvas { Clip = new RectangleGeometry(new Rect(x, baseY - total, barW, total + radius), radius, radius) };
                if (rxH > 0)
                    col.Children.Add(new System.Windows.Shapes.Path { Data = new RectangleGeometry(new Rect(x, baseY - rxH, barW, rxH)), Fill = rxBrush });
                if (txH > 0)
                    col.Children.Add(new System.Windows.Shapes.Path { Data = new RectangleGeometry(new Rect(x, baseY - total, barW, txH - (rxH > 0 && txH > 4 ? 1 : 0))), Fill = txBrush });
                bar = col;
            }
            DayChart.Children.Add(bar);
            bars.Add(bar);

            // Full-height hit area: the tooltip works over the whole day, and hovering dims the other days.
            var hit = new Border { Width = slot, Height = plotH + top, Background = Brushes.Transparent, ToolTip = $"{day:M月d日}\n↓ {Formatting.Bytes(rx)}   ↑ {Formatting.Bytes(tx)}" };
            Canvas.SetLeft(hit, axisW + i * slot); Canvas.SetTop(hit, 0);
            hit.MouseEnter += (_, _) => { foreach (var b in bars) b.Opacity = b == bar ? 1 : 0.4; };
            hit.MouseLeave += (_, _) => { foreach (var b in bars) b.Opacity = 1; };
            DayChart.Children.Add(hit);

            if (i % labelEvery == 0 || last)
            {
                var lbl = new TextBlock { Text = last ? "今天" : day.Day.ToString(), Style = subtle, FontSize = 11, Width = slot + 20, TextAlignment = TextAlignment.Center };
                if (last) { lbl.Foreground = today; lbl.FontWeight = FontWeights.SemiBold; }
                Canvas.SetLeft(lbl, axisW + i * slot - 10); Canvas.SetTop(lbl, baseY + 4);
                DayChart.Children.Add(lbl);
            }
        }
    }

    /// <summary>Bottom-to-top gradient pinned to the plot rather than to each bar.</summary>
    private LinearGradientBrush PlotGradient(string from, string to, double bottom, double top)
    {
        var br = new LinearGradientBrush(((SolidColorBrush)FindResource(from)).Color, ((SolidColorBrush)FindResource(to)).Color,
            new Point(0, bottom), new Point(0, top)) { MappingMode = BrushMappingMode.Absolute };
        br.Freeze();
        return br;
    }

    /// <summary>Rounds up to 1 / 1.5 / 2 / 3 / 4 / 5 / 6 / 8 × 10^k of a byte unit, so the guides land on readable values.</summary>
    private static double NiceCeil(double v)
    {
        double unit = 1;
        while (v / unit >= 1024) unit *= 1024;
        double m = v / unit, p = Math.Pow(10, Math.Floor(Math.Log10(m)));
        foreach (double s in new[] { 1, 1.5, 2, 3, 4, 5, 6, 8 })
            if (s * p >= m) return s * p * unit;
        return 10 * p * unit;
    }

    /// <summary>Axis label: <see cref="Formatting.Bytes"/> without trailing zeros ("4 GB", "1.5 GB").</summary>
    private static string Tick(double bytes)
    {
        string s = Formatting.Bytes(bytes);
        int sp = s.IndexOf(' ');
        string num = s[..sp];
        if (num.Contains('.')) num = num.TrimEnd('0').TrimEnd('.');
        return num + s[sp..];
    }

    // ---- programs ----------------------------------------------------------

    private void RefreshApps()
    {
        var today = _traffic.Log.Today();
        var groups = new Dictionary<string, List<AppRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _traffic.Rows)
        {
            if (!groups.TryGetValue(r.Name, out var g)) groups[r.Name] = g = new();
            g.Add(r);
        }

        var desired = new List<AppItem>(groups.Count + today.Count);
        double sumRx = 0, sumTx = 0, maxRate = 0;
        foreach (var (name, rows) in groups)
        {
            if (!Matches(name)) continue;
            var it = AppItemFor(name);
            double rx = 0, tx = 0; ulong sRx = 0, sTx = 0; int conns = 0;
            ImageSource? icon = null; string path = "";
            foreach (var r in rows)
            {
                rx += r.Rx; tx += r.Tx; sRx += r.SessionRx; sTx += r.SessionTx; conns += r.Connections;
                icon ??= r.Icon;
                if (path.Length == 0) path = r.Path;
                maxRate = Math.Max(maxRate, Math.Max(r.Rx, r.Tx));
            }
            sumRx += rx; sumTx += tx;
            maxRate = Math.Max(maxRate, Math.Max(rx, tx));
            today.TryGetValue(name, out var t);
            it.Icon = icon;
            it.IsGroup = rows.Count > 1;
            if (_expandAll && it.IsGroup) _expanded.Add(name);
            it.Expanded = it.IsGroup && _expanded.Contains(name);
            it.Count = rows.Count > 1 ? $"{rows.Count} 个进程" : "";
            it.Tip = (path.Length > 0 ? path + "\n" : "") + $"本次运行  ↓ {Formatting.Bytes(sRx)}   ↑ {Formatting.Bytes(sTx)}"
                     + (rows.Count == 1 ? $"\nPID {rows[0].Pid}" : $"\n{rows.Count} 个进程 · 点击展开");
            it.RxText = Rate(rx); it.TxText = Rate(tx);
            it.TodayRxText = Total(t.rx); it.TodayTxText = Total(t.tx);
            it.ConnText = conns > 0 ? conns.ToString() : "";
            it.Dim = false;
            it.SortRate = rx + tx; it.SortToday = t.rx + t.tx; it.SortConns = conns;
            it.SortRx = rx; it.SortTx = tx; it.SortTodayRx = t.rx; it.SortTodayTx = t.tx;
            it.Children = it.Expanded ? rows : null;
            desired.Add(it);
        }
        // Programs that moved bytes earlier today but are gone now.
        foreach (var (name, t) in today)
        {
            if (groups.ContainsKey(name) || !Matches(name)) continue;
            var it = AppItemFor(name);
            it.Icon = _traffic.IconFor(name); it.Count = ""; it.Tip = "已退出";
            it.IsGroup = false; it.Expanded = false; it.Children = null;
            it.RxText = it.TxText = it.ConnText = "";
            it.TodayRxText = Total(t.rx); it.TodayTxText = Total(t.tx);
            it.Dim = true;
            it.SortRate = 0; it.SortToday = t.rx + t.tx; it.SortConns = 0;
            it.SortRx = it.SortTx = 0; it.SortTodayRx = t.rx; it.SortTodayTx = t.tx;
            desired.Add(it);
        }

        desired.Sort((a, b) =>
        {
            int c = 0;
            if (_appSort is { } s)
            {
                c = s.key switch
                {
                    "name" => Str(a.Name, b.Name),
                    "rx" => a.SortRx.CompareTo(b.SortRx),
                    "tx" => a.SortTx.CompareTo(b.SortTx),
                    "todayRx" => a.SortTodayRx.CompareTo(b.SortTodayRx),
                    "todayTx" => a.SortTodayTx.CompareTo(b.SortTodayTx),
                    "conns" => a.SortConns.CompareTo(b.SortConns),
                    _ => 0,
                };
                c = Dir(s.desc, c);
            }
            if (c == 0) c = b.SortRate.CompareTo(a.SortRate);
            if (c == 0) c = b.SortToday.CompareTo(a.SortToday);
            if (c == 0) c = b.SortConns.CompareTo(a.SortConns);
            return c != 0 ? c : Str(a.Name, b.Name);
        });

        // Flatten: an expanded program is followed by one row per process, busiest first.
        var flat = new List<AppItem>(desired.Count + 8);
        var keepPids = new HashSet<int>();
        foreach (var it in desired)
        {
            it.RxBar = Bar(it.SortRx, maxRate); it.TxBar = Bar(it.SortTx, maxRate);
            flat.Add(it);
            if (it.Children == null) continue;
            foreach (var r in it.Children.OrderByDescending(r => r.Rx + r.Tx).ThenByDescending(r => r.Connections).ThenBy(r => r.Pid))
            {
                var c = PidItemFor(r.Pid, it.Name);
                c.RxText = Rate(r.Rx); c.TxText = Rate(r.Tx);
                c.RxBar = Bar(r.Rx, maxRate); c.TxBar = Bar(r.Tx, maxRate);
                c.ConnText = r.Connections > 0 ? r.Connections.ToString() : "";
                c.Tip = (r.Path.Length > 0 ? r.Path + "\n" : "") + $"本次运行  ↓ {Formatting.Bytes(r.SessionRx)}   ↑ {Formatting.Bytes(r.SessionTx)}";
                keepPids.Add(r.Pid);
                flat.Add(c);
            }
        }
        Sync(_apps, flat);
        var keepNames = desired.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _appItems.Keys.Where(k => !keepNames.Contains(k)).ToList()) _appItems.Remove(k);
        foreach (var k in _pidItems.Keys.Where(k => !keepPids.Contains(k)).ToList()) _pidItems.Remove(k);
        foreach (var n in _expanded.Where(n => !groups.ContainsKey(n)).ToList()) _expanded.Remove(n);

        AppEmpty.Visibility = flat.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppEmptyText.Text = _filter.Length > 0 ? "没有匹配的程序" : "还没有程序产生网络活动";
        Summary.Text = (_traffic.RatesAvailable
            ? $"{groups.Count} 个程序在用网络   ·   ↓ {RateOrZero(sumRx)}   ↑ {RateOrZero(sumTx)}"
            : $"{groups.Count} 个程序有网络连接") + (_filter.Length > 0 ? $"   ·   筛选 “{_filter}”" : "");
    }

    private static double Bar(double v, double max) => max <= 0 || v < 1 ? 0 : Math.Max(3, BarMax * v / max);

    private AppItem AppItemFor(string name)
    {
        if (!_appItems.TryGetValue(name, out var it)) _appItems[name] = it = new AppItem { Name = name };
        return it;
    }

    private AppItem PidItemFor(int pid, string parent)
    {
        if (!_pidItems.TryGetValue(pid, out var it))
            _pidItems[pid] = it = new AppItem { Name = $"PID {pid}", IsChild = true, Parent = parent };
        it.Parent = parent;
        return it;
    }

    // ---- connections -------------------------------------------------------

    private void RefreshConns()
    {
        bool activeOnly = ActiveOnly.IsChecked == true;
        var all = _traffic.Connections;
        var byName = new Dictionary<string, List<ConnItem>>(StringComparer.OrdinalIgnoreCase);
        int shown = 0;
        double maxRate = 0;
        foreach (var c in all)
        {
            var (rx, tx) = _traffic.RateOf(c);
            bool busy = rx >= 1 || tx >= 1;
            if (activeOnly && !busy && !c.Established) continue;
            var id = new ConnId(c.Pid, c.Tcp, c.V6, c.Local, c.Remote);
            var p = _traffic.ProcessOf(c.Pid);
            if (!_connItems.TryGetValue(id, out var it))
                _connItems[id] = it = new ConnItem { Pid = c.Pid, Proto = c.ProtoText, Local = c.LocalText, Remote = c.RemoteText, Tip = p.Path };
            if (_filter.Length > 0 && !Matches(p.Name) && !Matches(it.Remote) && !Matches(it.Local)) continue;
            it.State = c.StateText;
            it.RxText = Rate(rx); it.TxText = Rate(tx);
            it.SortRate = rx + tx; it.SortRx = rx; it.SortTx = tx;
            it.StateKind = c.Established ? 0 : c.Listening ? 2 : 1;
            it.Dim = c.Listening;
            it.ProcIcon = p.Icon;
            if (!byName.TryGetValue(p.Name, out var list)) byName[p.Name] = list = new();
            list.Add(it);
            shown++;
        }

        // One row per program; its connections are listed under it when expanded. A program with a
        // single connection is shown flat, with the connection's own columns.
        var groups = new List<ConnItem>(byName.Count);
        foreach (var (name, items) in byName)
        {
            if (items.Count == 1)
            {
                var only = items[0];
                only.Name = name; only.Icon = only.ProcIcon; only.IsChild = false; only.IsGroup = false; only.Expanded = false;
                only.Count = ""; only.Parent = ""; only.Children = null;
                maxRate = Math.Max(maxRate, Math.Max(only.SortRx, only.SortTx));
                groups.Add(only);
                continue;
            }
            if (!_connGroups.TryGetValue(name, out var g)) _connGroups[name] = g = new ConnItem { Name = name, IsGroup = true };
            double rx = 0, tx = 0; int est = 0, listen = 0, tcp = 0, udp = 0;
            foreach (var it in items)
            {
                rx += it.SortRx; tx += it.SortTx;
                if (it.StateKind == 0) est++; else if (it.StateKind == 2) listen++;
                if (it.Proto.StartsWith("TCP")) tcp++; else udp++;
                it.Name = $"PID {it.Pid}"; it.Icon = null; it.IsChild = true; it.Parent = name;
                maxRate = Math.Max(maxRate, Math.Max(it.SortRx, it.SortTx));
            }
            if (_expandAll) _connExpanded.Add(name);
            g.Icon = items[0].ProcIcon;
            g.Expanded = _connExpanded.Contains(name);
            g.Count = $"{items.Count} 个连接";
            g.Tip = items[0].Tip + (items[0].Tip.Length > 0 ? "\n" : "") + "点击展开";
            g.Proto = tcp > 0 && udp > 0 ? "TCP+UDP" : tcp > 0 ? "TCP" : "UDP";
            g.Local = ""; g.Remote = "";
            g.State = est > 0 && listen > 0 ? $"{est} 已连接 · {listen} 监听" : est > 0 ? $"{est} 已连接" : listen > 0 ? $"{listen} 监听" : $"{items.Count} 个";
            g.StateKind = est > 0 ? 0 : listen == items.Count ? 2 : 1;
            g.Dim = listen == items.Count;
            g.RxText = Rate(rx); g.TxText = Rate(tx);
            g.SortRate = rx + tx; g.SortRx = rx; g.SortTx = tx;
            g.Children = items;
            maxRate = Math.Max(maxRate, Math.Max(rx, tx));
            groups.Add(g);
        }

        int ByKey(ConnItem a, ConnItem b, string key) => key switch
        {
            "name" => Str(a.Name, b.Name),
            "proto" => Str(a.Proto, b.Proto),
            "local" => Str(a.Local, b.Local),
            "remote" => Str(a.Remote, b.Remote),
            "state" => a.StateKind.CompareTo(b.StateKind) is var sc && sc != 0 ? sc : Str(a.State, b.State),
            "rx" => a.SortRx.CompareTo(b.SortRx),
            "tx" => a.SortTx.CompareTo(b.SortTx),
            _ => 0,
        };
        int Compare(ConnItem a, ConnItem b)
        {
            int c = _connSort is { } s ? Dir(s.desc, ByKey(a, b, s.key)) : 0;
            if (c == 0) c = b.SortRate.CompareTo(a.SortRate);
            if (c == 0) c = a.StateKind.CompareTo(b.StateKind);
            if (c == 0) c = Str(a.Name, b.Name);
            return c != 0 ? c : string.Compare(a.Remote, b.Remote, StringComparison.Ordinal);
        }
        groups.Sort(Compare);

        var flat = new List<ConnItem>(shown + groups.Count);
        foreach (var g in groups)
        {
            g.RxBar = Bar(g.SortRx, maxRate); g.TxBar = Bar(g.SortTx, maxRate);
            flat.Add(g);
            if (g.Children == null || !g.Expanded) continue;
            var kids = g.Children.ToList();
            kids.Sort(Compare);
            foreach (var k in kids) { k.RxBar = Bar(k.SortRx, maxRate); k.TxBar = Bar(k.SortTx, maxRate); flat.Add(k); }
        }
        Sync(_conns, flat);
        if (_connItems.Count > shown + 512)
        {
            var keep = new HashSet<ConnItem>(byName.Values.SelectMany(v => v));
            foreach (var k in _connItems.Where(kv => !keep.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _connItems.Remove(k);
        }
        foreach (var k in _connGroups.Keys.Where(k => !byName.ContainsKey(k)).ToList()) _connGroups.Remove(k);
        foreach (var n in _connExpanded.Where(n => !byName.ContainsKey(n)).ToList()) _connExpanded.Remove(n);

        ConnEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnEmptyText.Text = _filter.Length > 0 ? "没有匹配的连接" : activeOnly ? "没有活动连接" : "没有连接";
        Summary.Text = $"{byName.Count} 个程序   ·   " + (activeOnly ? $"{shown} 个活动连接（共 {all.Count} 个）" : $"{shown} 个连接")
                       + (_filter.Length > 0 ? $"   ·   筛选 “{_filter}”" : "");
    }

    /// <summary>Brings <paramref name="col"/> to the order of <paramref name="desired"/> with moves and inserts, so scrolling survives.</summary>
    private static void Sync<T>(ObservableCollection<T> col, List<T> desired) where T : class
    {
        for (int i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            if (i < col.Count && ReferenceEquals(col[i], want)) continue;
            int at = -1;
            for (int j = i + 1; j < col.Count; j++) if (ReferenceEquals(col[j], want)) { at = j; break; }
            if (at < 0) col.Insert(i, want); else col.Move(at, i);
        }
        while (col.Count > desired.Count) col.RemoveAt(col.Count - 1);
    }
}

public abstract class RowItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private string _name = "", _rx = "", _tx = "";
    private ImageSource? _icon;
    private bool _dim;
    private double _rxBar, _txBar;
    public string Name { get => _name; set => Set(ref _name, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string RxText { get => _rx; set => Set(ref _rx, value); }
    public string TxText { get => _tx; set => Set(ref _tx, value); }
    public bool Dim { get => _dim; set => Set(ref _dim, value); }

    private string _count = "", _tip = "";
    private bool _isGroup, _expanded, _isChild;
    /// <summary>Badge next to the name ("15 个进程"), empty for none.</summary>
    public string Count { get => _count; set => Set(ref _count, value); }
    public string Tip { get => _tip; set => Set(ref _tip, value); }
    /// <summary>Several rows share the name; this one can be expanded.</summary>
    public bool IsGroup { get => _isGroup; set { if (Set(ref _isGroup, value)) OnChevron(); } }
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) OnChevron(); } }
    public string Chevron => !_isGroup ? "" : _expanded ? "" : "";
    private void OnChevron() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
    /// <summary>A detail row under an expanded group.</summary>
    public bool IsChild { get => _isChild; set => Set(ref _isChild, value); }
    public string Parent = "";
    /// <summary>Width of the faint bar behind the number, relative to the busiest row.</summary>
    public double RxBar { get => _rxBar; set => Set(ref _rxBar, Math.Round(value)); }
    public double TxBar { get => _txBar; set => Set(ref _txBar, Math.Round(value)); }
    public double SortRate, SortRx, SortTx;
}

public sealed class AppItem : RowItem
{
    private string _todayRx = "", _todayTx = "", _conn = "";
    public string TodayRxText { get => _todayRx; set => Set(ref _todayRx, value); }
    public string TodayTxText { get => _todayTx; set => Set(ref _todayTx, value); }
    public string ConnText { get => _conn; set => Set(ref _conn, value); }
    public List<AppRow>? Children;
    public ulong SortToday, SortTodayRx, SortTodayTx;
    public int SortConns;
}

public sealed class ConnItem : RowItem
{
    private string _state = "", _proto = "", _local = "", _remote = "";
    private int _kind = 2;
    public int Pid;
    /// <summary>The owning program's icon, kept while the row is shown as a child without one.</summary>
    public ImageSource? ProcIcon;
    public List<ConnItem>? Children;
    public string Proto { get => _proto; set => Set(ref _proto, value); }
    public string Local { get => _local; set => Set(ref _local, value); }
    public string Remote { get => _remote; set => Set(ref _remote, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    /// <summary>0 established, 1 in transition, 2 listening / idle.</summary>
    public int StateKind { get => _kind; set => Set(ref _kind, value); }
}
