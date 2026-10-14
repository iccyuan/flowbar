using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Dictionary<ConnId, ConnItem> _connItems = new();
    private IntPtr _hwnd;

    private readonly record struct ConnId(int Pid, bool Tcp, bool V6, Endpoint Local, Endpoint Remote);

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
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        AppList.SizeChanged += (_, _) => FitColumns();
        ConnList.SizeChanged += (_, _) => FitColumns();
        foreach (var col in ((GridView)AppList.View).Columns.Concat(((GridView)ConnList.View).Columns))
            _headerText[col] = (string)col.Header;
        AppList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) => OnHeaderClick(e, apps: true)));
        ConnList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) => OnHeaderClick(e, apps: false)));

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
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

    public void ShowConnections() => TabConns.IsChecked = true;
    public void ShowStats(int days = 0)
    {
        if (days == 7) RangeWeek.IsChecked = true; else if (days == 30) RangeMonth.IsChecked = true;
        TabStats.IsChecked = true;
    }

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

    private void SwitchTab()
    {
        bool apps = TabApps.IsChecked == true, conns = TabConns.IsChecked == true, stats = TabStats.IsChecked == true;
        AppList.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        ConnList.Visibility = conns ? Visibility.Visible : Visibility.Collapsed;
        StatsView.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        ActiveOnly.Visibility = conns ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void FitColumns()
    {
        double w = AppList.ActualWidth;
        if (w > 0) NameCol.Width = Math.Max(160, w - (104 * 4 + 64) - 28);
        double cw = ConnList.ActualWidth;
        if (cw > 0) ProcCol.Width = Math.Max(120, cw - (54 + 164 + 176 + 92 + 84 * 2) - 28);
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
        StatsSummary.Text = perName.Count == 0 ? "" : $"↓ {Formatting.Bytes(totRx)}    ↑ {Formatting.Bytes(totTx)}    ·    {perName.Count} 个程序";
        StatsEmpty.Visibility = perName.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RankHint.Visibility = perName.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

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
            row.Rx.MinWidth = t.rx > 0 ? 2 : 0; // a sliver still shows there was something
            row.Tx.MinWidth = t.tx > 0 ? 2 : 0;
            row.Rest.Width = new GridLength(Math.Max(0, (double)max - t.rx - t.tx), GridUnitType.Star);
            row.RxText.Text = Formatting.Bytes(t.rx);
            row.TxText.Text = Formatting.Bytes(t.tx);
        }

        DaySection.Visibility = _statsDays > 1 && perName.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DayTitle.Text = _statsFocus == null ? "每日流量" : $"每日流量 · {_statsFocus}";
        _perDay = perDay;
        DrawDays();
        Summary.Text = _statsDays == 1 ? "今日按程序累计" : $"最近 {_statsDays} 天按程序累计";
    }

    private RankRow MakeRankRow(string name)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

        var icon = new Image { Source = _traffic.IconFor(name), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        var label = new TextBlock { Text = name, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var row = new RankRow { Rx = new ColumnDefinition(), Tx = new ColumnDefinition(), Rest = new ColumnDefinition() };
        var bar = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        bar.ColumnDefinitions.Add(row.Rx);
        bar.ColumnDefinitions.Add(row.Tx);
        bar.ColumnDefinitions.Add(row.Rest);
        var rxBar = new Border { Background = (Brush)FindResource("ChartRx"), CornerRadius = new CornerRadius(3, 0, 0, 3) };
        var txBar = new Border { Background = (Brush)FindResource("ChartTx"), CornerRadius = new CornerRadius(0, 3, 3, 0) };
        Grid.SetColumn(txBar, 1);
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
            Child = grid, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, -6, 0),
            Background = focused ? (Brush)FindResource("MenuHover") : Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
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
        const double labelH = 18, top = 6;
        double plotH = h - labelH - top;
        ulong max = 1;
        foreach (var d in _perDay) max = Math.Max(max, d.rx + d.tx);
        DayMax.Text = $"最高 {Formatting.Bytes(max)} / 天";

        var border = (Brush)FindResource("MenuBorder");
        DayChart.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = h - labelH + 0.5, Y2 = h - labelH + 0.5, Stroke = border, StrokeThickness = 1 });

        int n = _perDay.Count;
        double slot = w / n;
        double barW = Math.Clamp(slot * 0.62, 6, 30);
        var rxBrush = (Brush)FindResource("ChartRx");
        var txBrush = (Brush)FindResource("ChartTx");
        var subtle = (Style)FindResource("Subtle");
        int labelEvery = n <= 7 ? 1 : n <= 14 ? 2 : 5;
        for (int i = 0; i < n; i++)
        {
            var (day, rx, tx) = _perDay[i];
            double x = i * slot + (slot - barW) / 2;
            double rxH = plotH * rx / max, txH = plotH * tx / max;
            string tip = $"{day:M月d日}\n↓ {Formatting.Bytes(rx)}   ↑ {Formatting.Bytes(tx)}";
            if (rx + tx == 0)
            {
                var stub = new Border { Width = barW, Height = 2, Background = border, Opacity = 0.8, ToolTip = tip };
                Canvas.SetLeft(stub, x); Canvas.SetTop(stub, h - labelH - 2);
                DayChart.Children.Add(stub);
            }
            else
            {
                var rxBar = new Border { Width = barW, Height = Math.Max(1, rxH), Background = rxBrush, CornerRadius = txH < 1 ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(0), ToolTip = tip };
                Canvas.SetLeft(rxBar, x); Canvas.SetTop(rxBar, h - labelH - rxBar.Height);
                DayChart.Children.Add(rxBar);
                if (txH >= 1)
                {
                    var txBar = new Border { Width = barW, Height = txH, Background = txBrush, CornerRadius = new CornerRadius(3, 3, 0, 0), ToolTip = tip };
                    Canvas.SetLeft(txBar, x); Canvas.SetTop(txBar, h - labelH - rxBar.Height - txH);
                    DayChart.Children.Add(txBar);
                }
            }
            bool last = i == n - 1;
            if (i % labelEvery == 0 || last)
            {
                var lbl = new TextBlock { Text = last ? "今天" : day.Day.ToString(), Style = subtle, FontSize = 11, Width = slot, TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(lbl, i * slot); Canvas.SetTop(lbl, h - labelH + 3);
                DayChart.Children.Add(lbl);
            }
        }
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
        double sumRx = 0, sumTx = 0;
        foreach (var (name, rows) in groups)
        {
            var it = AppItemFor(name);
            double rx = 0, tx = 0; ulong sRx = 0, sTx = 0; int conns = 0;
            ImageSource? icon = null; string path = "";
            foreach (var r in rows)
            {
                rx += r.Rx; tx += r.Tx; sRx += r.SessionRx; sTx += r.SessionTx; conns += r.Connections;
                icon ??= r.Icon;
                if (path.Length == 0) path = r.Path;
            }
            sumRx += rx; sumTx += tx;
            today.TryGetValue(name, out var t);
            it.Icon = icon;
            it.Count = rows.Count > 1 ? $"×{rows.Count}" : "";
            it.Tip = (path.Length > 0 ? path + "\n" : "") + $"本次运行  ↓ {Formatting.Bytes(sRx)}   ↑ {Formatting.Bytes(sTx)}"
                     + (rows.Count == 1 ? $"\nPID {rows[0].Pid}" : $"\n{rows.Count} 个进程");
            it.RxText = Rate(rx); it.TxText = Rate(tx);
            it.TodayRxText = Total(t.rx); it.TodayTxText = Total(t.tx);
            it.ConnText = conns > 0 ? conns.ToString() : "";
            it.Dim = false;
            it.SortRate = rx + tx; it.SortToday = t.rx + t.tx; it.SortConns = conns;
            it.SortRx = rx; it.SortTx = tx; it.SortTodayRx = t.rx; it.SortTodayTx = t.tx;
            desired.Add(it);
        }
        // Programs that moved bytes earlier today but are gone now.
        foreach (var (name, t) in today)
        {
            if (groups.ContainsKey(name)) continue;
            var it = AppItemFor(name);
            it.Icon = null; it.Count = ""; it.Tip = "已退出";
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
        Sync(_apps, desired);
        var keepNames = desired.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _appItems.Keys.Where(k => !keepNames.Contains(k)).ToList()) _appItems.Remove(k);

        Summary.Text = _traffic.RatesAvailable
            ? $"{groups.Count} 个程序在用网络   ↓ {RateOrZero(sumRx)}   ↑ {RateOrZero(sumTx)}"
            : $"{groups.Count} 个程序有网络连接";
    }

    private AppItem AppItemFor(string name)
    {
        if (!_appItems.TryGetValue(name, out var it)) _appItems[name] = it = new AppItem { Name = name };
        return it;
    }

    // ---- connections -------------------------------------------------------

    private void RefreshConns()
    {
        bool activeOnly = ActiveOnly.IsChecked == true;
        var all = _traffic.Connections;
        var desired = new List<ConnItem>(all.Count);
        foreach (var c in all)
        {
            var (rx, tx) = _traffic.RateOf(c);
            bool busy = rx >= 1 || tx >= 1;
            if (activeOnly && !busy && !c.Established) continue;
            var id = new ConnId(c.Pid, c.Tcp, c.V6, c.Local, c.Remote);
            if (!_connItems.TryGetValue(id, out var it))
            {
                var p = _traffic.ProcessOf(c.Pid);
                _connItems[id] = it = new ConnItem
                {
                    Name = p.Name, Icon = p.Icon, Proto = c.ProtoText, Local = c.LocalText, Remote = c.RemoteText,
                };
            }
            it.State = c.StateText;
            it.RxText = Rate(rx); it.TxText = Rate(tx);
            it.SortRate = rx + tx; it.SortRx = rx; it.SortTx = tx;
            it.SortState = c.Established ? 0 : c.Listening ? 2 : 1;
            it.Dim = c.Listening;
            desired.Add(it);
        }
        desired.Sort((a, b) =>
        {
            int c = 0;
            if (_connSort is { } s)
            {
                c = s.key switch
                {
                    "name" => Str(a.Name, b.Name),
                    "proto" => Str(a.Proto, b.Proto),
                    "local" => Str(a.Local, b.Local),
                    "remote" => Str(a.Remote, b.Remote),
                    "state" => a.SortState.CompareTo(b.SortState) is var sc && sc != 0 ? sc : Str(a.State, b.State),
                    "rx" => a.SortRx.CompareTo(b.SortRx),
                    "tx" => a.SortTx.CompareTo(b.SortTx),
                    _ => 0,
                };
                c = Dir(s.desc, c);
            }
            if (c == 0) c = b.SortRate.CompareTo(a.SortRate);
            if (c == 0) c = a.SortState.CompareTo(b.SortState);
            if (c == 0) c = Str(a.Name, b.Name);
            return c != 0 ? c : string.Compare(a.Remote, b.Remote, StringComparison.Ordinal);
        });
        Sync(_conns, desired);
        if (_connItems.Count > desired.Count + 512)
        {
            var keep = desired.ToHashSet();
            foreach (var k in _connItems.Where(kv => !keep.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _connItems.Remove(k);
        }
        Summary.Text = activeOnly ? $"{desired.Count} 个活动连接（共 {all.Count} 个）" : $"{all.Count} 个连接";
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
    public string Name { get => _name; set => Set(ref _name, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string RxText { get => _rx; set => Set(ref _rx, value); }
    public string TxText { get => _tx; set => Set(ref _tx, value); }
    public bool Dim { get => _dim; set => Set(ref _dim, value); }
    public double SortRate, SortRx, SortTx;
}

public sealed class AppItem : RowItem
{
    private string _count = "", _tip = "", _todayRx = "", _todayTx = "", _conn = "";
    public string Count { get => _count; set => Set(ref _count, value); }
    public string Tip { get => _tip; set => Set(ref _tip, value); }
    public string TodayRxText { get => _todayRx; set => Set(ref _todayRx, value); }
    public string TodayTxText { get => _todayTx; set => Set(ref _todayTx, value); }
    public string ConnText { get => _conn; set => Set(ref _conn, value); }
    public ulong SortToday, SortTodayRx, SortTodayTx;
    public int SortConns;
}

public sealed class ConnItem : RowItem
{
    private string _state = "";
    public string Proto { get; init; } = "";
    public string Local { get; init; } = "";
    public string Remote { get; init; } = "";
    public string State { get => _state; set => Set(ref _state, value); }
    public int SortState;
}
