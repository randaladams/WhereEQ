using System.Text;

namespace WhereEQ.Services;

/// <summary>
/// Maps the zone names the log uses ("You have entered East Commonlands.") to the short names map files use
/// (ecommons.txt), for Classic through Velious as on Project 1999. Lookups ignore case, punctuation and a
/// leading "The".
/// </summary>
public static class ZoneNames
{
    private static readonly (string Long, string Short)[] Zones =
    {
        // Antonica
        ("Qeynos Hills", "qeytoqrg"), ("South Qeynos", "qeynos"), ("North Qeynos", "qeynos2"),
        ("Qeynos Aqueduct System", "qcat"), ("Qeynos Catacombs", "qcat"), ("Surefall Glade", "qrg"),
        ("Western Plains of Karana", "qey2hh1"), ("Northern Plains of Karana", "northkarana"),
        ("Eastern Plains of Karana", "eastkarana"), ("Southern Plains of Karana", "southkarana"),
        ("Blackburrow", "blackburrow"), ("Everfrost Peaks", "everfrost"), ("Everfrost", "everfrost"), ("Halas", "halas"),
        ("Permafrost Caverns", "permafrost"), ("Permafrost Keep", "permafrost"), ("Lake Rathetear", "lakerathe"),
        ("Rathe Mountains", "rathemtn"), ("Gorge of King Xorbb", "beholder"), ("Beholder's Maze", "beholder"),
        ("Highpass Hold", "highpass"), ("High Keep", "highkeep"), ("Kithicor Woods", "kithicor"), ("Kithicor Forest", "kithicor"),
        ("West Commonlands", "commons"), ("East Commonlands", "ecommons"), ("Nektulos Forest", "nektulos"),
        ("Neriak Foreign Quarter", "neriaka"), ("Neriak Commons", "neriakb"), ("Neriak Third Gate", "neriakc"),
        ("Lavastorm Mountains", "lavastorm"), ("Najena", "najena"), ("Solusek's Eye", "soldunga"),
        ("Nagafen's Lair", "soldungb"), ("Temple of Solusek Ro", "soltemple"),
        ("West Freeport", "freportw"), ("East Freeport", "freporte"), ("North Freeport", "freportn"),
        ("Befallen", "befallen"), ("Northern Desert of Ro", "nro"), ("North Ro", "nro"),
        ("Southern Desert of Ro", "sro"), ("South Ro", "sro"), ("Oasis of Marr", "oasis"), ("Ocean of Tears", "oot"),
        ("Innothule Swamp", "innothule"), ("Grobb", "grobb"), ("Guk", "guktop"), ("Upper Guk", "guktop"),
        ("Ruins of Old Guk", "gukbottom"), ("Lower Guk", "gukbottom"), ("Feerrott", "feerrott"),
        ("Lost Temple of Cazic-Thule", "cazicthule"), ("Temple of Cazic-Thule", "cazicthule"), ("Oggok", "oggok"),
        ("Rivervale", "rivervale"), ("Misty Thicket", "misty"), ("Runnyeye Citadel", "runnyeye"), ("Clan Runnyeye", "runnyeye"),
        ("Estate of Unrest", "unrest"), ("Infected Paw", "paw"), ("Splitpaw Lair", "paw"),
        ("Plane of Hate", "hateplane"), ("Plane of Fear", "fearplane"), ("Plane of Sky", "airplane"),
        ("Arena", "arena"), ("Erud's Crossing", "erudsxing"),
        // Faydwer
        ("Butcherblock Mountains", "butcher"), ("South Kaladim", "kaladima"), ("North Kaladim", "kaladimb"),
        ("Dagnor's Cauldron", "cauldron"), ("Steamfont Mountains", "steamfont"), ("Ak'Anon", "akanon"),
        ("Lesser Faydark", "lfaydark"), ("Greater Faydark", "gfaydark"), ("Northern Felwithe", "felwithea"),
        ("Southern Felwithe", "felwitheb"), ("Castle Mistmoore", "mistmoore"), ("Crushbone", "crushbone"),
        ("Kedge Keep", "kedge"),
        // Odus
        ("Toxxulia Forest", "tox"), ("Erudin", "erudnext"), ("Erudin Palace", "erudnint"), ("Paineel", "paineel"),
        ("Kerra Isle", "kerraridge"), ("Kerra Ridge", "kerraridge"), ("Hole", "hole"), ("Ruins of Old Paineel", "hole"),
        ("Warrens", "warrens"), ("Stonebrunt Mountains", "stonebrunt"),
        // Kunark
        ("Field of Bone", "fieldofbone"), ("Lake of Ill Omen", "lakeofillomen"), ("Firiona Vie", "firiona"),
        ("Frontier Mountains", "frontiermtns"), ("Overthere", "overthere"), ("East Cabilis", "cabeast"),
        ("Cabilis East", "cabeast"), ("West Cabilis", "cabwest"), ("Cabilis West", "cabwest"),
        ("Swamp of No Hope", "swampofnohope"), ("Warsliks Woods", "warslikswood"), ("Warslik's Woods", "warslikswood"),
        ("Trakanon's Teeth", "trakanon"), ("Emerald Jungle", "emeraldjungle"), ("Dreadlands", "dreadlands"),
        ("Burning Woods", "burningwood"), ("Burning Wood", "burningwood"), ("Skyfire Mountains", "skyfire"),
        ("Timorous Deep", "timorous"), ("Kaesora", "kaesora"), ("Karnor's Castle", "karnor"), ("Chardok", "chardok"),
        ("Ruins of Sebilis", "sebilis"), ("Old Sebilis", "sebilis"), ("City of Mist", "citymist"),
        ("Kurn's Tower", "kurn"), ("Mines of Nurga", "nurga"), ("Crypt of Dalnir", "dalnir"), ("Dalnir", "dalnir"),
        ("Temple of Droga", "droga"), ("Droga", "droga"), ("Howling Stones", "charasis"), ("Charasis", "charasis"),
        ("Veeshan's Peak", "veeshan"), ("Sebilis", "sebilis"),
        // Velious
        ("Iceclad Ocean", "iceclad"), ("Eastern Wastelands", "eastwastes"), ("Eastern Wastes", "eastwastes"),
        ("Great Divide", "greatdivide"), ("Wakening Lands", "wakening"), ("Wakening Land", "wakening"),
        ("Cobalt Scar", "cobaltscar"), ("Western Wastes", "westwastes"), ("City of Thurgadin", "thurgadina"),
        ("Thurgadin", "thurgadina"), ("Icewell Keep", "thurgadinb"), ("Kael Drakkel", "kael"),
        ("Skyshrine", "skyshrine"), ("Temple of Veeshan", "templeveeshan"), ("Dragon Necropolis", "necropolis"),
        ("Siren's Grotto", "sirens"), ("Crystal Caverns", "crystal"), ("Velketor's Labyrinth", "velketor"),
        ("Tower of Frozen Shadow", "frozenshadow"), ("Sleeper's Tomb", "sleeper"), ("Plane of Growth", "growthplane"),
        ("Plane of Mischief", "mischiefplane"),
    };

    private static readonly Dictionary<string, string> ByLong =
        Zones.GroupBy(z => Key(z.Long)).ToDictionary(g => g.Key, g => g.First().Short);

    private static readonly Dictionary<string, string> ByShort =
        Zones.GroupBy(z => z.Short).ToDictionary(g => g.Key, g => g.First().Long, StringComparer.OrdinalIgnoreCase);

    /// <summary>Short (map file) name for a long zone name, or null if unknown.</summary>
    public static string? ToShort(string? longName) =>
        longName != null && ByLong.TryGetValue(Key(longName), out var s) ? s : null;

    /// <summary>Long name for a short zone name ("cobaltscar" -> "Cobalt Scar"), or the input if unknown.</summary>
    public static string ToLong(string shortName) => ByShort.TryGetValue(shortName, out var l) ? l : shortName;

    /// <summary>"The Nektulos Forest" -> "nektulosforest" (lowercase letters/digits only, no leading "the").</summary>
    public static string Key(string name)
    {
        var s = name.Trim();
        if (s.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
