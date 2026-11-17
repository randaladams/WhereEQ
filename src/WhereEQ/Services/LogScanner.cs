using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WhereEQ.Services;

/// <summary>What we know about a character from their eqlog file. Persisted so big logs are read only once.</summary>
public sealed class FriendInfo
{
    public string Name { get; set; } = "";
    public DateTime? Added { get; set; }
    /// <summary>Last time this friend showed up in a /friends (or /who) listing.</summary>
    public DateTime? LastOnline { get; set; }
    public int? Level { get; set; }
    public string? Class { get; set; }
    public string? Race { get; set; }
    public string? Guild { get; set; }
    public string? Zone { get; set; }
}

public sealed record TrailPoint(double X, double Y, DateTime T);

/// <summary>A mob death seen in the log. <see cref="By"/> is null for "You have slain X!", else the killer
/// from "X has been slain by Killer!" (another player, a pet, ...). <see cref="Zone"/> is the log's zone name.</summary>
public sealed record KillRecord(string Name, string? Zone, DateTime Time, string? By);

public sealed class LogState
{
    /// <summary>Bumped when the scanner learns new things, so cached logs are re-read once from the start.</summary>
    public const int CurrentVersion = 5;
    public int Version { get; set; }

    public string Path { get; set; } = "";
    public string Character { get; set; } = "";
    public string Server { get; set; } = "";
    public long Offset { get; set; }
    public int? Level { get; set; }
    public DateTime? LevelSeen { get; set; }
    public string? Class { get; set; }
    public string? Zone { get; set; }
    public DateTime? LastLine { get; set; }

    /// <summary>Last /loc ("Your Location is Y, X, Z"), in map coordinates (see MapData.FromLoc), and its zone.</summary>
    public double? MapX { get; set; }
    public double? MapY { get; set; }
    public double? LocZ { get; set; }
    public DateTime? LocSeen { get; set; }
    public string? LocZone { get; set; }
    /// <summary>From "You think you are heading NorthEast." (sense heading).</summary>
    public string? Heading { get; set; }
    public DateTime? HeadingSeen { get; set; }

    /// <summary>Recent /loc points in the current zone (map coordinates), oldest first. Saved with the log state
    /// so the trail survives a restart.</summary>
    public List<TrailPoint> Trail { get; set; } = new();
    public const int TrailMax = 300;

    /// <summary>Recent mob deaths (newest last), for spawn timers.</summary>
    public List<KillRecord> Kills { get; set; } = new();
    public const int KillsMax = 300;

    /// <summary>Current guild (null = not in a guild, when <see cref="GuildSeen"/> is set).</summary>
    public string? Guild { get; set; }
    /// <summary>When the guild was last confirmed (from /who of yourself or a join/leave message).</summary>
    public DateTime? GuildSeen { get; set; }

    public string? BindZone { get; set; }
    public DateTime? BindSeen { get; set; }
    /// <summary>"/charinfo" when /char reported the zone ("You are currently bound in: X"), "bound here" when
    /// inferred from the current zone. (Value kept as "/charinfo" for compatibility with cached state.)</summary>
    public string? BindHow { get; set; }

    public List<FriendInfo> Friends { get; set; } = new();
    /// <summary>True while reading the lines of a /friends listing.</summary>
    public bool InFriendsList { get; set; }

    public FriendInfo Friend(string name)
    {
        var f = Friends.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f == null) Friends.Add(f = new FriendInfo { Name = name });
        return f;
    }
}

/// <summary>
/// Incrementally reads eqlog_&lt;Name&gt;_&lt;server&gt;.txt from the last offset and extracts level, class and zone.
/// Sources (later lines win):
///   [Tue Nov 10 15:16:42 2026] [51 Rake] Yourname (Halfling)            (/who of yourself)
///   You have gained a level! Welcome to level 51!
///   You have lost a level ... level 50
///   You have entered East Commonlands.
/// </summary>
public static partial class LogScanner
{
    [GeneratedRegex(@"^eqlog_(?<name>[^_]+)_(?<server>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LogFileName();

    [GeneratedRegex(@"^\[(?<ts>[^\]]+)\] (?<msg>.*)$")]
    private static partial Regex Line();

    // [53 Wanderer] Somefriend (Wood Elf) <Some Guild>  ZONE: cobaltscar
    // [ANONYMOUS] Someone  <Some Guild>
    [GeneratedRegex(@"^(?:AFK |<LINKDEAD>)?\s*\[(?:(?<lvl>\d+) (?<cls>[A-Za-z ]+?)|ANONYMOUS)\] (?<name>\w+)(?: \((?<race>[A-Za-z ]+)\))?(?:\s+<(?<guild>[^>]+)>)?(?:\s+ZONE: (?<zone>\S+))?")]
    private static partial Regex WhoLine();

    // "You have joined Some Guild." / "You are now a member of Some Guild." / "You have been added to Some Guild."
    [GeneratedRegex(@"^You (?:have joined|are now a member of|have been added to)(?: the guild)? (?<guild>[^.!]+?)[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex GuildJoined();

    // "You have left the guild." / "You have been removed from Some Guild." / "...kicked from..."
    [GeneratedRegex(@"^You (?:have left|have been removed from|have been kicked from|are no longer a member of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GuildLeft();

    [GeneratedRegex(@"^You are currently bound in: (?<zone>.+?)\.?$")]
    private static partial Regex BoundIn();

    [GeneratedRegex(@"^(?<name>\w+) is (?<no>no longer|now) your friend\.$")]
    private static partial Regex FriendChange();

    [GeneratedRegex(@"^You have gained a level! Welcome to level (?<lvl>\d+)!")]
    private static partial Regex LevelUp();

    [GeneratedRegex(@"^You (?:have )?lost a level.*?level (?<lvl>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LevelDown();

    [GeneratedRegex(@"^You have entered (?<zone>.+)\.$")]
    private static partial Regex ZoneLine();

    public static bool TryParseFileName(string path, out string character, out string server)
    {
        var m = LogFileName().Match(System.IO.Path.GetFileNameWithoutExtension(path));
        character = m.Success ? m.Groups["name"].Value : "";
        server = m.Success ? m.Groups["server"].Value : "";
        return m.Success;
    }

    /// <summary>Reads any new lines since <paramref name="state"/>.Offset. Returns true if anything changed.</summary>
    public static bool Update(LogState state)
    {
        var info = new FileInfo(state.Path);
        if (!info.Exists) return false;
        if (state.Version != LogState.CurrentVersion)
        {
            // Parser learned new things: re-read from the start, keeping nothing stale.
            state.Version = LogState.CurrentVersion;
            state.Offset = 0;
            state.Friends.Clear();
            state.BindZone = null; state.BindSeen = null; state.BindHow = null;
            state.Guild = null; state.GuildSeen = null;
            state.MapX = state.MapY = state.LocZ = null; state.LocSeen = null; state.LocZone = null;
            state.Heading = null; state.HeadingSeen = null; state.Trail.Clear(); state.Kills.Clear();
            state.InFriendsList = false;
        }
        if (info.Length < state.Offset) state.Offset = 0; // log was rotated or truncated
        if (info.Length == state.Offset) return false;

        using var fs = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(state.Offset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.Latin1, false, 1 << 16);

        long consumed = state.Offset;
        string? line;
        var name = state.Character;
        while ((line = sr.ReadLine()) != null)
        {
            // Latin1 is 1 byte per char; +2 for CRLF (EQ always writes CRLF). Only commit complete lines.
            if (sr.EndOfStream && !EndsWithNewline(fs)) break;
            consumed += line.Length + 2;

            var m = Line().Match(line);
            if (!m.Success) continue;
            var msg = m.Groups["msg"].Value;
            var ts = ParseTimestamp(m.Groups["ts"].Value);
            if (ts != null) state.LastLine = ts;

            // /friends output: header, dashes, who-style lines, then "There is/are ..." ends it.
            if (state.InFriendsList && (msg.StartsWith("There ") || msg.StartsWith("Your Location") || msg.StartsWith("You ")))
                state.InFriendsList = false;

            // /loc is by far the most common line when a /loc macro or mapping tool is running: handle it first.
            if (msg.StartsWith("Your Location is "))
            {
                if (TryParseLoc(msg.AsSpan("Your Location is ".Length), out var ly, out var lx, out var lz))
                {
                    var p = MapData.FromLoc(ly, lx);
                    state.MapX = p.X; state.MapY = p.Y; state.LocZ = lz;
                    state.LocSeen = ts; state.LocZone = state.Zone;
                    state.Trail.Add(new TrailPoint(p.X, p.Y, ts ?? DateTime.Now));
                    if (state.Trail.Count > LogState.TrailMax) state.Trail.RemoveRange(0, state.Trail.Count - LogState.TrailMax);
                }
                continue;
            }

            // Mob deaths (spawn timers). Before the other checks: a mob's name can contain words like "guild".
            if (msg.EndsWith('!') && TryParseKill(msg, name, out var victim, out var killer))
            {
                state.Kills.Add(new KillRecord(victim, state.Zone, ts ?? DateTime.Now, killer));
                if (state.Kills.Count > LogState.KillsMax) state.Kills.RemoveRange(0, state.Kills.Count - LogState.KillsMax);
                continue;
            }

            // Cheap prefix checks before regexes; this loop runs over hundreds of thousands of lines.
            if (msg.StartsWith('[') || msg.StartsWith("AFK [") || msg.StartsWith("<LINKDEAD>"))
            {
                var w = WhoLine().Match(msg);
                if (w.Success)
                {
                    var who = w.Groups["name"].Value;
                    var hasLevel = w.Groups["lvl"].Success;
                    if (who.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        // /who always shows the guild tag (even when anonymous), so no tag means no guild.
                        state.Guild = w.Groups["guild"].Success ? w.Groups["guild"].Value.Trim() : null;
                        state.GuildSeen = ts;
                        if (hasLevel)
                        {
                            state.Level = int.Parse(w.Groups["lvl"].Value);
                            state.LevelSeen = ts;
                            state.Class = Models.EqClass.Normalize(w.Groups["cls"].Value) ?? state.Class;
                        }
                    }
                    else if (state.InFriendsList || state.Friends.Any(f => f.Name.Equals(who, StringComparison.OrdinalIgnoreCase)))
                    {
                        // A friend seen online (in /friends, or in any /who once we know they're a friend).
                        var f = state.Friend(who);
                        f.LastOnline = ts;
                        if (hasLevel)
                        {
                            f.Level = int.Parse(w.Groups["lvl"].Value);
                            f.Class = Models.EqClass.Normalize(w.Groups["cls"].Value) ?? f.Class;
                        }
                        if (w.Groups["race"].Success) f.Race = w.Groups["race"].Value;
                        if (w.Groups["guild"].Success) f.Guild = w.Groups["guild"].Value;
                        f.Zone = w.Groups["zone"].Success ? w.Groups["zone"].Value : (state.InFriendsList ? null : state.Zone);
                    }
                }
            }
            else if (msg.StartsWith("Friends currently on EverQuest"))
            {
                state.InFriendsList = true;
            }
            else if (msg.EndsWith(" your friend."))
            {
                var fc = FriendChange().Match(msg);
                if (fc.Success)
                {
                    var who = fc.Groups["name"].Value;
                    if (fc.Groups["no"].Value == "now")
                    {
                        var f = state.Friend(who);
                        f.Name = who; // keep the latest capitalization
                        f.Added ??= ts;
                    }
                    else
                    {
                        state.Friends.RemoveAll(f => f.Name.Equals(who, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            else if (msg.StartsWith("You are currently bound in: "))
            {
                var b = BoundIn().Match(msg);
                if (b.Success) { state.BindZone = b.Groups["zone"].Value; state.BindSeen = ts; state.BindHow = "/charinfo"; }
            }
            else if (msg.Contains("guild", StringComparison.OrdinalIgnoreCase) && !msg.Contains(", '"))
            {
                // System messages only (chat lines contain ", '"). Best-effort: P1999 wording varies.
                var g = GuildJoined().Match(msg);
                if (g.Success) { state.Guild = g.Groups["guild"].Value.Trim(); state.GuildSeen = ts; }
                else if (GuildLeft().IsMatch(msg)) { state.Guild = null; state.GuildSeen = ts; }
            }
            else if (msg == "You feel yourself bind to the area.")
            {
                if (state.Zone != null) { state.BindZone = state.Zone; state.BindSeen = ts; state.BindHow = "bound here"; }
            }
            else if (msg.StartsWith("You have gained a level"))
            {
                var l = LevelUp().Match(msg);
                if (l.Success) { state.Level = int.Parse(l.Groups["lvl"].Value); state.LevelSeen = ts; }
            }
            else if (msg.Contains("lost a level", StringComparison.OrdinalIgnoreCase))
            {
                var l = LevelDown().Match(msg);
                if (l.Success) { state.Level = int.Parse(l.Groups["lvl"].Value); state.LevelSeen = ts; }
            }
            else if (msg.StartsWith("You have entered "))
            {
                var z = ZoneLine().Match(msg);
                // Skip "You have entered an area where levitation effects do not function." etc.
                if (z.Success && !msg.StartsWith("You have entered an area"))
                {
                    state.Zone = z.Groups["zone"].Value;
                    state.Trail.Clear(); // locs from the previous zone don't belong on this map
                }
            }
            else if (msg.StartsWith("You think you are heading "))
            {
                state.Heading = msg["You think you are heading ".Length..].TrimEnd('.', ' ');
                state.HeadingSeen = ts;
            }
        }

        var changed = consumed != state.Offset;
        state.Offset = consumed;
        return changed;
    }

    /// <summary>
    /// "You have slain a fire beetle!" (killer = null) or "a plague rat has been slain by Somebody!".
    /// Skips chat, your own death, Eye of Zomm, and players killed by mobs ("Somefriend has been slain by a gnoll!").
    /// </summary>
    internal static bool TryParseKill(string msg, string self, out string victim, out string? killer)
    {
        victim = ""; killer = null;
        if (msg.Contains(", '")) return false; // chat line that happens to end in "!"
        if (msg.StartsWith("You have slain "))
        {
            victim = msg["You have slain ".Length..^1].Trim();
            return victim.Length > 0;
        }
        var at = msg.IndexOf(" has been slain by ", StringComparison.Ordinal);
        if (at <= 0) return false;
        victim = msg[..at].Trim();
        killer = msg[(at + " has been slain by ".Length)..^1].Trim();
        if (victim.Length == 0 || killer.Length == 0) return false;
        if (victim.Equals(self, StringComparison.OrdinalIgnoreCase)) return false;
        if (victim.StartsWith("Eye of ", StringComparison.Ordinal)) return false;
        // One capitalized word killed by something with a space or in lower case ("a gnoll") is a player death.
        var victimIsPlayerLike = !victim.Contains(' ') && char.IsUpper(victim[0]);
        var killerIsMobLike = killer.Contains(' ') || char.IsLower(killer[0]);
        if (victimIsPlayerLike && killerIsMobLike) return false;
        return true;
    }

    /// <summary>Parses "2670.49, 879.69, -32.22" (Y, X, Z).</summary>
    private static bool TryParseLoc(ReadOnlySpan<char> s, out double y, out double x, out double z)
    {
        y = x = z = 0;
        var c1 = s.IndexOf(',');
        if (c1 < 0) return false;
        var rest = s[(c1 + 1)..];
        var c2 = rest.IndexOf(',');
        if (c2 < 0) return false;
        var inv = CultureInfo.InvariantCulture;
        return double.TryParse(s[..c1].Trim(), NumberStyles.Float, inv, out y)
            && double.TryParse(rest[..c2].Trim(), NumberStyles.Float, inv, out x)
            && double.TryParse(rest[(c2 + 1)..].Trim(), NumberStyles.Float, inv, out z);
    }

    private static bool EndsWithNewline(FileStream fs)
    {
        if (fs.Length == 0) return true;
        var pos = fs.Position;
        fs.Seek(-1, SeekOrigin.End);
        var b = fs.ReadByte();
        fs.Seek(pos, SeekOrigin.Begin);
        return b == '\n';
    }

    // "Tue Nov 10 15:16:42 2026"
    public static DateTime? ParseTimestamp(string s) =>
        DateTime.TryParseExact(s, "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
}
