using System.IO;

namespace WhereEQ.Services;

/// <summary>Where a respawn time came from, shown in the Timers window.</summary>
public enum SpawnSource { None, Yours, Npc, Zone, Guess }

/// <summary>
/// P1999 respawn times from Data/SpawnTimes/spawntimes.txt (generated from EqTool, MIT; see NOTICE.md there).
/// Lookup order: your own override, exact NPC name, NPC name contains, zone default, else 6:40 (the most
/// common P1999 timer) marked as a guess.
/// </summary>
public sealed class SpawnTimes
{
    public static readonly TimeSpan CommonDefault = new(0, 6, 40);

    private sealed class ZoneTimes
    {
        public TimeSpan? Default;
        public readonly Dictionary<string, TimeSpan> Exact = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Text, TimeSpan Time)> Contains = new();
    }

    private readonly Dictionary<string, ZoneTimes> _zones = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _zones.Count == 0;

    public static SpawnTimes LoadBundled() =>
        Load(Path.Combine(AppContext.BaseDirectory, "Data", "SpawnTimes", "spawntimes.txt"));

    public static SpawnTimes Load(string path)
    {
        var st = new SpawnTimes();
        if (!File.Exists(path)) return st;
        foreach (var raw in File.ReadLines(path))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            var parts = raw.Split('\t');
            if (parts.Length < 3 || !TimeSpan.TryParse(parts[2], out var t)) continue;
            if (!st._zones.TryGetValue(parts[0], out var z)) st._zones[parts[0]] = z = new ZoneTimes();
            var key = parts[1];
            if (key == "*") z.Default = t;
            else if (key.StartsWith('=')) z.Exact[key[1..]] = t;
            else if (key.StartsWith('~')) z.Contains.Add((key[1..], t));
        }
        return st;
    }

    /// <summary>Override key for a mob in a zone (settings).</summary>
    public static string OverrideKey(string? zoneShort, string mob) => $"{zoneShort ?? "?"}|{mob}".ToLowerInvariant();

    /// <summary>Respawn time for <paramref name="mob"/> in <paramref name="zoneShort"/>. Time is null when the
    /// zone is known not to respawn (e.g. the arena).</summary>
    public (TimeSpan? Time, SpawnSource Source) Get(string? zoneShort, string mob, IReadOnlyDictionary<string, int>? overrides)
    {
        if (overrides != null && overrides.TryGetValue(OverrideKey(zoneShort, mob), out var secs) && secs > 0)
            return (TimeSpan.FromSeconds(secs), SpawnSource.Yours);
        if (zoneShort == null || !_zones.TryGetValue(zoneShort, out var z))
            return (CommonDefault, SpawnSource.Guess);
        if (z.Exact.TryGetValue(mob, out var t)) return (t, SpawnSource.Npc);
        foreach (var (text, time) in z.Contains)
            if (mob.Contains(text, StringComparison.OrdinalIgnoreCase)) return (time, SpawnSource.Npc);
        if (z.Default is { } d) return d > TimeSpan.Zero ? (d, SpawnSource.Zone) : (null, SpawnSource.None);
        return (CommonDefault, SpawnSource.Guess);
    }
}
