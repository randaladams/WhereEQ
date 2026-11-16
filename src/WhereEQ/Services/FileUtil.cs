using System.IO;
using System.Text;

namespace WhereEQ.Services;

public static class FileUtil
{
    /// <summary>Reads a file that EverQuest may still have open for writing.</summary>
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.Latin1);
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }

    /// <summary>Spell names appear with ` or ' depending on source; compare them loosely.</summary>
    public static string NormalizeSpellName(string name) =>
        name.Trim().Replace('`', '\'').Replace('’', '\'').ToLowerInvariant();
}
