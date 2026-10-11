using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace FlowBar.Core;

public enum Placement
{
    /// <summary>Embedded in the taskbar, right before the notification area.</summary>
    Tray,
    /// <summary>Embedded in the taskbar, right after the Start button.</summary>
    Left,
    /// <summary>A free-floating always-on-top widget.</summary>
    Floating,
}

public enum Layout { Auto, TwoLines, OneLine }

public enum TextTone { Auto, Light, Dark }

public sealed class AppConfig
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowBar");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    public int IntervalMs { get; set; } = 1000;
    public Placement Placement { get; set; } = Placement.Tray;
    public Layout Layout { get; set; } = Layout.OneLine;
    public UnitStyle Unit { get; set; } = UnitStyle.Bytes;
    public TextTone TextTone { get; set; } = TextTone.Auto;
    /// <summary>Key into <see cref="Theme.Schemes"/>; empty = plain light/dark text.</summary>
    public string ColorScheme { get; set; } = "";
    /// <summary>0 = pick from taskbar height.</summary>
    public double FontSize { get; set; } = 0;
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>null = automatic (physical adapters).</summary>
    public Guid? Adapter { get; set; }
    public bool ShowArrows { get; set; } = true;
    /// <summary>Floating placement position, physical pixels.</summary>
    public int FloatX { get; set; } = -1;
    public int FloatY { get; set; } = -1;
    public bool FloatHideWhenFullscreen { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
        }
        catch (Exception ex) { Log.Write("config load failed: " + ex.Message); }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write("config save failed: " + ex.Message); }
    }

    // ---- autostart (HKCU Run key) -----------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "FlowBar";

    public static bool AutostartEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string s && s.Contains("FlowBar", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static readonly string FilePath = Path.Combine(AppConfig.Dir, "flowbar.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
