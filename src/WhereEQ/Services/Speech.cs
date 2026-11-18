using System.Windows;

namespace WhereEQ.Services;

/// <summary>
/// Spoken alerts with the voice built into Windows (SAPI, the one Narrator uses): no download, works offline,
/// nothing leaves the PC. Used through COM late binding, so there's no extra package. All calls run on the UI
/// thread (SAPI's voice object is single-threaded); phrases queue up instead of talking over each other, and the
/// same phrase isn't repeated within 3 seconds.
/// </summary>
public static class Speech
{
    private const int SpeakAsync = 1; // SVSFlagsAsync
    private const int Purge = 2;      // SVSFPurgeBeforeSpeak
    private static dynamic? _voice;
    private static dynamic? _defaultVoice; // the voice token Windows picked, for "Windows default"
    private static bool _failed;
    private static string? _appliedVoice;
    private static string? _lastText;
    private static DateTime _lastAt;

    /// <summary>Last speech failure (for diagnostics), null when the last phrase was spoken.</summary>
    public static string? LastError { get; private set; }

    private static dynamic? Voice()
    {
        if (_voice != null || _failed) return _voice;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null) { _failed = true; return null; }
            _voice = Activator.CreateInstance(type);
            _defaultVoice = _voice?.Voice;
        }
        catch { _failed = true; }
        return _voice;
    }

    /// <summary>Names of the installed Windows voices (call on the UI thread).</summary>
    public static List<string> VoiceNames()
    {
        var names = new List<string>();
        try
        {
            if (Voice() is not { } v) return names;
            var tokens = v.GetVoices();
            int n = tokens.Count;
            for (var i = 0; i < n; i++) names.Add((string)tokens.Item(i).GetDescription());
        }
        catch { /* no voices */ }
        return names;
    }

    /// <summary>Says <paramref name="text"/> with the voice, speed and volume in <paramref name="s"/>. Safe to call
    /// from any thread. Falls back to a beep if Windows has no voice.</summary>
    public static void Say(string text, SoundSettings s, bool force = false)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(() => SayNow(text, s, force));
    }

    /// <summary>Stops anything being said or waiting to be said.</summary>
    public static void Stop()
    {
        var d = Application.Current?.Dispatcher;
        d?.BeginInvoke(() => { try { Voice()?.Speak("", SpeakAsync | Purge); } catch { } });
    }

    private static void SayNow(string text, SoundSettings s, bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && text == _lastText && (now - _lastAt).TotalSeconds < 3) return;
        _lastText = text;
        _lastAt = now;
        try
        {
            if (Voice() is not { } v) { LastError = "no Windows voice"; Sounds.Play(Sound.Tell, force: true); return; }
            if (s.VoiceName != _appliedVoice)
            {
                var chosen = _defaultVoice;
                var tokens = v.GetVoices();
                int n = tokens.Count;
                for (var i = 0; i < n && s.VoiceName != null; i++)
                {
                    var t = tokens.Item(i);
                    if ((string)t.GetDescription() == s.VoiceName) { chosen = t; break; }
                }
                if (chosen != null) v.Voice = chosen;
                _appliedVoice = s.VoiceName;
            }
            v.Rate = Math.Clamp(s.VoiceRate, -10, 10);
            v.Volume = Math.Clamp(s.VoiceVolume, 0, 100);
            v.Speak(text, SpeakAsync | (force ? Purge : 0));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Sounds.Play(Sound.Tell, force: true);
        }
    }
}
