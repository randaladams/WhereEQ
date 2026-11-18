using System.IO;
using System.Text.Json;

namespace WhereEQ.Services;

public sealed class CharacterOverride
{
    public string? Class { get; set; }
    public int? Level { get; set; }
}

public sealed class MapOverlaySettings
{
    public bool Open { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 420;
    /// <summary>Darkness of the window backdrop, 0 (fully see-through) to 1.</summary>
    public double Backdrop { get; set; } = 0.35;
    /// <summary>Opacity of the map drawing itself.</summary>
    public double MapOpacity { get; set; } = 0.9;
    /// <summary>"Bright" (lightened map colors), "Original", or "White".</summary>
    public string LineStyle { get; set; } = "Bright";
    public bool ShowLabels { get; set; } = true;
    /// <summary>Draw the trail of recent /loc points.</summary>
    public bool ShowTrail { get; set; } = true;
    /// <summary>"Clear trail": trail points before this time are not drawn.</summary>
    public DateTime? TrailClearedAt { get; set; }
    public bool Follow { get; set; } = true;
    /// <summary>Click-through: mouse goes to the game underneath. Toggle with Ctrl+Alt+M.</summary>
    public bool Locked { get; set; }
    /// <summary>Character to track; null = whoever logged a /loc most recently.</summary>
    public string? Character { get; set; }
}

/// <summary>Which log events beep (Settings → Sounds).</summary>
public sealed class SoundSettings
{
    // The first three (0.1.21) were plain flags; kept so older settings files keep their choice.
    public bool Fall { get; set; } = true;
    public bool Stand { get; set; } = true;
    public bool Tell { get; set; } = true;
    /// <summary>The other alerts, by <see cref="LogNotice"/> name. Missing = the alert's default.</summary>
    public Dictionary<string, bool> On { get; set; } = new();

    public bool IsOn(LogNotice n) => n switch
    {
        LogNotice.Fall => Fall,
        LogNotice.Stand => Stand,
        LogNotice.Tell => Tell,
        _ => On.TryGetValue(n.ToString(), out var v) ? v : Sounds.Alerts.FirstOrDefault(a => a.Notice == n)?.DefaultOn ?? false,
    };

    public void Set(LogNotice n, bool on)
    {
        switch (n)
        {
            case LogNotice.Fall: Fall = on; break;
            case LogNotice.Stand: Stand = on; break;
            case LogNotice.Tell: Tell = on; break;
            default: On[n.ToString()] = on; break;
        }
    }
}

/// <summary>A timer started by hand in the Timers window (or "restart now" on a kill).</summary>
public sealed class ManualTimer
{
    public string Name { get; set; } = "";
    /// <summary>Map short name of the zone, if known.</summary>
    public string? Zone { get; set; }
    public DateTime Start { get; set; }
    public int Seconds { get; set; }
}

public sealed class TimersSettings
{
    public bool Open { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 200;
    /// <summary>Darkness of the window backdrop, 0 (fully see-through) to 1.</summary>
    public double Backdrop { get; set; } = 0.35;
    /// <summary>Click-through: mouse goes to the game underneath. Toggle with Ctrl+Alt+T.</summary>
    public bool Locked { get; set; }
    /// <summary>Details button: show zone, kill time, killer and timer length (wider window).</summary>
    public bool Details { get; set; }
    /// <summary>Also time mobs killed by other players and pets ("X has been slain by Y!").</summary>
    public bool IncludeOthers { get; set; }
    /// <summary>How long a timer stays in the list after the mob is up again.</summary>
    public int KeepUpMinutes { get; set; } = 5;
    /// <summary>"Clear": kills before this time are not shown.</summary>
    public DateTime? ClearedBefore { get; set; }
    /// <summary>Kills removed one by one (TimersWindow row keys).</summary>
    public List<string> Dismissed { get; set; } = new();
    public List<ManualTimer> Manual { get; set; } = new();
    /// <summary>Timers (row keys) with the 🔔 alert on.</summary>
    public List<string> Alerts { get; set; } = new();
    /// <summary>Your own respawn times in seconds, keyed by SpawnTimes.OverrideKey ("zone|mob", lower case).</summary>
    public Dictionary<string, int> Overrides { get; set; } = new();
}

/// <summary>User settings, stored in %LocalAppData%\WhereEQ\settings.json.</summary>
public sealed class Settings
{
    /// <summary>Legacy single folder (first versions). Migrated into the three folders below on load.</summary>
    public string? DataFolder { get; set; }

    /// <summary>Where *-Inventory.txt files are (/outputfile inventory writes to the EQ install folder).</summary>
    public string? InventoryFolder { get; set; }
    /// <summary>Where *-Spellbook.txt files are.</summary>
    public string? SpellbookFolder { get; set; }
    /// <summary>Where eqlog_Name_server.txt files are (usually the EQ "Logs" folder).</summary>
    public string? LogFolder { get; set; }
    /// <summary>Where Name_server.ini files are (EverQuest folder; holds each character's friends list).
    /// Blank = same as <see cref="InventoryFolder"/>.</summary>
    public string? IniFolder { get; set; }
    /// <summary>Also look in subfolders of the folders above.</summary>
    public bool IncludeSubfolders { get; set; } = true;
    /// <summary>Only read logs for this server (the part after the name in eqlog_Name_SERVER.txt). Blank = all.</summary>
    public string? LogServer { get; set; }

    public bool AlwaysOnTop { get; set; }

    /// <summary>EverQuest map files (&lt;P1999 folder&gt;\maps). Blank = "maps" under the inventory folder.</summary>
    public string? MapsFolder { get; set; }

    /// <summary>Map overlay window state.</summary>
    public MapOverlaySettings Map { get; set; } = new();

    /// <summary>Spawn timers window state and options.</summary>
    public TimersSettings Timers { get; set; } = new();

    /// <summary>Sounds for log events.</summary>
    public SoundSettings Sounds { get; set; } = new();

    /// <summary>Usable bank slots (P1999: 8). Higher-numbered slots in the inventory file are ignored.</summary>
    public int BankSlots { get; set; } = InventoryParser.DefaultBankSlots;

    public bool HasAnyFolder =>
        !string.IsNullOrWhiteSpace(InventoryFolder) || !string.IsNullOrWhiteSpace(SpellbookFolder) || !string.IsNullOrWhiteSpace(LogFolder);

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>The maps folder to use: the setting, else "maps" next to the inventory files (the P1999 folder).</summary>
    public string? EffectiveMapsFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MapsFolder)) return MapsFolder;
            if (string.IsNullOrWhiteSpace(InventoryFolder)) return null;
            var guess = Path.Combine(InventoryFolder, "maps");
            return Directory.Exists(guess) ? guess : null;
        }
    }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>Characters hidden from every tab (their files are left alone).</summary>
    public List<string> HiddenCharacters { get; set; } = new();

    /// <summary>Manual class/level for characters without a log (keyed by character name).</summary>
    public Dictionary<string, CharacterOverride> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhereEQ");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(AppDataDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {
                    s.Overrides = new(s.Overrides ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.HiddenCharacters ??= new();
                    s.Map ??= new();
                    s.Timers ??= new();
                    s.Sounds ??= new();
                    s.Sounds.On ??= new();
                    s.Timers.Dismissed ??= new();
                    s.Timers.Manual ??= new();
                    s.Timers.Alerts ??= new();
                    s.Timers.Overrides = new(s.Timers.Overrides ?? new(), StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(s.DataFolder) && !s.HasAnyFolder)
                    {
                        s.InventoryFolder = s.SpellbookFolder = s.LogFolder = s.DataFolder;
                    }
                    s.DataFolder = null;
                    return s;
                }
            }
        }
        catch { /* corrupt settings: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch { /* not fatal */ }
    }
}
