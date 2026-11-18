namespace WhereEQ.Services;

/// <summary>Things WhereEQ can beep for. Each has its own short tone pattern so you can tell them apart.</summary>
public enum Sound { Timer, Fall, Stand, Tell }

/// <summary>
/// Short PC-speaker style beeps (Console.Beep), played off the UI thread. The same sound isn't repeated within
/// two seconds, so a burst of identical log lines ("You must be standing to attack!") beeps once.
/// </summary>
public static class Sounds
{
    private static readonly Dictionary<Sound, (int Hz, int Ms)[]> Patterns = new()
    {
        [Sound.Timer] = new[] { (1200, 120), (1600, 120) },             // rising pair: mob about to spawn
        [Sound.Fall] = new[] { (1400, 110), (1000, 110), (700, 160) },  // falling: levitate about to end
        [Sound.Stand] = new[] { (500, 150), (500, 150) },               // low double: stand up
        [Sound.Tell] = new[] { (1800, 70), (2200, 70), (1800, 70) },    // quick chirp: someone sent a tell
    };

    private static readonly Dictionary<Sound, DateTime> LastPlayed = new();
    private static readonly object Lock = new();

    public static void Play(Sound sound, bool force = false)
    {
        lock (Lock)
        {
            var now = DateTime.UtcNow;
            if (!force && LastPlayed.TryGetValue(sound, out var last) && (now - last).TotalSeconds < 2) return;
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
