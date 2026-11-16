namespace WhereEQ.Models;

/// <summary>Classic-era EverQuest classes and the /who titles used at levels 51, 55 and 60.</summary>
public static class EqClass
{
    public static readonly string[] All =
    {
        "Bard", "Cleric", "Druid", "Enchanter", "Magician", "Monk", "Necromancer",
        "Paladin", "Ranger", "Rogue", "Shadow Knight", "Shaman", "Warrior", "Wizard",
    };

    private static readonly Dictionary<string, string> TitleToClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Minstrel"] = "Bard", ["Troubadour"] = "Bard", ["Virtuoso"] = "Bard",
        ["Vicar"] = "Cleric", ["Templar"] = "Cleric", ["High Priest"] = "Cleric",
        ["Wanderer"] = "Druid", ["Preserver"] = "Druid", ["Hierophant"] = "Druid",
        ["Illusionist"] = "Enchanter", ["Beguiler"] = "Enchanter", ["Phantasmist"] = "Enchanter",
        ["Elementalist"] = "Magician", ["Conjurer"] = "Magician", ["Arch Mage"] = "Magician",
        ["Disciple"] = "Monk", ["Master"] = "Monk", ["Grandmaster"] = "Monk",
        ["Heretic"] = "Necromancer", ["Defiler"] = "Necromancer", ["Warlock"] = "Necromancer",
        ["Cavalier"] = "Paladin", ["Knight"] = "Paladin", ["Crusader"] = "Paladin",
        ["Pathfinder"] = "Ranger", ["Outrider"] = "Ranger", ["Warder"] = "Ranger",
        ["Rake"] = "Rogue", ["Blackguard"] = "Rogue", ["Assassin"] = "Rogue",
        ["Reaver"] = "Shadow Knight", ["Revenant"] = "Shadow Knight", ["Grave Lord"] = "Shadow Knight",
        ["Mystic"] = "Shaman", ["Luminary"] = "Shaman", ["Oracle"] = "Shaman",
        ["Champion"] = "Warrior", ["Myrmidon"] = "Warrior", ["Warlord"] = "Warrior",
        ["Channeler"] = "Wizard", ["Evoker"] = "Wizard", ["Sorcerer"] = "Wizard",
    };

    /// <summary>Maps a /who class or title ("Rake", "Shadow Knight", "Shadowknight") to a class name.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (TitleToClass.TryGetValue(text, out var c)) return c;
        if (text.Equals("Shadowknight", StringComparison.OrdinalIgnoreCase)) return "Shadow Knight";
        return All.FirstOrDefault(a => a.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CastsSpells(string cls) => cls is not ("Warrior" or "Monk" or "Rogue");
}
