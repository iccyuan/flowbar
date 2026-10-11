using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlowBar.Core;

namespace FlowBar.UI;

/// <summary>Font family / weight picker with live preview; changes apply to the taskbar as you browse.</summary>
public partial class FontDialog : Window
{
    private sealed record FontEntry(string Name, FontFamily Family);

    public static readonly (string Key, string Label)[] Weights =
    {
        ("Normal", "常规"), ("Medium", "中等"), ("SemiBold", "半粗"), ("Bold", "粗体"),
    };

    private readonly AppConfig _cfg;
    private readonly Action _apply;
    private readonly string _origFamily;
    private readonly string _origWeight;
    private readonly List<FontEntry> _all;
    private IntPtr _hwnd;
    private bool _done;

    public FontDialog(AppConfig cfg, Action apply)
    {
        _cfg = cfg;
        _apply = apply;
        _origFamily = cfg.FontFamily;
        _origWeight = cfg.FontWeight;
        InitializeComponent();

        _all = Fonts.SystemFontFamilies
            .Select(f => new FontEntry(DisplayName(f), f))
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !e.Name.StartsWith('@'))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Fill("");
        SearchBox.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            Fill(SearchBox.Text);
        };
        FamilyList.SelectionChanged += (_, _) =>
        {
            if (FamilyList.SelectedItem is FontEntry e) { _cfg.FontFamily = e.Name; Preview(); _apply(); }
        };

        foreach (var (key, label) in Weights)
        {
            var rb = new RadioButton { Content = label, Style = (Style)FindResource("Pill"), IsChecked = key == _cfg.FontWeight, Tag = key };
            rb.Checked += (_, _) => { _cfg.FontWeight = key; Preview(); _apply(); };
            WeightRow.Children.Add(rb);
        }

        OkButton.Click += (_, _) => { _done = true; _cfg.Save(); Close(); };
        CancelButton.Click += (_, _) => Cancel();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Cancel(); };
        Closed += (_, _) => { if (!_done) Revert(); };
        Preview();
    }

    private static string DisplayName(FontFamily f)
    {
        if (f.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage("zh-cn"), out var zh)) return zh;
        if (f.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage("en-us"), out var en)) return en;
        return f.FamilyNames.Values.FirstOrDefault() ?? f.Source;
    }

    private void Fill(string filter)
    {
        var items = string.IsNullOrWhiteSpace(filter) ? _all
            : _all.Where(e => e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        FamilyList.ItemsSource = items;
        var cur = items.FirstOrDefault(e => e.Name.Equals(_cfg.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (cur != null) { FamilyList.SelectedItem = cur; FamilyList.ScrollIntoView(cur); }
    }

    private void Preview()
    {
        var fam = new FontFamily(_cfg.FontFamily + ", Segoe UI");
        var w = WeightOf(_cfg.FontWeight);
        foreach (var tb in new[] { PreviewLight, PreviewDark })
        {
            tb.FontFamily = fam; tb.FontWeight = w; tb.FontSize = 14;
            System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        }
    }

    public static FontWeight WeightOf(string key) => key switch
    {
        "Medium" => FontWeights.Medium,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.Normal,
    };

    private void Cancel() { Revert(); _done = true; Close(); }

    private void Revert()
    {
        if (_cfg.FontFamily == _origFamily && _cfg.FontWeight == _origWeight) return;
        _cfg.FontFamily = _origFamily;
        _cfg.FontWeight = _origWeight;
        _apply();
    }

    /// <summary>Opens centred on <paramref name="anchor"/> (screen px), below or above the taskbar.</summary>
    public void ShowAt(RECT anchor, bool openBelow)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Opacity = 0;
        Show();
        UpdateLayout();
        double s = Native.DpiOf(_hwnd) / 96.0;
        int w = (int)Math.Round(ActualWidth * s), h = (int)Math.Round(ActualHeight * s);
        int shadow = (int)Math.Round(16 * s);
        var mi = Native.MonitorInfoFor(ref anchor);
        int x = (anchor.Left + anchor.Right) / 2 - w / 2;
        int y = openBelow ? anchor.Bottom - shadow + 2 : anchor.Top - h + shadow - 2;
        x = Math.Clamp(x, mi.rcWork.Left - shadow + 4, Math.Max(mi.rcWork.Left, mi.rcWork.Right - w + shadow - 4));
        y = Math.Clamp(y, mi.rcWork.Top - shadow + 4, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - h + shadow - 4));
        FrostedGlass.Capture(new RECT { Left = x + shadow - 4, Top = y + shadow - 4, Right = x + w - shadow + 4, Bottom = y + h - shadow + 4 });
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
        FrostedGlass.Refresh(Glass);
        Activate();
        SearchBox.Focus();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }
}
