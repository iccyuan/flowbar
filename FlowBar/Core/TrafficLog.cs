using System.IO;
using System.Text.Json;

namespace FlowBar.Core;

/// <summary>Per-day traffic totals, persisted to %AppData%\FlowBar\traffic.json.</summary>
public sealed class TrafficLog
{
    private static readonly string FilePath = Path.Combine(AppConfig.Dir, "traffic.json");

    private sealed class Store
    {
        public Dictionary<string, ulong[]> Days { get; set; } = new();
    }

    private readonly Store _store;
    private readonly object _gate = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.Now;

    public TrafficLog()
    {
        _store = new Store();
        try
        {
            if (File.Exists(FilePath))
                _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new Store();
        }
        catch (Exception ex) { Log.Write("traffic log load failed: " + ex.Message); }
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    public void Add(ulong rx, ulong tx)
    {
        if (rx == 0 && tx == 0) return;
        lock (_gate)
        {
            string k = Key(DateTime.Now);
            if (!_store.Days.TryGetValue(k, out var v)) _store.Days[k] = v = new ulong[2];
            v[0] += rx;
            v[1] += tx;
            _dirty = true;
            // keep a month of history
            if (_store.Days.Count > 40)
                foreach (var old in _store.Days.Keys.OrderBy(x => x).Take(_store.Days.Count - 31).ToList())
                    _store.Days.Remove(old);
        }
        if ((DateTime.Now - _lastSave).TotalSeconds > 30) Save();
    }

    public (ulong rx, ulong tx) Today()
    {
        lock (_gate)
            return _store.Days.TryGetValue(Key(DateTime.Now), out var v) ? (v[0], v[1]) : (0UL, 0UL);
    }

    public (ulong rx, ulong tx) Month()
    {
        lock (_gate)
        {
            string prefix = DateTime.Now.ToString("yyyy-MM");
            ulong rx = 0, tx = 0;
            foreach (var (k, v) in _store.Days)
                if (k.StartsWith(prefix)) { rx += v[0]; tx += v[1]; }
            return (rx, tx);
        }
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
            catch (Exception ex) { Log.Write("traffic log save failed: " + ex.Message); }
        }
    }
}
