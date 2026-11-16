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
