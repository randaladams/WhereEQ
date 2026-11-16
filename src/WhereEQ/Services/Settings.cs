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
    public bool Follow { get; set; } = true;
    /// <summary>Click-through: mouse goes to the game underneath. Toggle with Ctrl+Alt+M.</summary>
    public bool Locked { get; set; }
    /// <summary>Character to track; null = whoever logged a /loc most recently.</summary>
    public string? Character { get; set; }
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
