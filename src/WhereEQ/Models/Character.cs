using WhereEQ.Services;

namespace WhereEQ.Models;

/// <summary>Everything known about one character, merged from inventory, spellbook, log and manual overrides.</summary>
public sealed class Character
{
    public required string Name { get; init; }
    public CharacterInventory? Inventory { get; set; }
    public List<SpellEntry>? Spellbook { get; set; }
    public DateTime? SpellbookTime { get; set; }
    public LogState? Log { get; set; }
    /// <summary>Friends list from Name_server.ini (authoritative when present).</summary>
    public List<string>? IniFriends { get; set; }
    public DateTime? IniTime { get; set; }

    /// <summary>This character's friends: the .ini list if we have it, otherwise what the log shows.</summary>
    public IEnumerable<string> FriendNames =>
        IniFriends ?? Log?.Friends.Select(f => f.Name) ?? Enumerable.Empty<string>();
    public string FriendsSource => IniFriends != null ? ".ini" : Log?.Friends.Count > 0 ? "log" : "";
    public CharacterOverride? Override { get; set; }
    /// <summary>Class guessed from the spellbook when nothing better is known.</summary>
    public string? GuessedClass { get; set; }

    public string? Class => Log?.Class ?? Override?.Class ?? GuessedClass;

    public string ClassSource =>
        Log?.Class != null ? "log (/who)" :
        Override?.Class != null ? "manual" :
        GuessedClass != null ? "spellbook guess" : "unknown";

    /// <summary>Level from the log, else manual, else the highest spell level in the book (a lower bound).</summary>
    public int? Level => Log?.Level ?? Override?.Level ?? SpellbookMaxLevel;

    public string LevelSource =>
        Log?.Level != null ? (Log.LevelSeen is { } t ? $"log, {t:MMM d}" : "log") :
        Override?.Level != null ? "manual" :
        SpellbookMaxLevel != null ? "spellbook (minimum)" : "unknown";

    public int? SpellbookMaxLevel => Spellbook is { Count: > 0 } b ? b.Max(s => s.Level) : null;

    public string? Zone => Log?.Zone;
    public string BindText => Log?.BindZone is { } z ? z : "";
    public string BindTip => Log?.BindZone == null ? "Not in the log yet. In game, /charinfo shows your bind point."
        : $"{(Log.BindHow == "/charinfo" ? "Reported by /charinfo" : "Bound while in this zone")}{(Log.BindSeen is { } t ? $" on {t:MMM d, h:mm tt}" : "")}";
    public DateTime? LastSeen => Log?.LastLine;

    public string ClassText => Class ?? "?";
    public string LevelText => Level?.ToString() ?? "?";
    public string BagsText => Inventory is { } i ? $"{i.BagSlotsFree} / {i.BagSlotsTotal}" : "";
    public string BankText => Inventory is { } i ? $"{i.BankSlotsFree} / {i.BankSlotsTotal}" : "";
    public string InventoryAge => Inventory is { } i ? Ago(i.SnapshotTime) : "no file";
    public string SpellbookAge => SpellbookTime is { } t ? Ago(t) : "no file";
    public string LastSeenText => LastSeen is { } t ? Ago(t) : "no log";

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 60) return $"{(int)d.TotalDays}d ago";
        return t.ToString("yyyy-MM-dd");
    }
}
