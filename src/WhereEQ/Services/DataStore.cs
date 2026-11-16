using System.IO;
using System.Text.Json;
using WhereEQ.Models;

namespace WhereEQ.Services;

/// <summary>
/// Finds and keeps up to date every *-Inventory.txt, *-Spellbook.txt and eqlog_*.txt in their configured
/// folders (each type has its own folder; they may be the same folder).
/// </summary>
public sealed class DataStore : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CharacterInventory> _inventories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (List<SpellEntry> spells, DateTime time)> _spellbooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogState> _logs = new(StringComparer.OrdinalIgnoreCase); // active logs, by path
    private Dictionary<string, LogState> _cache = new(StringComparer.OrdinalIgnoreCase);  // remembered read positions
    private readonly Dictionary<string, (string character, List<string> friends, DateTime time)> _inis = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _debounce;

    public Settings Settings { get; }
    public SpellDatabase Spells { get; }

    /// <summary>Raised (on a background thread) after files were (re)loaded.</summary>
    public event Action? Changed;

    public DataStore(Settings settings, SpellDatabase spells)
    {
        Settings = settings;
        Spells = spells;
    }

    private static string LogCachePath => Path.Combine(Settings.AppDataDir, "logstate.json");

    private string? FolderFor(FileKind kind)
    {
        var f = kind switch
        {
            FileKind.Inventory => Settings.InventoryFolder,
            FileKind.Spellbook => Settings.SpellbookFolder,
            FileKind.Log => Settings.LogFolder,
            FileKind.Ini => string.IsNullOrWhiteSpace(Settings.IniFolder) ? Settings.InventoryFolder : Settings.IniFolder,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(f) && Directory.Exists(f) ? Path.GetFullPath(f) : null;
    }

    /// <summary>True when <paramref name="path"/> is a file of a known type that lives in that type's folder.</summary>
    private bool Wanted(string path)
    {
        var kind = Kind(path);
        var folder = FolderFor(kind);
        if (folder == null) return false;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var inFolder = Settings.IncludeSubfolders
            ? (dir + Path.DirectorySeparatorChar).StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : dir.Equals(folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (!inFolder) return false;
        var wantServer = Settings.LogServer?.Trim();
        if (kind == FileKind.Log && !string.IsNullOrEmpty(wantServer))
            return LogScanner.TryParseFileName(path, out _, out var server) &&
                   server.Equals(wantServer, StringComparison.OrdinalIgnoreCase);
        if (kind == FileKind.Ini)
            return CharacterIniParser.TryParseFileName(path, out _, out var iniServer) &&
                   (string.IsNullOrEmpty(wantServer) || iniServer.Equals(wantServer, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public Task StartAsync() => Task.Run(() =>
    {
        LoadLogCache();
        foreach (var f in Enumerate(FileKind.Inventory, "*-Inventory.txt")) LoadFile(f);
        foreach (var f in Enumerate(FileKind.Spellbook, "*-Spellbook.txt")) LoadFile(f);
        foreach (var f in Enumerate(FileKind.Log, "eqlog_*.txt")) LoadFile(f);
        foreach (var f in Enumerate(FileKind.Ini, "*_*.ini")) LoadFile(f);
        SaveLogCache();

        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        var folders = new[] { FileKind.Inventory, FileKind.Spellbook, FileKind.Log, FileKind.Ini }
            .Select(FolderFor).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var w = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = Settings.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Changed += OnFsEvent;
            w.Created += OnFsEvent;
            w.Renamed += (_, e) => { Remove(e.OldFullPath); Queue(e.FullPath); };
            w.Deleted += (_, e) => Remove(e.FullPath);
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }

        Changed?.Invoke();
    });

    private IEnumerable<string> Enumerate(FileKind kind, string pattern)
    {
        var folder = FolderFor(kind);
        if (folder == null) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = Settings.IncludeSubfolders, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
            }).Where(Wanted).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
        if (!Wanted(path)) return;
        lock (_lock) _pending.Add(path);
        // EQ writes /outputfile in several chunks; wait for it to settle.
        _debounce?.Change(750, Timeout.Infinite);
    }

    private void Flush()
    {
        string[] paths;
        lock (_lock) { paths = _pending.ToArray(); _pending.Clear(); }
        var any = false;
        foreach (var p in paths) any |= LoadFile(p);
        if (paths.Any(p => Kind(p) == FileKind.Log)) SaveLogCache();
        if (any) Changed?.Invoke();
    }

    private void Remove(string path)
    {
        lock (_lock)
        {
            _inventories.Remove(path);
            _spellbooks.Remove(path);
            _logs.Remove(path);
            _inis.Remove(path);
        }
        Changed?.Invoke();
    }

    private enum FileKind { Inventory, Spellbook, Log, Ini, Other }

    private static FileKind Kind(string path)
    {
        var n = Path.GetFileName(path);
        if (n.EndsWith("-Inventory.txt", StringComparison.OrdinalIgnoreCase)) return FileKind.Inventory;
        if (n.EndsWith("-Spellbook.txt", StringComparison.OrdinalIgnoreCase)) return FileKind.Spellbook;
        if (n.StartsWith("eqlog_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return FileKind.Log;
        if (CharacterIniParser.TryParseFileName(path, out _, out _)) return FileKind.Ini;
        return FileKind.Other;
    }

    private bool LoadFile(string path)
    {
        // Retry briefly: EQ may still hold the file while writing it.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return false;
                switch (Kind(path))
                {
                    case FileKind.Inventory:
                        var inv = InventoryParser.Parse(path);
                        lock (_lock) _inventories[path] = inv;
                        return true;
                    case FileKind.Spellbook:
                        var sb = SpellbookParser.Parse(path);
                        lock (_lock) _spellbooks[path] = (sb, File.GetLastWriteTime(path));
                        return true;
                    case FileKind.Log:
                        LogState? st;
                        lock (_lock)
                        {
                            if (!_logs.TryGetValue(path, out st) && _cache.TryGetValue(path, out st)) _logs[path] = st;
                        }
                        if (st == null)
                        {
                            if (!LogScanner.TryParseFileName(path, out var name, out var server)) return false;
                            st = new LogState { Path = path, Character = name, Server = server };
                        }
                        var changed = LogScanner.Update(st);
                        lock (_lock) _logs[path] = st;
                        return changed;
                    case FileKind.Ini:
                        CharacterIniParser.TryParseFileName(path, out var iniName, out _);
                        var friends = CharacterIniParser.ReadFriends(path);
                        lock (_lock) _inis[path] = (iniName, friends, File.GetLastWriteTime(path));
                        return true;
                    default:
                        return false;
                }
            }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    private void LoadLogCache()
    {
        try
        {
            if (!File.Exists(LogCachePath)) return;
            var list = JsonSerializer.Deserialize<List<LogState>>(File.ReadAllText(LogCachePath)) ?? new();
            // Keep every remembered log (even ones outside the current folder) so switching folders back is instant.
            _cache = list.Where(l => !string.IsNullOrEmpty(l.Path))
                         .GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch { _cache = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveLogCache()
    {
        try
        {
            List<LogState> list;
            lock (_lock)
            {
                foreach (var (p, s) in _logs) _cache[p] = s;
                list = _cache.Values.Where(l => File.Exists(l.Path)).ToList();
            }
            File.WriteAllText(LogCachePath, JsonSerializer.Serialize(list));
        }
        catch { /* not fatal */ }
    }

    /// <summary>Snapshot of all characters, merged from every source.</summary>
    public List<Character> GetCharacters()
    {
        lock (_lock)
        {
            var chars = new Dictionary<string, Character>(StringComparer.OrdinalIgnoreCase);
            Character Get(string name) => chars.TryGetValue(name, out var c) ? c : chars[name] = new Character { Name = name };

            foreach (var inv in _inventories.Values)
            {
                var c = Get(inv.Character);
                if (c.Inventory == null || inv.SnapshotTime > c.Inventory.SnapshotTime) c.Inventory = inv;
            }
            foreach (var (path, (spells, time)) in _spellbooks)
            {
                var c = Get(SpellbookParser.CharacterFromFile(path));
                if (c.SpellbookTime == null || time > c.SpellbookTime) { c.Spellbook = spells; c.SpellbookTime = time; }
            }
            foreach (var log in _logs.Values)
            {
                var c = Get(log.Character);
                if (c.Log == null || (log.LastLine ?? DateTime.MinValue) > (c.Log.LastLine ?? DateTime.MinValue)) c.Log = log;
            }
            // The EQ folder keeps .ini files for every character ever played; only attach them to characters we
            // already know from inventory/spellbook/log files so old characters don't clutter the list.
            foreach (var (character, friends, time) in _inis.Values)
            {
                if (!chars.TryGetValue(character, out var c)) continue;
                if (c.IniTime == null || time > c.IniTime) { c.IniFriends = friends; c.IniTime = time; }
            }
            foreach (var c in chars.Values)
            {
                c.Override = Settings.Overrides.TryGetValue(c.Name, out var o) ? o : null;
                if (c.Spellbook != null && c.Log?.Class == null && c.Override?.Class == null)
                    c.GuessedClass = Spells.GuessClass(c.Spellbook);
            }
            return chars.Values.OrderBy(c => c.Name).ToList();
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce?.Dispose();
        SaveLogCache();
    }
}
