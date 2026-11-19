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
/// from "X has been slain by Killer!" (another player, a pet, ...). <see cref="Zone"/> is the log's zone name.
/// <see cref="Kind"/> is "faction" or "xp" for a death with no "slain" line, noticed only from a faction change or
/// experience (the mob's name isn't known, so there's no respawn time). <see cref="X"/>/<see cref="Y"/> is the
/// last /loc before the kill (map coordinates), when it was taken in the same zone shortly before.</summary>
public sealed record KillRecord(string Name, string? Zone, DateTime Time, string? By, string? Kind = null,
                                double? X = null, double? Y = null);

/// <summary>A spell effect with a timer: a buff or debuff on you (<see cref="Target"/> null), or a spell you cast
/// on someone else. <see cref="Seconds"/> is the estimated duration from spells_us.txt; <see cref="Others"/> lists
/// other spells with the same landing message when the log line was ambiguous.</summary>
public sealed record ActiveSpell(string Name, string? Target, DateTime Start, int Seconds, bool Beneficial,
                                 bool ByYou, string? Others = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime End => Start.AddSeconds(Seconds);
}

/// <summary>Log lines worth a sound (see <see cref="LogScanner.Notice"/>).</summary>
public enum LogNotice { Fall, Stand, Tell, Fade, Illusion, Invite, Trade, Food, Fizzle, Mana, Forage, Charm, Gem }

public sealed class LogState
{
    /// <summary>Bumped when the scanner learns new things, so cached logs are re-read once from the start.</summary>
    public const int CurrentVersion = 6; // 6: $xpNN
    public int Version { get; set; }

    public string Path { get; set; } = "";
    public string Character { get; set; } = "";
    public string Server { get; set; } = "";
    public long Offset { get; set; }
    public int? Level { get; set; }
    public DateTime? LevelSeen { get; set; }
    /// <summary>"$xp78" typed by this character (any channel: "You say, '$xp78'"): 78% into <see cref="XpLevel"/>.
    /// EverQuest doesn't log experience percent, so this is how it's recorded.</summary>
    public int? XpPercent { get; set; }
    public int? XpLevel { get; set; }
    public DateTime? XpSeen { get; set; }
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

    /// <summary>Buffs/debuffs on you and spells you cast on others that haven't run out (see LogScanner.TrackSpells).</summary>
    public List<ActiveSpell> Spells { get; set; } = new();
    public const int SpellsMax = 80;
    /// <summary>From "You begin casting X.": what's being cast and when, to tell which spell landed and on whom.</summary>
    public string? Casting { get; set; }
    public DateTime? CastingSince { get; set; }
    /// <summary>What you last hit or swung at in melee ("You pierce a froglok for 116 points of damage."), for
    /// weapon procs, which land with no "You begin casting" line.</summary>
    public string? MeleeTarget { get; set; }
    public DateTime? MeleeSeen { get; set; }
    /// <summary>Last "Your song ends." (Bards only see that line, for their own songs).</summary>
    public DateTime? SongEnded { get; set; }
    /// <summary>Song landings on others ("A gnoll's hair stands on end.") waiting for "Your song ends." to show
    /// they were yours.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<((SpellInfo Song, List<SpellInfo> All) Song, string Target, DateTime At)> PendingSongs { get; } = new();

    // Faction/experience lines within a couple of seconds, waiting to be matched against "slain" lines.
    public DateTime? PendingTime { get; set; }
    public int PendingXp { get; set; }
    public int PendingSlain { get; set; }
    public bool PendingQuest { get; set; }
    public string? PendingFaction { get; set; }

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

    // "$xp78" / "$XP 78" (percent into the current level).
    [GeneratedRegex(@"\$xp\s*(?<pct>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex XpTag();

    [GeneratedRegex(@"^You have gained a level! Welcome to level (?<lvl>\d+)!")]
    private static partial Regex LevelUp();

    [GeneratedRegex(@"^You (?:have )?lost a level.*?level (?<lvl>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LevelDown();

    [GeneratedRegex(@"^You have entered (?<zone>.+)\.$")]
    private static partial Regex ZoneLine();

    // "Somefriend tells you, 'hi'" (players have one-word names; NPC merchants/bankers have more).
    [GeneratedRegex(@"^(?<who>[A-Za-z]+) tells you, '(?<text>.*)'$")]
    private static partial Regex TellLine();

    // Pets and one-word-name NPCs also "tell" you things.
    [GeneratedRegex(@"^(Attacking |Following you|Guarding |Sorry, Master|I'll give you |That'll be |Welcome to my bank|Come back soon)|Master\.?$")]
    private static partial Regex NpcTell();

    /// <summary>
    /// Raised (on the log reader's thread) for fresh lines that deserve a sound: falling ("You feel as if you are
    /// about to fall."), needing to stand ("You must be standing to ..."), and tells from players. Only lines
    /// stamped within the last 20 seconds count, so reading an old log doesn't replay them.
    /// Arguments: what happened, the character whose log it is, that character's class from the log (or null),
    /// the line.
    /// </summary>
    public static event Action<LogNotice, string, string?, string>? Notice;

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
            state.Spells.Clear(); state.Casting = null; state.CastingSince = null;
            state.XpPercent = state.XpLevel = null; state.XpSeen = null;
            state.PendingTime = null;
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

            if (Notice != null && ts is { } lt && (DateTime.Now - lt).TotalSeconds < 20) CheckNotice(name, state.Class, msg);

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

            // "$xp78" typed by this character only ("You say, '$xp78'", "You tell your party, '...'", ...); never
            // from anyone else's chat.
            if (msg.StartsWith("You ") && msg.Contains(", '") && msg.Contains("$xp", StringComparison.OrdinalIgnoreCase) &&
                XpTag().Match(msg[msg.IndexOf(", '", StringComparison.Ordinal)..]) is { Success: true } xp &&
                int.TryParse(xp.Groups["pct"].Value, out var pct) && pct <= 100)
            {
                state.XpPercent = pct;
                state.XpLevel = state.Level;
                state.XpSeen = ts;
                continue;
            }

            // A faction/experience group ends once the log has moved on a couple of seconds.
            if (state.PendingTime is { } pt && ts is { } now && (now - pt).TotalSeconds > PendingSeconds) FlushPending(state);

            // Buffs and spell timers: only lines recent enough for a buff to still be running.
            if (ts is { } spellTs && !Effects.IsEmpty && (DateTime.Now - spellTs).TotalHours < SpellLookbackHours)
                TrackSpells(state, Effects, msg, spellTs);

            // Mob deaths (spawn timers). Before the other checks: a mob's name can contain words like "guild".
            if (msg.EndsWith('!') && TryParseKill(msg, name, out var victim, out var killer))
            {
                var (kx, ky) = KillPosition(state, ts);
                AddKill(state, new KillRecord(victim, state.Zone, ts ?? DateTime.Now, killer, X: kx, Y: ky));
                if (state.Spells.Count > 0)
                    state.Spells.RemoveAll(s => s.Target != null && s.Target.Equals(victim, StringComparison.OrdinalIgnoreCase));
                Pending(state, ts).PendingSlain++;
                continue;
            }
            // Deaths without a "slain" line still give experience and/or faction ("Your faction standing with X
            // got worse."). Quest turn-ins give them too, but come with an NPC talking or paying you.
            if (msg.StartsWith("You gain") && msg.EndsWith("experience!!"))
            {
                Pending(state, ts).PendingXp++;
                continue;
            }
            if (msg.StartsWith("Your faction standing with "))
            {
                var ps = Pending(state, ts);
                var rest = msg["Your faction standing with ".Length..];
                var sp = rest.IndexOf(' ');
                var faction = sp > 0 ? rest[..sp] : rest;
                // The mob's own faction is the one that gets worse; keep the first of those.
                if (ps.PendingFaction == null || (!rest.Contains("better") && ps.PendingFaction.EndsWith('+')))
                    ps.PendingFaction = rest.Contains("better") ? faction + "+" : faction;
                continue;
            }
            if ((msg.StartsWith("You receive ") && msg.Contains(" from ") && !msg.Contains(" from the corpse") &&
                 !msg.Contains(" for the ")) ||
                (msg.Contains(" says, '") && msg.IndexOf(' ') < msg.IndexOf(" says, '")))
            {
                // Quest reward / NPC talking (multi-word speaker = NPC; players are one word).
                if (state.PendingTime != null || msg.Contains(" says, '")) Pending(state, ts).PendingQuest = true;
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
                // Skip "You have entered an area where levitation effects do not function.", "You have entered an
                // Arena (PvP) area." etc.: no zone name starts with "an ".
                if (z.Success && !msg.StartsWith("You have entered an ", StringComparison.OrdinalIgnoreCase))
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

        // Nothing more for a while: settle the last faction/experience group.
        if (state.PendingTime is { } last && (DateTime.Now - last).TotalSeconds > PendingSeconds + 1) FlushPending(state);

        var changed = consumed != state.Offset;
        state.Offset = consumed;
        return changed;
    }

    private const double PendingSeconds = 2;

    /// <summary>Spell data from the client's spells_us.txt (set by DataStore); empty = no buff tracking.</summary>
    public static SpellEffects Effects { get; set; } = SpellEffects.Empty;
    private const double SpellLookbackHours = 3;
    /// <summary>A song's landing line and "Your song ends." are logged within this many seconds of each other.</summary>
    private const double SongMatchSeconds = 1.5;

    /// <summary>
    /// Buff tracking, as EqTool does it: the spell's "lands on you" line starts a timer on you ("Your skin turns
    /// hard as steel."), its "wears off" line ends it ("Your skin returns to normal."). After "You begin casting X.",
    /// X's "lands on someone" line ("Somebody's skin turns hard as steel.") starts a timer on that target, which ends
    /// with "Your X spell has worn off." or when the target dies. Durations come from the spell's formula.
    /// </summary>
    private static void TrackSpells(LogState state, SpellEffects fx, string msg, DateTime ts)
    {
        if (msg.Contains(", '") || msg.Contains(" says '")) return; // chat

        if (msg.StartsWith("You begin casting "))
        {
            state.Casting = msg["You begin casting ".Length..].TrimEnd('.');
            state.CastingSince = ts;
            return;
        }
        if (msg is "Your spell is interrupted." or "Your spell fizzles!" || msg.StartsWith("Your target resisted the "))
        {
            state.Casting = null;
            return;
        }
        if (msg is "You died." || msg.StartsWith("You have been slain by "))
        {
            state.Spells.RemoveAll(s => s.Target == null);
            state.Casting = null;
            return;
        }
        if (msg.StartsWith("Your ") && msg.EndsWith(" spell has worn off."))
        {
            var spell = msg["Your ".Length..^" spell has worn off.".Length];
            var gone = state.Spells.Where(s => s.ByYou && s.Target != null && SameSpell(s.Name, spell))
                                   .OrderBy(s => s.Start).FirstOrDefault();
            if (gone != null) state.Spells.Remove(gone);
            return;
        }

        // The spell being cast, if its cast time (plus lag: item clicks were seen landing 10 s after a 5 s cast)
        // hasn't passed.
        SpellInfo? casting = null;
        if (state.Casting is { } cn && state.CastingSince is { } since && fx.ByName(cn) is { } ci &&
            (ts - since).TotalSeconds <= ci.CastTimeMs / 1000.0 + 10)
            casting = ci;

        if (fx.LandsOnYou(msg) is { } onYou)
        {
            var s = SpellEffects.Pick(onYou, casting, state.Class, state.Level);
            AddSpell(state, s, null, ts, byYou: casting == s, onYou);
            return;
        }
        if (fx.FadesFromYou(msg) is { } faded)
        {
            state.Spells.RemoveAll(a => a.Target == null && faded.Any(f => SameSpell(f.Name, a.Name)));
            return;
        }
        if (casting is { CastOnOther.Length: > 0 } c && msg.EndsWith(c.CastOnOther) && !msg.StartsWith("You"))
        {
            var target = msg[..^c.CastOnOther.Length].Trim();
            if (target.Length > 0 && target.Length < 64) { AddSpell(state, c, target, ts, true, null); return; }
        }

        // Bard songs never log "You begin singing"; your own songs end with "Your song ends." in the same second as
        // the line saying where they landed, before or after it. Other bards' songs don't give you that line.
        if (msg == "Your song ends.")
        {
            state.SongEnded = ts;
            foreach (var (song, target, at) in state.PendingSongs)
                if ((ts - at).TotalSeconds <= SongMatchSeconds) AddSpell(state, song.Song, target, at, true, song.All, sureWhich: false);
            state.PendingSongs.Clear();
            return;
        }
        if ((state.Class == "Bard" || state.SongEnded != null) && !msg.StartsWith("You") &&
            fx.SongLandsOnOther(msg, out var songTarget, out var songs))
        {
            var song = SpellEffects.Pick(songs, null, "Bard", state.Level);
            if (state.SongEnded is { } ended && (ts - ended).TotalSeconds is >= 0 and <= SongMatchSeconds)
                AddSpell(state, song, songTarget, ts, true, songs, sureWhich: false);
            else
            {
                state.PendingSongs.RemoveAll(p => (ts - p.At).TotalSeconds > SongMatchSeconds);
                state.PendingSongs.Add(((song, songs), songTarget, ts));
            }
            return;
        }

        // Melee: remember the target for weapon procs.
        if (msg.StartsWith("You ") && MeleeLine().Match(msg) is { Success: true } hit)
        {
            state.MeleeTarget = hit.Groups["t"].Value;
            state.MeleeSeen = ts;
            return;
        }
        // A weapon/item proc landing on the mob you're fighting ("A froglok shin knight's blood boils."): no
        // "begin casting" line, so it's matched by the mob's name + a proc effect's landing text. (A groupmate's
        // identical proc on the same mob looks the same; it just refreshes the timer.)
        if (state.MeleeTarget is { } mt && state.MeleeSeen is { } seen && (ts - seen).TotalSeconds <= 10 &&
            msg.Length > mt.Length && msg.StartsWith(mt, StringComparison.OrdinalIgnoreCase) &&
            fx.ProcLandsOnOther(msg[mt.Length..]) is { Count: > 0 } procs)
        {
            AddSpell(state, procs[0], mt, ts, true, null);
        }
    }

    // "You pierce a froglok shin knight for 116 points of damage." / "You try to slash a gnoll, but miss!"
    [GeneratedRegex(@"^You (?:try to )?(?:hit|slash|pierce|crush|bash|kick|punch|backstab|strike|bite|claw|smash|slam|maul|gore|sting|frenzy on|round kick|flying kick|tiger claw|eagle strike|dragon punch) (?<t>.+?)(?: for \d+ points? of damage\.|, but miss!)$")]
    private static partial Regex MeleeLine();

    private static bool SameSpell(string a, string b) =>
        a.Replace('`', '\'').Equals(b.Replace('`', '\''), StringComparison.OrdinalIgnoreCase);

    /// <param name="sureWhich">False when it's yours but the line fits several spells (songs: "begins to chant." is
    /// Tuyen's Chant of Flame or of Frost), so the alternatives are still listed.</param>
    private static void AddSpell(LogState state, SpellInfo s, string? target, DateTime ts, bool byYou, List<SpellInfo>? candidates,
                                 bool sureWhich = true)
    {
        // Your own spells last by your level; someone else's: assume at least your level (buffs mostly hit their cap).
        var minForClass = state.Class != null && s.Levels.TryGetValue(state.Class, out var cl) ? cl : s.MinLevel;
        // Level unknown (no /who yet): assume 50, or the spell's level if higher (procs have no level at all).
        var casterLevel = byYou ? Math.Max(state.Level ?? Math.Max(minForClass, 50), minForClass)
                                : Math.Max(state.Level ?? 50, s.MinLevel);
        var seconds = s.DurationSeconds(casterLevel);
        if (seconds <= 0) return;
        // "A froglok shin knight's blood boils." starts a sentence; the mob is "a froglok shin knight".
        if (target != null && (target.StartsWith("A ") || target.StartsWith("An "))) target = char.ToLower(target[0]) + target[1..];
        // Same landing message, different spells: say which others it could be (unless you cast it yourself).
        var others = (!byYou || !sureWhich) && candidates is { Count: > 1 }
            ? string.Join(", ", candidates.Where(c => c != s).Select(c => SpellInfo.DisplayName(c.Name)).Take(6))
            : null;
        state.Spells.RemoveAll(a => SameSpell(a.Name, s.Name) &&
                                    string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));
        state.Spells.RemoveAll(a => a.End < ts);
        state.Spells.Add(new ActiveSpell(SpellInfo.DisplayName(s.Name), target, ts, seconds, s.Beneficial, byYou, others));
        if (state.Spells.Count > LogState.SpellsMax) state.Spells.RemoveRange(0, state.Spells.Count - LogState.SpellsMax);
    }

    /// <summary>Where a kill happened, roughly: the last /loc, if it's in this zone and under 10 minutes old.</summary>
    private static (double? X, double? Y) KillPosition(LogState state, DateTime? when)
    {
        if (state.MapX is not { } x || state.MapY is not { } y || state.LocZone != state.Zone || state.LocSeen is not { } seen)
            return (null, null);
        var t = when ?? DateTime.Now;
        return (t - seen).TotalMinutes is >= 0 and <= 10 ? (x, y) : (null, null);
    }

    private static void CheckNotice(string character, string? cls, string msg)
    {
        if (Classify(msg) is not { } notice) return;
        try { Notice?.Invoke(notice, character, cls, msg); } catch { /* a sound must never break log reading */ }
    }

    /// <summary>The item in "--You have looted a Flawless Diamond.--" (also "an", "some", or "Somebody has looted").
    /// Whole name only, so "Crushed Nephrite" isn't "Nephrite".</summary>
    public static string? LootedItem(string msg)
    {
        if (!msg.StartsWith("--") || !msg.EndsWith(".--")) return null;
        var at = msg.IndexOf(" looted ", StringComparison.Ordinal);
        if (at < 0) return null;
        var item = msg[(at + " looted ".Length)..^3];
        foreach (var article in new[] { "a ", "an ", "some ", "the " })
            if (item.StartsWith(article)) { item = item[article.Length..]; break; }
        return item.Length > 0 ? item : null;
    }

    /// <summary>Who looted, from "--You have looted ...--" ("You") or "--Somebody has looted ...--".</summary>
    public static string Looter(string msg) => msg.Length > 2 ? msg[2..].Split(' ', 2)[0] : "";

    /// <summary>The mob in "A bulthar's eyes glaze over." ("a bulthar"), or null.</summary>
    public static string? GlazedMob(string msg)
    {
        const string tail = "'s eyes glaze over.";
        if (!msg.EndsWith(tail)) return null;
        var mob = msg[..^tail.Length];
        // The game capitalizes the first letter at the start of a sentence; mob names are "a bulthar".
        if (mob.StartsWith("A ") || mob.StartsWith("An ")) mob = char.ToLower(mob[0]) + mob[1..];
        return mob.Length > 0 ? mob : null;
    }

    /// <summary>Which alert (if any) a log message is. Chat lines never are, except tells to you.</summary>
    public static LogNotice? Classify(string msg)
    {
        if (msg.Contains(" tells you, '"))
            return TellLine().Match(msg) is { Success: true } t && !NpcTell().IsMatch(t.Groups["text"].Value) ? LogNotice.Tell : null;
        if (msg.Contains(", '") || msg.Contains(" says '")) return null; // chat / NPC speech

        // "--You have looted a Flawless Diamond.--" / "--Somebody has looted a Nephrite.--"
        if (msg.StartsWith("--") && LootedItem(msg) is { } item)
            return Sounds.VeliousGems.Contains(item) ? LogNotice.Gem : null;

        if (msg.StartsWith("You "))
        {
            if (msg == "You feel as if you are about to fall.") return LogNotice.Fall;
            if (msg.StartsWith("You must be standing to ")) return LogNotice.Stand;
            if (msg == "You feel yourself starting to appear.") return LogNotice.Fade;
            if (msg == "You feel as if you are about to look like yourself again.") return LogNotice.Illusion;
            if ((msg.StartsWith("You are low on ") || msg.StartsWith("You are out of ")) &&
                (msg.Contains("food") || msg.Contains("drink"))) return LogNotice.Food;
            if (msg is "You are hungry." or "You are thirsty.") return LogNotice.Food;
            if (msg == "You miss a note, bringing your song to a close!") return LogNotice.Fizzle;
            if (msg.StartsWith("You have scrounged up ")) return LogNotice.Forage;
            return null;
        }
        if (msg.StartsWith("Your "))
        {
            if (msg == "Your shadows fade.") return LogNotice.Fade;
            if (msg == "Your spell fizzles!") return LogNotice.Fizzle;
            return null;
        }
        if (msg.EndsWith(" invites you to join a group.")) return LogNotice.Invite;
        if (msg == "Insufficient Mana to cast this spell!") return LogNotice.Mana;
        if (msg.EndsWith("'s eyes glaze over.")) return LogNotice.Charm;
        if (msg.Contains(" has offered you ")) return LogNotice.Trade;
        return null;
    }

    private static void AddKill(LogState state, KillRecord k)
    {
        state.Kills.Add(k);
        if (state.Kills.Count > LogState.KillsMax) state.Kills.RemoveRange(0, state.Kills.Count - LogState.KillsMax);
    }

    private static LogState Pending(LogState state, DateTime? ts)
    {
        if (state.PendingTime == null)
        {
            state.PendingTime = ts ?? DateTime.Now;
            state.PendingXp = state.PendingSlain = 0;
            state.PendingQuest = false;
            state.PendingFaction = null;
        }
        return state;
    }

    /// <summary>
    /// Turns experience/faction lines that no "slain" line explains into unnamed deaths: one per experience
    /// message beyond the slain count, or one for faction changes alone. Quest turn-ins are skipped.
    /// </summary>
    private static void FlushPending(LogState state)
    {
        if (state.PendingTime is not { } t) return;
        if (!state.PendingQuest)
        {
            var faction = state.PendingFaction?.TrimEnd('+');
            var deaths = state.PendingXp > 0 ? state.PendingXp - state.PendingSlain
                       : state.PendingSlain == 0 && faction != null ? 1 : 0;
            var (kx, ky) = KillPosition(state, t);
            for (var i = 0; i < deaths; i++)
            {
                AddKill(state, faction != null
                    ? new KillRecord($"faction death ({faction})", state.Zone, t, null, "faction", kx, ky)
                    : new KillRecord("xp death", state.Zone, t, null, "xp", kx, ky));
            }
        }
        state.PendingTime = null;
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
