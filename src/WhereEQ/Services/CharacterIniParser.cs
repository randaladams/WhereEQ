using System.IO;
using System.Text.RegularExpressions;

namespace WhereEQ.Services;

/// <summary>
/// Reads the per-character client settings file &lt;Name&gt;_&lt;server&gt;.ini (in the EverQuest folder).
/// Its [Friends] section is the character's friends list:
///   [Friends]
///   Friend0=Somefriend
///   Friend1=Another
/// UI_&lt;Name&gt;_&lt;server&gt;.ini (window layout) and eqclient.ini don't match the file name pattern.
/// </summary>
public static partial class CharacterIniParser
{
    [GeneratedRegex(@"^(?<name>[A-Za-z]+)_(?<server>[A-Za-z0-9]+)$")]
    private static partial Regex IniFileName();

    public static bool TryParseFileName(string path, out string character, out string server)
    {
        var m = IniFileName().Match(Path.GetFileNameWithoutExtension(path));
        character = m.Success ? m.Groups["name"].Value : "";
        server = m.Success ? m.Groups["server"].Value : "";
        return m.Success && Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The EQ client's friends list holds at most this many names.</summary>
    public const int MaxFriends = 100;

    /// <summary>
    /// Replaces the [Friends] section of <paramref name="path"/> with Friend0..FriendN = <paramref name="friends"/>,
    /// leaving every other section as it was. A copy of the original is kept as &lt;file&gt;.whereeq.bak.
    /// </summary>
    public static void WriteFriends(string path, IReadOnlyList<string> friends)
    {
        if (friends.Count > MaxFriends) throw new ArgumentException($"EverQuest allows at most {MaxFriends} friends.");
        var lines = File.ReadAllLines(path, System.Text.Encoding.Latin1).ToList();

        var start = lines.FindIndex(l => l.Trim().Equals("[Friends]", StringComparison.OrdinalIgnoreCase));
        var section = friends.Select((f, i) => $"Friend{i}={f}").ToList();
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[Friends]");
            lines.AddRange(section);
        }
        else
        {
            var end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            // Keep any blank line that separated [Friends] from the next section.
            var trailingBlank = end > start + 1 && lines[end - 1].Trim().Length == 0 && end < lines.Count;
            lines.RemoveRange(start + 1, end - start - 1);
            if (trailingBlank) section.Add("");
            lines.InsertRange(start + 1, section);
        }

        File.Copy(path, path + ".whereeq.bak", overwrite: true);
        var tmp = path + ".whereeq.tmp";
        File.WriteAllText(tmp, string.Join("\r\n", lines) + "\r\n", System.Text.Encoding.Latin1);
        File.Move(tmp, path, overwrite: true);
    }

    public static List<string> ReadFriends(string path)
    {
        var friends = new List<string>();
        var inFriends = false;
        foreach (var raw in FileUtil.ReadLinesShared(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line.StartsWith('['))
            {
                inFriends = line.Equals("[Friends]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inFriends) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var name = line[(eq + 1)..].Trim();
            if (name.Length > 0 && name.All(char.IsLetter) &&
                !friends.Contains(name, StringComparer.OrdinalIgnoreCase))
                friends.Add(name);
        }
        return friends;
    }
}
