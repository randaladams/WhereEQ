using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Spawn timers: every mob death in the logs ("You have slain X!", optionally "X has been slain by Y!") starts a
/// countdown using the bundled P1999 respawn times. Timers can also be started by hand.
/// </summary>
public partial class TimersWindow : Window
{
    /// <summary>Within this many seconds, the same mob in the same zone seen in two logs is one kill.</summary>
    private const double SameKillSeconds = 5;
    /// <summary>Kills with no known respawn stay this long.</summary>
    private static readonly TimeSpan UnknownKeep = TimeSpan.FromMinutes(30);
    private static readonly int[] KeepChoices = { 1, 5, 15, 60 };

    public sealed class TimerRow : INotifyPropertyChanged
    {
        public string Key { get; init; } = "";
        public string Mob { get; init; } = "";
        public string? ZoneShort { get; init; }
        public string ZoneText { get; init; } = "";
        public DateTime Killed { get; init; }
        public string KilledText => Killed.ToString("HH:mm:ss");
        public string By { get; set; } = "";
        public bool Mine { get; set; }
        public TimeSpan? Respawn { get; init; }
        public SpawnSource Source { get; init; }
        public bool IsManual { get; init; }
        public DateTime? Due => Respawn is { } r ? Killed + r : null;

        public string RespawnText => Respawn is not { } r ? "none" : Format(r) + (Source == SpawnSource.Guess ? "?" : "");
        public string RespawnSourceText => Source switch
        {
            SpawnSource.Yours => "Your saved time for this mob",
            SpawnSource.Npc => "P1999 time for this mob (EqTool / P1999 wiki)",
            SpawnSource.Zone => "Zone's usual respawn time (EqTool / P1999 wiki); some mobs differ",
            SpawnSource.Guess => "No data for this zone: 6:40 is the most common P1999 timer",
            _ => IsManual ? "Started by hand" : "This zone doesn't respawn",
        };

        public string LeftText { get; private set; } = "";
        /// <summary>Waiting, Soon (under a minute), Up, Unknown.</summary>
        public string State { get; private set; } = "";

        public void Tick(DateTime now)
        {
            string text, state;
            if (Due is not { } due) { text = "?"; state = "Unknown"; }
            else if (now >= due) { text = "up " + Format(now - due); state = "Up"; }
            else
            {
                var left = due - now;
                text = Format(left + TimeSpan.FromSeconds(0.999)); // count down to 0:01, then "up"
                state = left.TotalSeconds <= 60 ? "Soon" : "Waiting";
            }
            if (text != LeftText) { LeftText = text; Changed(nameof(LeftText)); }
            if (state != State) { State = state; Changed(nameof(State)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly Settings _settings;
    private readonly SpawnTimes _spawnTimes;
    private List<Character> _characters = new();
    private List<TimerRow> _rows = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _updatingUi = true;

    public TimersWindow(Settings settings, SpawnTimes spawnTimes)
    {
        InitializeComponent();
        _settings = settings;
        _spawnTimes = spawnTimes;
        var t = settings.Timers;

        Width = t.Width;
        Height = t.Height;
        if (t.Left is { } l && t.Top is { } top && OnScreen(l, top)) { Left = l; Top = top; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = t.Topmost;

        OthersCheck.IsChecked = t.IncludeOthers;
        TopmostCheck.IsChecked = t.Topmost;
        KeepBox.ItemsSource = KeepChoices.Select(m => m == 60 ? "1 hour up" : $"{m} min up").ToList();
        KeepBox.SelectedIndex = Math.Max(0, Array.IndexOf(KeepChoices, t.KeepUpMinutes));
        _updatingUi = false;

        HintText.Text = spawnTimes.IsEmpty
            ? "Spawn time data is missing (Data\\SpawnTimes); every timer uses 6:40."
            : "Timers start from \"You have slain …\" in your logs. Timer column: ? = guess (6:40). " +
              "Right-click a timer to restart or remove it. Times: P1999 wiki via EqTool (MIT).";

        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); SaveState(); };
        LocationChanged += (_, _) => SaveState();
        SizeChanged += (_, _) => SaveState();
    }

    private static bool OnScreen(double l, double t) =>
        l > SystemParameters.VirtualScreenLeft - 100 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
        t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    private void SaveState()
    {
        var t = _settings.Timers;
        if (WindowState == WindowState.Normal && IsLoaded) { t.Left = Left; t.Top = Top; t.Width = Width; t.Height = Height; }
        _settings.Save();
    }

    // ------------------------------------------------------------------ data

    /// <summary>Called by the main window whenever logs change.</summary>
    public void Update(List<Character> characters)
    {
        _characters = characters;
        Rebuild();
    }

    private void Rebuild()
    {
        var t = _settings.Timers;
        var now = DateTime.Now;
        var keepUp = TimeSpan.FromMinutes(t.KeepUpMinutes);
        var dismissed = new HashSet<string>(t.Dismissed);
        var rows = new List<TimerRow>();

        // Kills from every character's log, oldest first so duplicates merge into the first one seen.
        var kills = _characters.Where(c => c.Log != null)
            .SelectMany(c => SafeKills(c.Log!).Select(k => (Char: c.Name, Kill: k)))
            .Where(x => t.ClearedBefore is not { } cb || x.Kill.Time > cb)
            .Where(x => now - x.Kill.Time < TimeSpan.FromDays(2))
            .OrderBy(x => x.Kill.Time);
        foreach (var (charName, k) in kills)
        {
            var zoneShort = ZoneNames.ToShort(k.Zone);
            var mine = k.By == null;
            var dup = rows.FirstOrDefault(r => r.Mob.Equals(k.Name, StringComparison.OrdinalIgnoreCase) &&
                                               r.ZoneShort == zoneShort &&
                                               Math.Abs((r.Killed - k.Time).TotalSeconds) <= SameKillSeconds);
            if (dup != null)
            {
                // Same death seen in another of your logs: prefer "you killed it" over "slain by".
                if (mine && !dup.Mine) { dup.Mine = true; dup.By = charName; }
                continue;
            }
            var key = $"{charName}|{k.Time.Ticks}|{k.Name}";
            if (dismissed.Contains(key)) continue;
            var (respawn, source) = _spawnTimes.Get(zoneShort, k.Name, t.Overrides);
            rows.Add(new TimerRow
            {
                Key = key, Mob = k.Name, ZoneShort = zoneShort, ZoneText = k.Zone ?? "",
                Killed = k.Time, By = mine ? charName : k.By!, Mine = mine, Respawn = respawn, Source = source,
            });
        }
        if (!t.IncludeOthers) rows.RemoveAll(r => !r.Mine);

        foreach (var m in t.Manual)
        {
            rows.Add(new TimerRow
            {
                Key = ManualKey(m), Mob = m.Name, ZoneShort = m.Zone,
                ZoneText = m.Zone != null ? ZoneNames.ToLong(m.Zone) : "", Killed = m.Start, By = "(by hand)", Mine = true,
                Respawn = TimeSpan.FromSeconds(m.Seconds), Source = SpawnSource.None, IsManual = true,
            });
        }

        rows = rows.Where(r => r.Due is { } due ? due + keepUp > now : now - r.Killed < UnknownKeep)
                   .OrderBy(r => r.Due ?? DateTime.MaxValue).ThenBy(r => r.Killed).ToList();

        // Forget timers that are long gone so settings don't grow forever.
        t.Manual.RemoveAll(m => m.Start + TimeSpan.FromSeconds(m.Seconds) + TimeSpan.FromHours(2) < now);
        if (t.Dismissed.Count > 500) t.Dismissed.RemoveRange(0, t.Dismissed.Count - 500);

        foreach (var r in rows) r.Tick(now);
        if (rows.Select(r => r.Key + r.RespawnText + r.By).SequenceEqual(_rows.Select(r => r.Key + r.RespawnText + r.By))) return;

        var selected = (Grid.SelectedItem as TimerRow)?.Key;
        _rows = rows;
        Grid.ItemsSource = rows;
        if (selected != null) Grid.SelectedItem = rows.FirstOrDefault(r => r.Key == selected);
    }

    private static KillRecord[] SafeKills(LogState log)
    {
        // The log reader appends on a background thread; retry if we catch it mid-update.
        for (var i = 0; i < 3; i++)
        {
            try { return log.Kills.ToArray(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return Array.Empty<KillRecord>();
    }

    private static string ManualKey(ManualTimer m) => $"manual|{m.Start.Ticks}|{m.Name}";

    private void Tick()
    {
        var now = DateTime.Now;
        var keepUp = TimeSpan.FromMinutes(_settings.Timers.KeepUpMinutes);
        foreach (var r in _rows) r.Tick(now);
        // Drop timers whose "keep for" time ran out.
        if (_rows.Any(r => r.Due is { } due ? due + keepUp <= now : now - r.Killed >= UnknownKeep)) Rebuild();
    }

    // ------------------------------------------------------------------ UI

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.Timers.IncludeOthers = OthersCheck.IsChecked == true;
        _settings.Timers.Topmost = Topmost = TopmostCheck.IsChecked == true;
        _settings.Save();
        Rebuild();
    }

    private void Keep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || KeepBox.SelectedIndex < 0) return;
        _settings.Timers.KeepUpMinutes = KeepChoices[KeepBox.SelectedIndex];
        _settings.Save();
        Rebuild();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _settings.Timers.ClearedBefore = DateTime.Now;
        _settings.Timers.Manual.Clear();
        _settings.Timers.Dismissed.Clear();
        _settings.Save();
        Rebuild();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is not TimerRow r) return;
        MobBox.Text = r.Mob;
        TimeBox.Text = r.Respawn is { } t ? Format(t) : "";
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Remove_Click(sender, e); e.Handled = true; }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not TimerRow r) return;
        Remove(r);
        _settings.Save();
        Rebuild();
    }

    private void Remove(TimerRow r)
    {
        if (r.IsManual) _settings.Timers.Manual.RemoveAll(m => ManualKey(m) == r.Key);
        else _settings.Timers.Dismissed.Add(r.Key);
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not TimerRow r) return;
        var respawn = r.Respawn ?? SpawnTimes.CommonDefault;
        Remove(r);
        _settings.Timers.Manual.Add(new ManualTimer
        {
            Name = r.Mob, Zone = r.ZoneShort, Start = DateTime.Now, Seconds = (int)respawn.TotalSeconds,
        });
        _settings.Save();
        Rebuild();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var name = MobBox.Text.Trim();
        if (name.Length == 0) { MobBox.Focus(); return; }
        var zone = (Grid.SelectedItem as TimerRow) is { } sel && sel.Mob.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? sel.ZoneShort : CurrentZone();
        TimeSpan time;
        if (TimeBox.Text.Trim().Length == 0) time = _spawnTimes.Get(zone, name, _settings.Timers.Overrides).Time ?? SpawnTimes.CommonDefault;
        else if (!TryParseTime(TimeBox.Text, out time)) { BadTime(); return; }
        _settings.Timers.Manual.Add(new ManualTimer { Name = name, Zone = zone, Start = DateTime.Now, Seconds = (int)time.TotalSeconds });
        _settings.Save();
        Rebuild();
    }

    private void SaveTime_Click(object sender, RoutedEventArgs e)
    {
        var name = MobBox.Text.Trim();
        if (name.Length == 0) { MobBox.Focus(); return; }
        if (!TryParseTime(TimeBox.Text, out var time)) { BadTime(); return; }
        var zone = (Grid.SelectedItem as TimerRow) is { } sel && sel.Mob.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? sel.ZoneShort : CurrentZone();
        _settings.Timers.Overrides[SpawnTimes.OverrideKey(zone, name)] = (int)time.TotalSeconds;
        _settings.Save();
        HintText.Text = $"Saved: {name} in {(zone != null ? ZoneNames.ToLong(zone) : "unknown zone")} respawns in {Format(time)}.";
        _rows.Clear(); // force a rebuild: respawn times changed
        Rebuild();
    }

    private void BadTime()
    {
        HintText.Text = "Respawn time: type minutes (22), m:ss (6:40) or h:mm:ss (8:00:00).";
        TimeBox.Focus();
        TimeBox.SelectAll();
    }

    /// <summary>Zone of whichever character logged something most recently.</summary>
    private string? CurrentZone() =>
        ZoneNames.ToShort(_characters.Where(c => c.Log?.Zone != null)
            .OrderByDescending(c => c.Log!.LastLine ?? DateTime.MinValue).FirstOrDefault()?.Log!.Zone);

    // ------------------------------------------------------------------ helpers

    /// <summary>"22" = 22 minutes, "6:40" = m:ss, "8:00:00" = h:mm:ss.</summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var s = text.Trim();
        if (s.Length == 0) return false;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            time = TimeSpan.FromMinutes(minutes);
            return true;
        }
        var parts = s.Split(':');
        if (parts.Any(p => !int.TryParse(p, out var n) || n < 0)) return false;
        var v = parts.Select(int.Parse).ToArray();
        time = v.Length switch
        {
            2 => new TimeSpan(0, v[0], v[1]),
            3 => new TimeSpan(v[0], v[1], v[2]),
            _ => TimeSpan.Zero,
        };
        return time > TimeSpan.Zero;
    }

    /// <summary>6:40, 22:00, 8:00:00.</summary>
    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
