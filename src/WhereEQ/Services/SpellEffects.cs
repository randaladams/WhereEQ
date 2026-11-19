using System.IO;
using System.Text;

namespace WhereEQ.Services;

/// <summary>One spell from the client's spells_us.txt, with what the buff tracker needs.</summary>
public sealed class SpellInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"Your skin turns hard as steel." (the line you see when it lands on you).</summary>
    public string CastOnYou { get; init; } = "";
    /// <summary>"'s skin turns hard as steel." (appended to the target's name).</summary>
    public string CastOnOther { get; init; } = "";
    /// <summary>"Your skin returns to normal." (the line you see when it wears off you).</summary>
    public string Fades { get; init; } = "";
    public int CastTimeMs { get; init; }
    public int DurationFormula { get; init; }
    public int DurationTicks { get; init; }
    public bool Beneficial { get; init; }
    /// <summary>Minimum level by class name ("Shadow Knight"), only classes that get the spell (1..60).</summary>
    public Dictionary<string, int> Levels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int MinLevel => Levels.Count > 0 ? Levels.Values.Min() : 0;

    /// <summary>A weapon/item effect (e.g. Boiling Blood from Nadrandi's Lance): instant, and no class gets it.</summary>
    public bool IsProc => CastTimeMs == 0 && Levels.Count == 0;

    /// <summary>"Selo's Accelerando" (the file uses backticks); item effects like "JourneymanBoots" get spaces.</summary>
    public static string DisplayName(string name)
    {
        name = name.Replace('`', '\'');
        if (name.Contains(' ') || name.Length < 4) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>Buff length in seconds for a caster of <paramref name="casterLevel"/> (EQ duration formulas, as in
    /// EqTool's SpellDurations). 0 = not a buff.</summary>
    public int DurationSeconds(int casterLevel)
    {
        var level = Math.Clamp(casterLevel, 1, 60);
        var d = DurationTicks;
        int ticks = DurationFormula switch
        {
            0 => 0,
            1 or 6 => Math.Min((int)Math.Ceiling(level / 2.0), d),
            2 => Math.Min((int)Math.Ceiling(level / 5.0 * 3), d),
            3 => Math.Min(level * 30, d),
            4 => d == 0 ? 50 : d,
            5 => d == 0 ? 3 : d,
            7 => Math.Min(level, d),
            8 => Math.Min(level + 10, d),
            9 => Math.Min(level * 2 + 10, d),
            10 => Math.Min(level * 3 + 10, d),
            11 or 12 or 15 => d,
            50 => 72000,
            3600 => d == 0 ? 3600 : d,
            _ => d,
        };
        return ticks * 6;
    }
}

/// <summary>
/// The client's spells_us.txt (in the EverQuest folder), indexed by the messages the log shows: what you see when a
/// spell lands on you, on someone else, and when it wears off. Field layout as read by EqTool (MIT).
/// </summary>
public sealed class SpellEffects
{
    public static readonly SpellEffects Empty = new();

    private readonly Dictionary<string, List<SpellInfo>> _onYou = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SpellInfo>> _fades = new(StringComparer.Ordinal);
    /// <summary>"Lands on someone" text of weapon/item proc effects (no cast time, no class gets them).</summary>
    private readonly Dictionary<string, List<SpellInfo>> _procOnOther = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpellInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    public string? Path { get; private set; }
    public bool IsEmpty => _byName.Count == 0;
    public int Count => _byName.Count;

    // spells_us.txt class level columns, same order as EqClass.All (Warrior .. Enchanter).
    private const int FirstClassField = 104;
    private static readonly string[] ClassOrder =
    {
        "Warrior", "Cleric", "Paladin", "Ranger", "Shadow Knight", "Druid", "Monk", "Bard", "Rogue", "Shaman",
        "Necromancer", "Wizard", "Magician", "Enchanter",
    };

    /// <summary>Looks for spells_us.txt in the given folders (and their parents). Returns <see cref="Empty"/> if none.</summary>
    public static SpellEffects Find(params string?[] folders)
    {
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            for (var dir = new DirectoryInfo(f); dir != null; dir = dir.Parent)
            {
                var p = System.IO.Path.Combine(dir.FullName, "spells_us.txt");
                if (File.Exists(p))
                {
                    try { return Load(p); } catch { return Empty; }
                }
                if (dir.Parent == null || dir.FullName.Length <= 3) break;
            }
        }
        return Empty;
    }

    public static SpellEffects Load(string path)
    {
        var fx = new SpellEffects { Path = path };
        foreach (var line in File.ReadLines(path, Encoding.Latin1))
        {
            var s = Parse(line);
            if (s == null) continue;
            // Several rows can share a name. As in EqTool, the later row wins (P1999's own versions come last, e.g.
            // Banshee Aura 364 vs 8572), unless it has no classes and the earlier one does (NPC/item copies).
            if (fx._byName.TryGetValue(s.Name, out var old) && old.Levels.Count > 0 && s.Levels.Count == 0) continue;
            fx._byName[s.Name] = s;
        }
        foreach (var s in fx._byName.Values)
        {
            if (s.CastOnYou.Length > 0) Add(fx._onYou, s.CastOnYou, s);
            if (s.Fades.Length > 0) Add(fx._fades, s.Fades, s);
            if (s.IsProc && s.CastOnOther.Length > 0) Add(fx._procOnOther, s.CastOnOther, s);
        }
        return fx;
    }

    private static void Add(Dictionary<string, List<SpellInfo>> d, string key, SpellInfo s)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = new();
        list.Add(s);
    }

    private static SpellInfo? Parse(string line)
    {
        var f = line.Split('^');
        if (f.Length < FirstClassField + ClassOrder.Length) return null;
        if (!int.TryParse(f[0], out var id) || !int.TryParse(f[16], out var formula) || !int.TryParse(f[17], out var ticks))
            return null;
        var name = f[1].Trim();
        if (name.Length == 0 || (formula == 0 && ticks == 0)) return null; // instant spells: nothing to time
        if (name.StartsWith("GM ") || name.StartsWith("NPC") || name.Contains("test", StringComparison.OrdinalIgnoreCase))
            return null;
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ClassOrder.Length; i++)
            if (int.TryParse(f[FirstClassField + i], out var l) && l is > 0 and <= 60) levels[ClassOrder[i]] = l;
        _ = int.TryParse(f[13], out var castTime);
        _ = int.TryParse(f[83], out var benefit);
        return new SpellInfo
        {
            Id = id, Name = name, CastOnYou = f[6].Trim(), CastOnOther = f[7].Trim(), Fades = f[8].Trim(),
            CastTimeMs = castTime, DurationFormula = formula, DurationTicks = ticks, Beneficial = benefit != 0,
            Levels = levels,
        };
    }

    public SpellInfo? ByName(string name) => _byName.GetValueOrDefault(name.Replace('\'', '`')) ?? _byName.GetValueOrDefault(name);

    /// <summary>Spells whose "lands on you" text is <paramref name="msg"/>. Some lines end in ".." in the log.</summary>
    public List<SpellInfo>? LandsOnYou(string msg) =>
        _onYou.GetValueOrDefault(msg) ?? (msg.EndsWith("..") ? _onYou.GetValueOrDefault(msg[..^1]) : null);

    /// <summary>Weapon/item proc effects whose "lands on someone" text is <paramref name="rest"/> (the line after the
    /// target's name, e.g. "'s blood boils.").</summary>
    public List<SpellInfo>? ProcLandsOnOther(string rest) => _procOnOther.GetValueOrDefault(rest);

    /// <summary>Spells whose "wears off you" text is <paramref name="msg"/>.</summary>
    public List<SpellInfo>? FadesFromYou(string msg) => _fades.GetValueOrDefault(msg);

    /// <summary>
    /// Picks one spell for an ambiguous message (e.g. "You feel replenished." is two Bard songs): the one you're
    /// casting, else one your class gets at or below your level (highest such level), else the one whose level is
    /// closest to yours (as EqTool does).
    /// </summary>
    public static SpellInfo Pick(List<SpellInfo> candidates, SpellInfo? casting, string? cls, int? level)
    {
        if (casting != null && candidates.Contains(casting)) return casting;
        if (candidates.Count == 1) return candidates[0];
        var lvl = level ?? 50;
        if (cls != null)
        {
            var mine = candidates.Where(c => c.Levels.TryGetValue(cls, out var l) && l <= lvl)
                                 .OrderByDescending(c => c.Levels[cls]).FirstOrDefault();
            if (mine != null) return mine;
        }
        return candidates.Where(c => c.Levels.Count > 0)
                         .OrderBy(c => c.Levels.Values.Min(l => Math.Abs(l - lvl)))
                         .FirstOrDefault() ?? candidates[0];
    }
}
