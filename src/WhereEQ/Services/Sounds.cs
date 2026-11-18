namespace WhereEQ.Services;

/// <summary>Things WhereEQ can beep for. Each has its own short tone pattern so you can tell them apart.
/// Log-driven ones share their names with <see cref="LogNotice"/>.</summary>
public enum Sound { Timer, Fall, Stand, Tell, Fade, Illusion, Invite, Trade, Food, Fizzle, Mana, Forage, Charm, CharmEnd }

/// <summary>One log alert: what it's called in Settings, which lines trigger it, and whether it's on by default.</summary>
public sealed record AlertInfo(LogNotice Notice, string Label, string Description, bool DefaultOn = true);

/// <summary>
/// Short PC-speaker style beeps (Console.Beep), played off the UI thread. The same sound isn't repeated within
/// two seconds (a minute for food/drink, which the game repeats), so a burst of identical lines beeps once.
/// </summary>
public static class Sounds
{
    /// <summary>The log alerts, in the order Settings lists them.</summary>
    public static readonly AlertInfo[] Alerts =
    {
        new(LogNotice.Tell, "Tell", "A player sends you a tell (not merchants, bankers or pets)"),
        new(LogNotice.Fall, "About to fall", "\"You feel as if you are about to fall.\" (levitate wearing off)"),
        new(LogNotice.Fade, "Invisibility fading", "\"You feel yourself starting to appear.\" / \"Your shadows fade.\""),
        new(LogNotice.Illusion, "Illusion ending", "\"You feel as if you are about to look like yourself again.\"", DefaultOn: false),
        new(LogNotice.Stand, "Stand up", "\"You must be standing to attack / cast a spell / ...\""),
        new(LogNotice.Invite, "Group invite", "\"Somebody invites you to join a group.\""),
        new(LogNotice.Trade, "Trade offered", "\"Somebody has offered you ...\""),
        new(LogNotice.Fizzle, "Fizzle / missed note", "\"Your spell fizzles!\" / \"You miss a note, bringing your song to a close!\""),
        new(LogNotice.Mana, "Out of mana", "\"Insufficient Mana to cast this spell!\""),
        new(LogNotice.Forage, "Foraged", "\"You have scrounged up some food / water / ...\""),
        new(LogNotice.Charm, "Charm landed", "\"... 's eyes glaze over.\" On a Bard this starts an 18-second charm timer instead"),
        new(LogNotice.Food, "Food / drink low", "\"You are low on food / out of drink ...\" (at most once a minute)"),
    };

    private static readonly Dictionary<Sound, (int Hz, int Ms)[]> Patterns = new()
    {
        [Sound.Timer] = new[] { (1200, 120), (1600, 120) },                          // rising pair: mob about to spawn
        [Sound.Fall] = new[] { (1400, 110), (1000, 110), (700, 160) },               // falling
        [Sound.Stand] = new[] { (500, 150), (500, 150) },                            // low double
        [Sound.Tell] = new[] { (1800, 70), (2200, 70), (1800, 70) },                 // quick chirp
        [Sound.Fade] = new[] { (900, 80), (1100, 80), (900, 80), (1100, 80) },       // warble
        [Sound.Illusion] = new[] { (1100, 120), (900, 120) },                        // soft down pair
        [Sound.Invite] = new[] { (1000, 90), (1500, 90) },                           // up pair, short
        [Sound.Trade] = new[] { (1500, 60), (1500, 60), (1500, 60) },                // three ticks
        [Sound.Food] = new[] { (800, 200), (800, 200) },
        [Sound.Fizzle] = new[] { (600, 60), (450, 90) },                             // short drop
        [Sound.Mana] = new[] { (350, 250) },                                         // one low tone
        [Sound.Forage] = new[] { (1300, 50), (1700, 50) },                           // tiny up blip
        [Sound.Charm] = new[] { (900, 70), (1300, 70), (900, 70) },                  // up-down
        [Sound.CharmEnd] = new[] { (2000, 60) },                                     // one short high blip                             // slow pair
    };

    private static readonly Dictionary<Sound, DateTime> LastPlayed = new();
    private static readonly object Lock = new();

    public static Sound For(LogNotice n) => Enum.Parse<Sound>(n.ToString());

    /// <summary>Plays a log alert the way the settings say: nothing, its beep, or the voice.</summary>
    public static void Alert(SoundSettings s, LogNotice n, string line, bool force = false)
    {
        switch (s.ModeOf(n))
        {
            case AlertMode.Beep: Play(For(n), force); break;
            case AlertMode.Voice: Speech.Say(Phrase(n, line, s.ReadTells), s, force); break;
        }
    }

    /// <summary>The spawn timer 🔔: beep, or say "a fire beetle spawning in 30 seconds".</summary>
    public static void TimerAlert(SoundSettings s, string mob)
    {
        switch (s.TimerMode)
        {
            case AlertMode.Beep: Play(Sound.Timer, force: true); break;
            case AlertMode.Voice: Speech.Say($"{mob} spawning in 30 seconds", s, force: true); break;
        }
    }

    /// <summary>A Bard's charm timer is about to run out (4 seconds left).</summary>
    public static void CharmEndingAlert(SoundSettings s)
    {
        switch (s.TimerMode)
        {
            case AlertMode.Beep: Play(Sound.CharmEnd, force: true); break;
            case AlertMode.Voice: Speech.Say("Charm ending", s, force: true); break;
        }
    }

    /// <summary>What the voice says for a log line.</summary>
    public static string Phrase(LogNotice n, string line, bool readTells)
    {
        // "Somebody tells you, 'text'" / "Somebody invites you ..." / "Somebody has offered you ..."
        var who = line.Split(' ', 2)[0];
        switch (n)
        {
            case LogNotice.Tell:
                var q = line.IndexOf(", '", StringComparison.Ordinal);
                var text = q > 0 ? line[(q + 3)..].TrimEnd('\'') : "";
                return readTells && text.Length > 0 ? $"Tell from {who}: {text}" : $"Tell from {who}";
            case LogNotice.Fall: return "Levitate ending";
            case LogNotice.Fade: return "Invis fading";
            case LogNotice.Illusion: return "Illusion ending";
            case LogNotice.Stand: return "Stand up";
            case LogNotice.Invite: return $"Group invite from {who}";
            case LogNotice.Trade: return $"Trade from {who}";
            case LogNotice.Food:
                // "You are low on food." -> "Low on food"
                var t = line.StartsWith("You are ") ? line["You are ".Length..] : line;
                t = t.TrimEnd('.');
                return t.Length > 0 ? char.ToUpper(t[0]) + t[1..] : "Food and drink";
            case LogNotice.Fizzle: return line.StartsWith("You miss a note") ? "Missed note" : "Fizzle";
            case LogNotice.Mana: return "Out of mana";
            case LogNotice.Forage:
                var got = line.StartsWith("You have scrounged up ") ? line["You have scrounged up ".Length..].TrimEnd('.') : "";
                return got.Length > 0 ? $"Foraged {got}" : "Foraged";
            case LogNotice.Charm: return LogScanner.GlazedMob(line) is { } mob ? $"Charmed {mob}" : "Charm landed";
            default: return n.ToString();
        }
    }

    /// <summary>Sample lines for the ▶ test buttons.</summary>
    public static string SampleLine(LogNotice n) => n switch
    {
        LogNotice.Tell => "Somebody tells you, 'hello there'",
        LogNotice.Fall => "You feel as if you are about to fall.",
        LogNotice.Fade => "You feel yourself starting to appear.",
        LogNotice.Illusion => "You feel as if you are about to look like yourself again.",
        LogNotice.Stand => "You must be standing to cast a spell.",
        LogNotice.Invite => "Somebody invites you to join a group.",
        LogNotice.Trade => "Somebody has offered you a Bone Chips.",
        LogNotice.Food => "You are low on food.",
        LogNotice.Fizzle => "Your spell fizzles!",
        LogNotice.Mana => "Insufficient Mana to cast this spell!",
        LogNotice.Forage => "You have scrounged up some food.",
        LogNotice.Charm => "A bulthar's eyes glaze over.",
        _ => "",
    };

    public static void Play(Sound sound, bool force = false)
    {
        lock (Lock)
        {
            var now = DateTime.UtcNow;
            var gap = sound == Sound.Food ? 60 : 2;
            if (!force && LastPlayed.TryGetValue(sound, out var last) && (now - last).TotalSeconds < gap) return;
            LastPlayed[sound] = now;
        }
        var pattern = Patterns[sound];
        Task.Run(() =>
        {
            try
            {
                foreach (var (hz, ms) in pattern) Console.Beep(hz, ms);
            }
            catch
            {
                System.Media.SystemSounds.Beep.Play(); // no beep device: Windows' default sound
            }
        });
    }
}
