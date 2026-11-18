namespace WhereEQ.Services;

/// <summary>Things WhereEQ can beep for. Each has its own short tone pattern so you can tell them apart.
/// Log-driven ones share their names with <see cref="LogNotice"/>.</summary>
public enum Sound { Timer, Fall, Stand, Tell, Fade, Illusion, SpellOff, Charmed, Invite, Trade, Rampage, Gate, Food, Died }

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
        new(LogNotice.SpellOff, "Your spell wore off", "\"Your Fear / Root / Dooming Darkness ... spell has worn off.\" (on a mob)"),
        new(LogNotice.Stand, "Stand up", "\"You must be standing to attack / cast a spell / ...\""),
        new(LogNotice.Charmed, "You're charmed", "\"You have been charmed.\""),
        new(LogNotice.Died, "You died", "\"You have been slain by ...\""),
        new(LogNotice.Rampage, "Rampage", "\"... goes on a RAMPAGE!\" / \"... begins to rampage.\""),
        new(LogNotice.Gate, "Mob gating", "\"a wurm Gates.\" / \"... begins to cast the gate spell.\""),
        new(LogNotice.Invite, "Group invite", "\"Somebody invites you to join a group.\""),
        new(LogNotice.Trade, "Trade offered", "\"Somebody has offered you ...\""),
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
        [Sound.SpellOff] = new[] { (700, 300) },                                     // one long tone
        [Sound.Charmed] = new[] { (300, 120), (600, 120), (300, 120) },              // low wobble
        [Sound.Invite] = new[] { (1000, 90), (1500, 90) },                           // up pair, short
        [Sound.Trade] = new[] { (1500, 60), (1500, 60), (1500, 60) },                // three ticks
        [Sound.Rampage] = new[] { (400, 60), (400, 60), (400, 60), (400, 60) },      // fast low alarm
        [Sound.Gate] = new[] { (600, 80), (1200, 80), (2400, 80) },                  // fast rise
        [Sound.Food] = new[] { (800, 200), (800, 200) },                             // slow pair
        [Sound.Died] = new[] { (1000, 150), (700, 150), (500, 150), (300, 300) },    // long fall
    };

    private static readonly Dictionary<Sound, DateTime> LastPlayed = new();
    private static readonly object Lock = new();

    public static Sound For(LogNotice n) => Enum.Parse<Sound>(n.ToString());

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
