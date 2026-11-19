using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Timers &amp; buffs overlay. Top: spawn timers; bottom: <see cref="BuffsPanel"/>; the bar between them is dragged
/// to share the room (<see cref="TimersSettings.BuffsShare"/>). Spawn timers: every mob death in the logs ("You have slain X!", optionally "X has been slain by Y!")
/// starts a countdown using the bundled P1999 respawn times. Small and see-through like the map: compact shows
/// countdown + mob, the details button adds zone, kill time, killer and timer (wider). Lock (or Ctrl+Alt+T) makes
/// it click-through.
/// </summary>
public partial class TimersWindow : Window
{
    /// <summary>Within this many seconds, the same mob in the same zone seen in two logs is one kill.</summary>
    private const double SameKillSeconds = 5;
    /// <summary>Kills with no known respawn (count-up timers) stay this long.</summary>
    private static readonly TimeSpan UnknownKeep = TimeSpan.FromMinutes(60);
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
        /// <summary>🔔 fires this long before the end (30 s, or 4 s for a Bard charm).</summary>
        public TimeSpan AlertLead { get; init; } = TimeSpan.FromSeconds(30);
        /// <summary>Overrides "keep up timers" (charm timers vanish soon after they end).</summary>
        public TimeSpan? KeepUp { get; init; }
        public bool IsCharm { get; init; }
        /// <summary>Death of an unknown mob (from faction/experience): counts up from the kill.</summary>
        public bool CountUp { get; init; }
        public bool HasDue => Respawn != null;
        public DateTime? Due => Respawn is { } r ? Killed + r : null;

        public string RespawnText => CountUp ? "count up" : Respawn is not { } r ? "none" : Format(r) + (Source == SpawnSource.Guess ? "?" : "");
        public string RespawnSourceText => Source switch
        {
            SpawnSource.Yours => "Your saved time for this mob",
            SpawnSource.Npc => "P1999 time for this mob (EqTool / P1999 wiki)",
            SpawnSource.Zone => "Zone's usual respawn time (EqTool / P1999 wiki); some mobs differ",
            SpawnSource.Guess => "No data for this zone: 6:40 is the most common P1999 timer",
            _ => CountUp ? "No \"slain\" message: this death was seen from faction or experience, so the mob and its " +
                           "respawn time aren't known. Counting time since the kill."
               : IsManual ? "Started by hand" : "This zone doesn't respawn",
        };

        private bool _alert;
        /// <summary>🔔: beep once when the respawn is 30 seconds away.</summary>
        public bool Alert
        {
            get => _alert;
            set { if (_alert != value) { _alert = value; Changed(nameof(Alert)); } }
        }
        /// <summary>The 30-second beep already played for this timer.</summary>
        public bool Beeped { get; set; }

        public string LeftText { get; private set; } = "";
        /// <summary>Waiting, Soon (under a minute), Up, Unknown.</summary>
        public string State { get; private set; } = "";

        public void Tick(DateTime now)
        {
            string text, state;
            if (CountUp) { text = "+" + Format(now - Killed); state = "CountUp"; }
            else if (Due is not { } due) { text = "?"; state = "Unknown"; }
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
    private HwndSource? _hwnd;
    /// <summary>Width before details widened the window, to restore when details are turned off.</summary>
    private double? _compactWidth;
    private const double DetailsMinWidth = 600;

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

        OthersCheck.IsChecked = t.IncludeOthers;
        KeepBox.ItemsSource = KeepChoices.Select(m => m == 60 ? "1 hour" : $"{m} min").ToList();
        KeepBox.SelectedIndex = Math.Max(0, Array.IndexOf(KeepChoices, t.KeepUpMinutes));
        BackdropSlider.Value = t.Backdrop;
        Backdrop.Opacity = t.Backdrop;
        DetailsButton.IsChecked = t.Details;
        List.ItemTemplate = (DataTemplate)Resources[t.Details ? "DetailsTemplate" : "CompactTemplate"];
        Buffs.Init(settings);
        BuffsOthersCheck.IsChecked = settings.Buffs.ShowOnOthers;
        if (t.BuffsShare == null)
        {
            // First time with buffs in here: half the room each, and tall enough for both.
            t.BuffsShare = 0.5;
            if (Height < 360) Height = 360;
        }
        ApplySplit(t.BuffsShare.Value);
        _updatingUi = false;

        HintText.Text = spawnTimes.IsEmpty
            ? "Spawn time data is missing (Data\\SpawnTimes); every timer uses 6:40."
            : "Timers start from \"You have slain …\" in your logs. ? after a time = guess (6:40). " +
              "Right-click a timer to restart or remove it. Times: P1999 wiki via EqTool (MIT).";

        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _lockPoll.Tick += (_, _) => UpdateClickThrough();
        // Own drag instead of DragMove (Windows Snap would maximize/tile the overlay at screen edges). While
        // unlocked, drag from anywhere in the window, not just the button strip; it stays on screen.
        WindowDrag.Attach((UIElement)Content, this, anywhere: true, canDrag: () => !_settings.Timers.Locked);
        Loaded += (_, _) => WindowDrag.KeepOnScreen(this);
        SourceInitialized += (_, _) =>
        {
            _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwnd?.AddHook(WndProc);
            WindowDrag.UseSoftwareRendering(_hwnd);
            RegisterHotKey(_hwnd!.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.T));
            ApplyLock(_settings.Timers.Locked);
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _lockPoll.Stop();
            if (_hwnd != null) UnregisterHotKey(_hwnd.Handle, HotkeyId);
            SaveState();
        };
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
        Buffs.Update(characters);
        RefreshBuffsCharacterBox();
    }

    // ------------------------------------------------------------------ buffs + split

    private void ApplySplit(double buffsShare)
    {
        buffsShare = Math.Clamp(buffsShare, 0, 1);
        TimersRow.Height = new GridLength(1 - buffsShare, GridUnitType.Star);
        BuffsRow.Height = new GridLength(buffsShare, GridUnitType.Star);
    }

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var total = TimersRow.ActualHeight + BuffsRow.ActualHeight;
        if (total <= 0) return;
        _settings.Timers.BuffsShare = BuffsRow.ActualHeight / total;
        ApplySplit(_settings.Timers.BuffsShare.Value);
        _settings.Save();
    }

    private void RefreshBuffsCharacterBox()
    {
        var names = Buffs.CharacterChoices;
        if (BuffsCharacterBox.ItemsSource is List<string> old && old.SequenceEqual(names)) return;
        var was = _updatingUi;
        _updatingUi = true;
        BuffsCharacterBox.ItemsSource = names;
        BuffsCharacterBox.SelectedItem = _settings.Buffs.Character is { } c && names.Contains(c) ? c : BuffsPanel.ActiveChoice;
        _updatingUi = was;
    }

    private void BuffsCharacter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || BuffsCharacterBox.SelectedItem is not string s) return;
        _settings.Buffs.Character = s == BuffsPanel.ActiveChoice ? null : s;
        _settings.Save();
        Buffs.Reset();
    }

    private void BuffsOthers_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.Buffs.ShowOnOthers = BuffsOthersCheck.IsChecked == true;
        _settings.Save();
        Buffs.Rebuild();
    }

    private void Rebuild()
    {
        var t = _settings.Timers;
        var now = DateTime.Now;
        var keepUp = TimeSpan.FromMinutes(t.KeepUpMinutes);
        var dismissed = new HashSet<string>(t.Dismissed);
        var alerts = new HashSet<string>(t.Alerts);
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
            var countUp = k.Kind != null;
            var (respawn, source) = countUp ? (null, SpawnSource.None) : _spawnTimes.Get(zoneShort, k.Name, t.Overrides);
            rows.Add(new TimerRow
            {
                Key = key, Mob = k.Name, ZoneShort = zoneShort, ZoneText = k.Zone ?? "",
                Killed = k.Time, By = mine ? charName : k.By!, Mine = mine, Respawn = respawn, Source = source,
                CountUp = countUp,
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
                AlertLead = TimeSpan.FromSeconds(m.AlertSeconds ?? 30),
                KeepUp = m.KeepSeconds is { } ks ? TimeSpan.FromSeconds(ks) : null,
                IsCharm = m.Kind == "charm",
            });
        }

        rows = rows.Where(r => r.Due is { } due ? due + (r.KeepUp ?? keepUp) > now : now - r.Killed < UnknownKeep)
                   .OrderBy(r => r.Due ?? DateTime.MaxValue).ThenBy(r => r.Killed).ToList();

        // Forget timers that are long gone so settings don't grow forever.
        t.Manual.RemoveAll(m => m.Start + TimeSpan.FromSeconds(m.Seconds) + TimeSpan.FromHours(2) < now);
        if (t.Dismissed.Count > 500) t.Dismissed.RemoveRange(0, t.Dismissed.Count - 500);

        foreach (var r in rows)
        {
            r.Alert = alerts.Contains(r.Key);
            // Already inside the alert window when the list was built (e.g. at startup): don't beep late.
            r.Beeped = r.Due is { } d && d - now <= r.AlertLead;
            if (_rows.FirstOrDefault(o => o.Key == r.Key) is { } old) r.Beeped = old.Beeped;
            r.Tick(now);
        }
        // Alerts for timers no longer listed aren't needed.
        t.Alerts.RemoveAll(k => !rows.Any(r => r.Key == k));
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Select(r => r.Key + r.RespawnText + r.By).SequenceEqual(_rows.Select(r => r.Key + r.RespawnText + r.By)))
        {
            foreach (var (n, o) in rows.Zip(_rows)) o.Alert = n.Alert;
            return;
        }

        var selected = (List.SelectedItem as TimerRow)?.Key;
        _rows = rows;
        List.ItemsSource = rows;
        if (selected != null) List.SelectedItem = rows.FirstOrDefault(r => r.Key == selected);
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

    /// <summary>Called by the main window after it adds a timer (e.g. a Bard charm).</summary>
    public void RefreshTimers() => Rebuild();

    private void Tick()
    {
        Buffs.Tick();
        var now = DateTime.Now;
        var keepUp = TimeSpan.FromMinutes(_settings.Timers.KeepUpMinutes);
        foreach (var r in _rows)
        {
            r.Tick(now);
            if (r.Alert && !r.Beeped && r.Due is { } due && due - now <= r.AlertLead && due > now)
            {
                r.Beeped = true;
                if (r.IsCharm) Sounds.CharmEndingAlert(_settings.Sounds);
                else Sounds.TimerAlert(_settings.Sounds, r.Mob);
            }
        }
        // Drop timers whose "keep for" time ran out.
        if (_rows.Any(r => r.Due is { } due ? due + (r.KeepUp ?? keepUp) <= now : now - r.Killed >= UnknownKeep)) Rebuild();
    }

    // ------------------------------------------------------------------ UI

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Lock_Click(object sender, RoutedEventArgs e) => ApplyLock(true);

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var show = OptionsButton.IsChecked == true;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        BuffsHintText.Text = BuffsPanel.SpellDataText;
        // The panel needs some room; grow a tiny window (it stays that size).
        if (Width < 310) Width = 310;
        if (Height < 560) Height = 560;
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        var on = DetailsButton.IsChecked == true;
        _settings.Timers.Details = on;
        List.ItemTemplate = (DataTemplate)Resources[on ? "DetailsTemplate" : "CompactTemplate"];
        if (on && Width < DetailsMinWidth)
        {
            _compactWidth = Width;
            // Grow to the left if the window would run off the right edge of the screen.
            var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            if (Left + DetailsMinWidth > right) Left = Math.Max(SystemParameters.VirtualScreenLeft, right - DetailsMinWidth);
            Width = DetailsMinWidth;
        }
        else if (!on && _compactWidth is { } w)
        {
            Width = w;
            _compactWidth = null;
        }
        _settings.Save();
    }

    private void Others_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.Timers.IncludeOthers = OthersCheck.IsChecked == true;
        _settings.Save();
        Rebuild();
    }

    private void Backdrop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi) return;
        Backdrop.Opacity = _settings.Timers.Backdrop = e.NewValue;
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
        _settings.Timers.Alerts.Clear();
        _settings.Save();
        Rebuild();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Locked: the ✕/🔔 buttons still work, but rows shouldn't light up as selected.
        if (_settings.Timers.Locked && List.SelectedItem != null) { List.SelectedItem = null; return; }
        if (List.SelectedItem is not TimerRow r) return;
        MobBox.Text = r.Mob;
        TimeBox.Text = r.Respawn is { } t ? Format(t) : "";
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Remove_Click(sender, e); e.Handled = true; }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not TimerRow r) return;
        Remove(r);
        _settings.Save();
        Rebuild();
    }

    private void RowRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TimerRow r) return;
        Remove(r);
        _settings.Save();
        Rebuild();
    }

    private void RowAlert_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TimerRow r) return;
        var alerts = _settings.Timers.Alerts;
        alerts.RemoveAll(k => k == r.Key);
        if (r.Alert)
        {
            alerts.Add(r.Key);
            // Turned on inside the last 30 seconds: beep now so you know it's armed and close.
            r.Beeped = false;
        }
        _settings.Save();
        Tick();
    }


    private void Remove(TimerRow r)
    {
        if (r.IsManual) _settings.Timers.Manual.RemoveAll(m => ManualKey(m) == r.Key);
        else _settings.Timers.Dismissed.Add(r.Key);
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not TimerRow r) return;
        var respawn = r.Respawn ?? SpawnTimes.CommonDefault;
        Remove(r);
        var m = new ManualTimer { Name = r.Mob, Zone = r.ZoneShort, Start = DateTime.Now, Seconds = (int)respawn.TotalSeconds };
        _settings.Timers.Manual.Add(m);
        if (r.Alert) _settings.Timers.Alerts.Add(ManualKey(m)); // keep the 🔔 on the restarted timer
        _settings.Save();
        Rebuild();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var name = MobBox.Text.Trim();
        if (name.Length == 0) { MobBox.Focus(); return; }
        var zone = (List.SelectedItem as TimerRow) is { } sel && sel.Mob.Equals(name, StringComparison.OrdinalIgnoreCase)
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
        var zone = (List.SelectedItem as TimerRow) is { } sel && sel.Mob.Equals(name, StringComparison.OrdinalIgnoreCase)
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

    /// <summary>Snapshot hook (WHEREEQ_SNAPSHOT="out.png|timers|details" or "|options").</summary>
    public void DevShow(string what)
    {
        if (what == "details") { DetailsButton.IsChecked = true; Details_Click(this, new RoutedEventArgs()); }
        if (what == "options") { OptionsButton.IsChecked = true; OptionsButton_Click(this, new RoutedEventArgs()); }
        UpdateLayout();
    }

    // ------------------------------------------------------------------ click-through lock + hotkey

    /// <summary>Locked: the window ignores the mouse (clicks reach the game) and hides its controls.</summary>
    public void ApplyLock(bool locked)
    {
        _settings.Timers.Locked = locked;
        _settings.Save();
        TitleBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        OptionsPanel.Visibility = Visibility.Collapsed;
        OptionsButton.IsChecked = false;
        Frame.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        if (locked) { List.SelectedItem = null; Buffs.ClearSelection(); }
        Splitter.IsEnabled = !locked;

        if (_hwnd == null) return;
        var style = GetWindowLong(_hwnd.Handle, GWL_EXSTYLE);
        style = locked ? style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : style & ~WS_EX_TRANSPARENT & ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd.Handle, GWL_EXSTYLE, style);
        _clickThrough = locked;
        if (locked) _lockPoll.Start(); else _lockPoll.Stop();
    }

    // While locked the window is click-through, except the little ✕ and 🔔 on each timer: watch the cursor and
    // let the mouse in only while it's over one of them (the game keeps focus: the window never activates).
    private readonly DispatcherTimer _lockPoll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _clickThrough;

    private void UpdateClickThrough()
    {
        if (_hwnd == null || !_settings.Timers.Locked) return;
        var p = WindowDrag.CursorInDips(this);
        var local = new Point(p.X - Left, p.Y - Top);
        var overButton = false;
        if (local.X >= 0 && local.Y >= 0 && local.X < ActualWidth && local.Y < ActualHeight)
        {
            var hit = VisualTreeHelper.HitTest(this, local)?.VisualHit;
            for (DependencyObject? d = hit; d != null && d != this; d = VisualTreeHelper.GetParent(d))
                if (d is System.Windows.Controls.Primitives.ButtonBase b && b.Style == Resources["RowButton"]) { overButton = true; break; }
        }
        // Keep the mouse while a button is pressed so the click completes.
        if (!overButton && Mouse.LeftButton == MouseButtonState.Pressed && !_clickThrough) return;
        if (overButton == !_clickThrough) return;
        _clickThrough = !overButton;
        var style = GetWindowLong(_hwnd.Handle, GWL_EXSTYLE);
        style = _clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd.Handle, GWL_EXSTYLE, style);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ApplyLock(!_settings.Timers.Locked);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private const int HotkeyId = 0x5754; // "WT"
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
