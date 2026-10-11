using System.Windows.Media;
using Microsoft.Win32;

namespace FlowBar.Core;

public static class Theme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Apps (and our popups) follow this; the taskbar follows SystemUsesLightTheme.</summary>
    public static bool AppsUseLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Personalize);
        return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
    }

    public static bool SystemUsesLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Personalize);
        return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    /// <summary>
    /// Average perceived luminance (0..1) of what is on screen inside <paramref name="screenRect"/>.
    /// Used to decide whether the widget text should be dark or light on a custom-coloured or
    /// translucent taskbar. Samples a small grid, so it is cheap enough to call on theme changes.
    /// </summary>
    public static double SampleLuminance(RECT screenRect)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0) return SystemUsesLight() ? 0.9 : 0.1;
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return SystemUsesLight() ? 0.9 : 0.1;
        try
        {
            double sum = 0; int n = 0;
            const int cols = 8, rows = 3;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x = screenRect.Left + (int)((c + 0.5) * screenRect.Width / cols);
                    int y = screenRect.Top + (int)((r + 0.5) * screenRect.Height / rows);
                    uint px = Native.GetPixel(dc, x, y);
                    if (px == 0xFFFFFFFF) continue; // CLR_INVALID
                    double rr = (px & 0xFF) / 255.0, gg = ((px >> 8) & 0xFF) / 255.0, bb = ((px >> 16) & 0xFF) / 255.0;
                    sum += 0.2126 * rr + 0.7152 * gg + 0.0722 * bb;
                    n++;
                }
            return n == 0 ? (SystemUsesLight() ? 0.9 : 0.1) : sum / n;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
    }

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>Linear blend of <paramref name="c"/> towards <paramref name="to"/> by <paramref name="t"/> (0..1).</summary>
    public static Color Blend(Color c, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(c.R + (to.R - c.R) * t), (byte)Math.Round(c.G + (to.G - c.G) * t), (byte)Math.Round(c.B + (to.B - c.B) * t));

    /// <summary>
    /// Chart colours for the popup: (download start, download end, upload start, upload end).
    /// Follows the taskbar text scheme when one is chosen; a single-colour scheme lightens the upload
    /// line one step so the two series stay distinguishable.
    /// </summary>
    public static (Color rx, Color rxEnd, Color tx, Color txEnd) ChartColors(string? scheme, bool lightUi)
    {
        if (!Schemes.Any(s => s.Key == scheme))
        {
            return lightUi
                ? (Hex("#2563EB"), Hex("#06B6D4"), Hex("#DB2777"), Hex("#8B5CF6"))
                : (Hex("#60A5FA"), Hex("#22D3EE"), Hex("#F472B6"), Hex("#A78BFA"));
        }
        var (up, down) = SchemeColors(scheme, darkBackdrop: !lightUi);
        if (up == down) up = Blend(up, lightUi ? Colors.White : Colors.Black, lightUi ? 0.45 : 0.35);
        return (down, Blend(down, Colors.White, 0.35), up, Blend(up, Colors.White, 0.35));
    }

    /// <summary>
    /// Taskbar text colour presets. Each has a bright variant for dark taskbars and a deep variant
    /// for light ones; upload and download rows may differ.
    /// </summary>
    public sealed record Scheme(string Key, string Name, string UpOnDark, string DownOnDark, string UpOnLight, string DownOnLight);

    // Open Color shades: 7–8 on light taskbars, 3–4 on dark ones. (up, down) per backdrop.
    // All schemes are two-colour; single-colour presets were dropped on request.
    public static readonly Scheme[] Schemes =
    {
        new("duo-classic", "经典 · 上绿 / 下蓝", "#69DB7C", "#74C0FC", "#2F9E44", "#1971C2"),
        new("duo-sunset",  "日落 · 上橙 / 下蓝", "#FFA94D", "#4DABF7", "#E8590C", "#1C7ED6"),
        new("duo-coral",   "珊瑚 · 上红 / 下青", "#FF8787", "#3BC9DB", "#E03131", "#0C8599"),
        new("duo-orchid",  "兰花 · 上粉 / 下紫", "#FAA2C1", "#B197FC", "#D6336C", "#7048E8"),
        new("duo-lime",    "青柠 · 上黄绿 / 下靛蓝", "#C0EB75", "#91A7FF", "#66A80F", "#3B5BDB"),
    };

    /// <summary>Resolves (upload, download) colours for a scheme key on a dark or light backdrop.</summary>
    public static (Color up, Color down) SchemeColors(string? key, bool darkBackdrop)
    {
        var s = Schemes.FirstOrDefault(x => x.Key == key);
        if (s == null)
        {
            var c = darkBackdrop ? Colors.White : Color.FromRgb(0x1B, 0x1B, 0x1B);
            return (c, c);
        }
        return darkBackdrop ? (Hex(s.UpOnDark), Hex(s.DownOnDark)) : (Hex(s.UpOnLight), Hex(s.DownOnLight));
    }
}
