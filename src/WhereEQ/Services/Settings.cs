using System.IO;
using System.Text.Json;

namespace WhereEQ.Services;

public sealed class CharacterOverride
{
    public string? Class { get; set; }
    public int? Level { get; set; }
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

    public bool HasAnyFolder =>
        !string.IsNullOrWhiteSpace(InventoryFolder) || !string.IsNullOrWhiteSpace(SpellbookFolder) || !string.IsNullOrWhiteSpace(LogFolder);

    public Settings Clone() => (Settings)MemberwiseClone();
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
