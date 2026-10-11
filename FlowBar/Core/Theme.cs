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
}
