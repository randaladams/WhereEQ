using System.IO;
using WhereEQ.Models;

namespace WhereEQ.Services;

/// <summary>
/// Reference spell lists per class, loaded from Data\Spells\&lt;Class&gt;.txt next to the exe.
/// Each file is "level&lt;TAB&gt;spell name", the same format as /outputfile spellbook.
/// </summary>
public sealed class SpellDatabase
{
    private readonly Dictionary<string, List<SpellEntry>> _byClass = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; }
    public IReadOnlyCollection<string> Classes => _byClass.Keys;
    public bool IsEmpty => _byClass.Count == 0;

    public SpellDatabase(string folder)
    {
        Folder = folder;
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            var cls = EqClass.Normalize(Path.GetFileNameWithoutExtension(file).Replace('_', ' '));
            if (cls == null) continue;
            _byClass[cls] = SpellbookParser.Parse(file);
        }
    }

    public IReadOnlyList<SpellEntry> For(string cls) =>
        _byClass.TryGetValue(cls, out var l) ? l : Array.Empty<SpellEntry>();

    /// <summary>Guess a class from spellbook contents: the class whose list contains the most of these spells.</summary>
    public string? GuessClass(IEnumerable<SpellEntry> book)
    {
        var names = book.Select(s => FileUtil.NormalizeSpellName(s.Name)).ToHashSet();
        if (names.Count == 0) return null;
        var best = _byClass
            .Select(kv => (cls: kv.Key, hits: kv.Value.Count(s => names.Contains(FileUtil.NormalizeSpellName(s.Name)))))
            .OrderByDescending(x => x.hits)
            .FirstOrDefault();
        // Require most of the book to match so hybrids sharing a few spells don't win by accident.
        return best.hits >= Math.Max(3, names.Count / 2) ? best.cls : null;
    }
}
