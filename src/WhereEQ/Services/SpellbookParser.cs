using System.IO;

namespace WhereEQ.Services;

public sealed record SpellEntry(int Level, string Name);

/// <summary>Parses /outputfile spellbook output and the bundled reference lists (both "level&lt;TAB&gt;name").</summary>
public static class SpellbookParser
{
    public static string CharacterFromFile(string path) => InventoryParser.CharacterFromFile(path);

    public static List<SpellEntry> Parse(string path)
    {
        var list = new List<SpellEntry>();
        foreach (var line in FileUtil.ReadLinesShared(path))
        {
            // "level<TAB>name" (spellbook) or "level<TAB>name<TAB>era" (reference lists); '#' lines are comments.
            var cols = line.Split('\t');
            if (cols.Length < 2 || !int.TryParse(cols[0].Trim(), out var level)) continue;
            var name = cols[1].Trim();
            if (name.Length > 0) list.Add(new SpellEntry(level, name));
        }
        return list;
    }
}
