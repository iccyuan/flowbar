using System.IO;
using System.Text.Json;

namespace FlowBar.Core;

/// <summary>Per-day, per-program traffic totals, persisted to %AppData%\FlowBar\apps.json.</summary>
public sealed class AppTrafficLog
{
    private static readonly string FilePath = Path.Combine(AppConfig.Dir, "apps.json");

    private sealed class Store
    {
        /// <summary>day → program name → [rx, tx]</summary>
        public Dictionary<string, Dictionary<string, ulong[]>> Days { get; set; } = new();
        /// <summary>program name → last image path seen, so exited programs keep their icon</summary>
        public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Store _store;
    private readonly object _gate = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.Now;

    public AppTrafficLog()
    {
        _store = new Store();
        try
        {
            if (File.Exists(FilePath))
                _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new Store();
        }
        catch (Exception ex) { Log.Write("app traffic log load failed: " + ex.Message); }
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    public void RememberPath(string name, string path)
    {
        if (name.Length == 0 || path.Length == 0) return;
        lock (_gate)
        {
            if (_store.Paths.TryGetValue(name, out var cur) && cur == path) return;
            _store.Paths[name] = path;
            _dirty = true;
        }
    }

    public string? PathOf(string name)
    {
        lock (_gate) return _store.Paths.TryGetValue(name, out var p) ? p : null;
    }

    public void Add(string name, ulong rx, ulong tx)
    {
        if (rx == 0 && tx == 0 || name.Length == 0) return;
        lock (_gate)
        {
            string k = Key(DateTime.Now);
            if (!_store.Days.TryGetValue(k, out var day)) _store.Days[k] = day = new(StringComparer.OrdinalIgnoreCase);
            if (!day.TryGetValue(name, out var v)) day[name] = v = new ulong[2];
            v[0] += rx;
            v[1] += tx;
            _dirty = true;
            if (_store.Days.Count > 40)
            {
                foreach (var old in _store.Days.Keys.OrderBy(x => x).Take(_store.Days.Count - 31).ToList())
                    _store.Days.Remove(old);
                var live = new HashSet<string>(_store.Days.Values.SelectMany(d => d.Keys), StringComparer.OrdinalIgnoreCase);
                foreach (var n in _store.Paths.Keys.Where(n => !live.Contains(n)).ToList()) _store.Paths.Remove(n);
            }
        }
        if ((DateTime.Now - _lastSave).TotalSeconds > 30) Save();
    }

    /// <summary>Today's totals for every program that moved bytes, keyed by name (case-insensitive).</summary>
    public Dictionary<string, (ulong rx, ulong tx)> Today()
    {
        var result = new Dictionary<string, (ulong, ulong)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_store.Days.TryGetValue(Key(DateTime.Now), out var day))
                foreach (var (n, v) in day) result[n] = (v[0], v[1]);
        }
        return result;
    }

    /// <summary>A copy of everything: day ("yyyy-MM-dd") → program → (rx, tx).</summary>
    public Dictionary<string, Dictionary<string, (ulong rx, ulong tx)>> Snapshot()
    {
        var result = new Dictionary<string, Dictionary<string, (ulong rx, ulong tx)>>();
        lock (_gate)
        {
            foreach (var (k, day) in _store.Days)
            {
                var d = new Dictionary<string, (ulong rx, ulong tx)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (n, v) in day) d[n] = (v[0], v[1]);
                result[k] = d;
            }
        }
        return result;
    }

    /// <summary>This month's totals per program.</summary>
    public Dictionary<string, (ulong rx, ulong tx)> Month()
    {
        var result = new Dictionary<string, (ulong rx, ulong tx)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            string prefix = DateTime.Now.ToString("yyyy-MM");
            foreach (var (k, day) in _store.Days)
            {
                if (!k.StartsWith(prefix)) continue;
                foreach (var (n, v) in day)
                {
                    result.TryGetValue(n, out var t);
                    result[n] = (t.rx + v[0], t.tx + v[1]);
                }
            }
        }
        return result;
    }

    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(AppConfig.Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_store));
                _dirty = false;
                _lastSave = DateTime.Now;
            }
            catch (Exception ex) { Log.Write("app traffic log save failed: " + ex.Message); }
        }
    }
}
