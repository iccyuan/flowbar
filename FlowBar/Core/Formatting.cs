using System.Globalization;

namespace FlowBar.Core;

public enum UnitStyle
{
    /// <summary>12.3 KB/s</summary>
    Bytes,
    /// <summary>12.3K</summary>
    Compact,
    /// <summary>98.4 Mb/s</summary>
    Bits,
}

public static class Formatting
{
    private static readonly string[] ByteUnits = { "B/s", "KB/s", "MB/s", "GB/s" };
    private static readonly string[] CompactUnits = { "B", "K", "M", "G" };
    private static readonly string[] BitUnits = { "b/s", "Kb/s", "Mb/s", "Gb/s" };

    /// <summary>Splits a rate into a fixed-width number part and a unit part.</summary>
    public static (string value, string unit) Rate(double bytesPerSec, UnitStyle style)
    {
        if (bytesPerSec < 0 || double.IsNaN(bytesPerSec)) bytesPerSec = 0;
        string[] units;
        double v;
        double step;
        if (style == UnitStyle.Bits) { v = bytesPerSec * 8; units = BitUnits; step = 1000; }
        else { v = bytesPerSec; units = style == UnitStyle.Compact ? CompactUnits : ByteUnits; step = 1024; }

        int u = 0;
        while (v >= step * 0.9995 && u < units.Length - 1) { v /= step; u++; }

        string s;
        if (u == 0) s = ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        else if (v < 10) s = v.ToString("0.00", CultureInfo.InvariantCulture);
        else if (v < 100) s = v.ToString("0.0", CultureInfo.InvariantCulture);
        else s = v.ToString("0", CultureInfo.InvariantCulture);
        return (s, units[u]);
    }

    /// <summary>"1.23 GB" style totals.</summary>
    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        string s = u == 0 ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v < 10 ? v.ToString("0.00", CultureInfo.InvariantCulture)
            : v < 100 ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0", CultureInfo.InvariantCulture);
        return s + " " + units[u];
    }

    /// <summary>The widest number this style can print; used to size the taskbar widget.</summary>
    public static string WidestValue(UnitStyle _) => "999.9";
    public static string WidestUnit(UnitStyle style) => style switch
    {
        UnitStyle.Compact => "M",
        UnitStyle.Bits => "Mb/s",
        _ => "MB/s",
    };
}
