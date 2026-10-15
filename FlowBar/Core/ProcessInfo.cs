using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowBar.Core;

public sealed class ProcessEntry
{
    public int Pid;
    public string Name = "";
    public string Path = "";
    public ImageSource? Icon;
}

/// <summary>Resolves process ids to names, paths and small icons. Entries are cached per pid; icons per path.</summary>
public sealed class ProcessInfo
{
    private readonly Dictionary<int, ProcessEntry> _cache = new();
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    public ProcessEntry Get(int pid)
    {
        if (_cache.TryGetValue(pid, out var e)) return e;
        e = Resolve(pid);
        _cache[pid] = e;
        return e;
    }

    /// <summary>Forgets pids not in <paramref name="seen"/>, so a reused pid is resolved afresh next time.</summary>
    public void Prune(HashSet<int> seen)
    {
        List<int>? gone = null;
        foreach (var pid in _cache.Keys)
            if (!seen.Contains(pid)) (gone ??= new()).Add(pid);
        if (gone != null) foreach (var pid in gone) _cache.Remove(pid);
    }

    private ProcessEntry Resolve(int pid)
    {
        var e = new ProcessEntry { Pid = pid };
        if (pid == 0) { e.Name = "（无进程）"; e.Icon = GenericIcon; return e; } // TIME_WAIT leftovers and the like
        if (pid == 4) { e.Name = "System"; e.Icon = GenericIcon; return e; }
        string path = Native.ProcessImagePath(pid);
        if (path.Length > 0)
        {
            e.Path = path;
            e.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            e.Icon = IconFor(path);
        }
        else
        {
            // Services and protected processes refuse even a limited-information handle to a
            // standard user; the name is still readable, and most of them live in System32.
            // Short-lived processes are often gone by the time their first event reaches us; lump
            // those together rather than filling the daily log with one "PID 1234" entry each.
            try { e.Name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch { e.Name = "（未知进程）"; }
            string guess = System.IO.Path.Combine(Environment.SystemDirectory, e.Name + ".exe");
            if (System.IO.File.Exists(guess)) e.Icon = IconFor(guess);
        }
        e.Icon ??= GenericIcon;
        return e;
    }

    private ImageSource? _generic;

    /// <summary>The shell's stock "application" icon, for processes whose file we cannot see.</summary>
    public ImageSource? GenericIcon => _generic ??= IconFor("app.exe", generic: true);

    /// <summary>
    /// Best-effort icon for a program that is not running: the path it last ran from when known, else a
    /// System32 namesake, else the stock icon.
    /// </summary>
    public ImageSource? IconForName(string name, string? lastPath = null)
    {
        if (!string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath))
        {
            var img = IconFor(lastPath);
            if (img != null) return img;
        }
        string guess = System.IO.Path.Combine(Environment.SystemDirectory, name + ".exe");
        return (System.IO.File.Exists(guess) ? IconFor(guess) : null) ?? GenericIcon;
    }

    private ImageSource? IconFor(string path, bool generic = false)
    {
        if (_icons.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            IntPtr h = Native.SmallIconOf(path, generic);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    img = bmp;
                }
                finally { Native.DestroyIcon(h); }
            }
        }
        catch (Exception ex) { Log.Write("icon failed: " + ex.Message); }
        _icons[path] = img;
        return img;
    }
}
