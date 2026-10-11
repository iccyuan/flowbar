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

    /// <summary>
    /// Taskbar text colour presets. Each has a bright variant for dark taskbars and a deep variant
    /// for light ones; upload and download rows may differ.
    /// </summary>
    public sealed record Scheme(string Key, string Name, string UpOnDark, string DownOnDark, string UpOnLight, string DownOnLight);

    public static readonly Scheme[] Schemes =
    {
        new("duo-blue-amber", "双色 · 蓝 / 琥珀", "#FBBF24", "#7CB8FF", "#C2410C", "#1D4ED8"),
        new("duo-teal-green", "双色 · 青 / 绿",  "#4ADE80", "#22D3EE", "#15803D", "#0E7490"),
        new("duo-pink-violet", "双色 · 粉 / 紫", "#F9A8D4", "#C4B5FD", "#BE185D", "#6D28D9"),
        new("sky",    "天蓝", "#7DD3FC", "#7DD3FC", "#0369A1", "#0369A1"),
        new("mint",   "薄荷", "#6EE7B7", "#6EE7B7", "#047857", "#047857"),
        new("amber",  "琥珀", "#FCD34D", "#FCD34D", "#B45309", "#B45309"),
        new("rose",   "玫红", "#FDA4AF", "#FDA4AF", "#BE123C", "#BE123C"),
        new("violet", "紫罗兰", "#C4B5FD", "#C4B5FD", "#6D28D9", "#6D28D9"),
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
