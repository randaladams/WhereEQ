using System.ComponentModel;
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
/// Buffs overlay: spells on you (from their "lands on you" log lines, ended by their "wears off" lines) and,
/// optionally, spells you cast on others, each with a countdown and a draining bar. Durations are estimates from the
/// client's spells_us.txt. Transparent like the map and timers; Ctrl+Alt+B toggles click-through.
/// </summary>
public partial class BuffsWindow : Window
{
    private static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x2E, 0x9D, 0x55));
    private static readonly Brush BadBrush = Frozen(Color.FromRgb(0xC6, 0x3B, 0x3B));
    private static readonly Brush OtherBrush = Frozen(Color.FromRgb(0x3B, 0x6E, 0xC6));
    private const string ActiveChoice = "(active character)";

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public sealed class BuffRow : INotifyPropertyChanged
    {
        public bool IsHeader { get; init; }
        public string Header { get; init; } = "";
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string Tip { get; init; } = "";
        public Brush? BarBrush { get; init; }
        public DateTime Start { get; init; }
        public DateTime End { get; init; }

        public string LeftText { get; private set; } = "";
        public double Fraction { get; private set; }
        /// <summary>Under 30 seconds left.</summary>
        public bool Soon { get; private set; }

        public void Tick(DateTime now)
        {
            if (IsHeader) return;
            var left = End - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            var total = (End - Start).TotalSeconds;
            var text = TimersWindow.Format(left + TimeSpan.FromSeconds(0.999));
            var frac = total > 0 ? Math.Clamp(left.TotalSeconds / total, 0, 1) : 0;
            var soon = left.TotalSeconds <= 30;
            if (text != LeftText) { LeftText = text; Changed(nameof(LeftText)); }
            if (Math.Abs(frac - Fraction) > 0.002) { Fraction = frac; Changed(nameof(Fraction)); }
            if (soon != Soon) { Soon = soon; Changed(nameof(Soon)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly Settings _settings;
    private List<Character> _characters = new();
    private List<BuffRow> _rows = new();
    /// <summary>Spells removed by hand (until they're cast again: the key includes the start time).</summary>
    private readonly HashSet<string> _dismissed = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _updatingUi = true;
    private HwndSource? _hwnd;

    public BuffsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        var b = settings.Buffs;
        Width = b.Width;
        Height = b.Height;
        if (b.Left is { } l && b.Top is { } top) { Left = l; Top = top; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        OthersCheck.IsChecked = b.ShowOnOthers;
        BackdropSlider.Value = b.Backdrop;
        Backdrop.Opacity = b.Backdrop;
        _updatingUi = false;

        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        WindowDrag.Attach((UIElement)Content, this, anywhere: true, canDrag: () => !_settings.Buffs.Locked);
        Loaded += (_, _) => WindowDrag.KeepOnScreen(this);
        SourceInitialized += (_, _) =>
        {
            _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwnd?.AddHook(WndProc);
            RegisterHotKey(_hwnd!.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.B));
            ApplyLock(_settings.Buffs.Locked);
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            if (_hwnd != null) UnregisterHotKey(_hwnd.Handle, HotkeyId);
            SaveState();
        };
        LocationChanged += (_, _) => SaveState();
        SizeChanged += (_, _) => SaveState();
    }

    private void SaveState()
    {
        var b = _settings.Buffs;
        if (WindowState == WindowState.Normal && IsLoaded) { b.Left = Left; b.Top = Top; b.Width = Width; b.Height = Height; }
        _settings.Save();
    }

    // ------------------------------------------------------------------ data

    /// <summary>Called by the main window whenever logs change.</summary>
    public void Update(List<Character> characters)
    {
        _characters = characters;
        RefreshCharacterBox();
        Rebuild();
    }

    private void RefreshCharacterBox()
    {
        var names = new[] { ActiveChoice }.Concat(_characters.Where(c => c.Log != null).Select(c => c.Name)).ToList();
        if (CharacterBox.ItemsSource is List<string> old && old.SequenceEqual(names)) return;
        _updatingUi = true;
        CharacterBox.ItemsSource = names;
        CharacterBox.SelectedItem = _settings.Buffs.Character is { } c && names.Contains(c) ? c : ActiveChoice;
        _updatingUi = false;
    }

    private Character? Shown() =>
        _settings.Buffs.Character is { } name
            ? _characters.FirstOrDefault(c => c.Log != null && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : _characters.Where(c => c.Log != null).OrderByDescending(c => c.Log!.LastLine ?? DateTime.MinValue).FirstOrDefault();

    private void Rebuild()
    {
        var now = DateTime.Now;
        var who = Shown();
        var spells = who?.Log is { } log ? SafeSpells(log) : Array.Empty<ActiveSpell>();
        var live = spells.Where(s => s.End > now && !_dismissed.Contains(KeyOf(s))).ToList();

        var rows = new List<BuffRow>();
        var onYou = live.Where(s => s.Target == null).OrderBy(s => s.End).ToList();
        var onOthers = _settings.Buffs.ShowOnOthers ? live.Where(s => s.Target != null).OrderBy(s => s.End).ToList() : new();
        if (onYou.Count > 0 || onOthers.Count > 0)
            rows.Add(new BuffRow { IsHeader = true, Header = $"On {who!.Name}" + (onYou.Count == 0 ? ": nothing" : "") });
        rows.AddRange(onYou.Select(s => Row(s)));
        if (onOthers.Count > 0)
        {
            rows.Add(new BuffRow { IsHeader = true, Header = "Your spells on others" });
            rows.AddRange(onOthers.Select(s => Row(s)));
        }
        foreach (var r in rows) r.Tick(now);

        EmptyText.Text = LogScanner.Effects.IsEmpty
            ? "spells_us.txt not found. It's in your EverQuest folder: point Settings → Inventory files at that folder."
            : who == null ? "No logs yet. Turn logging on in game with /log on."
            : $"No spells on {who.Name}. Buffs show up here when they land on you.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = LogScanner.Effects.IsEmpty
            ? "No spell data: spells_us.txt wasn't found in the EverQuest folder."
            : $"Spell data: {LogScanner.Effects.Path} ({LogScanner.Effects.Count} spells). Times are estimates from the " +
              "spell file (others' buffs assume the caster is at least your level). Right-click a spell to remove it.";

        if (rows.Select(r => r.Key + r.Header).SequenceEqual(_rows.Select(r => r.Key + r.Header))) return;
        _rows = rows;
        List.ItemsSource = rows;
    }

    private static BuffRow Row(ActiveSpell s)
    {
        var label = s.Target == null ? s.Name : $"{s.Name} → {s.Target}";
        var tip = $"{s.Name}{(s.Target != null ? " on " + s.Target : "")}\n" +
                  $"Landed {s.Start:HH:mm:ss}, about {TimersWindow.Format(TimeSpan.FromSeconds(s.Seconds))}" +
                  (s.ByYou ? " (you cast it)" : "") +
                  (s.Others != null ? $"\nSame message as: {s.Others}" : "");
        return new BuffRow
        {
            Key = KeyOf(s), Label = label, Tip = tip, Start = s.Start, End = s.End,
            BarBrush = s.Target != null ? OtherBrush : s.Beneficial ? GoodBrush : BadBrush,
        };
    }

    private static string KeyOf(ActiveSpell s) => $"{s.Name}|{s.Target}|{s.Start.Ticks}";

    private static ActiveSpell[] SafeSpells(LogState log)
    {
        // The log reader changes the list on a background thread; retry if we catch it mid-update.
        for (var i = 0; i < 3; i++)
        {
            try { return log.Spells.ToArray(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return Array.Empty<ActiveSpell>();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var r in _rows) r.Tick(now);
        if (_rows.Any(r => !r.IsHeader && r.End <= now)) Rebuild();
    }

    // ------------------------------------------------------------------ UI

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Lock_Click(object sender, RoutedEventArgs e) => ApplyLock(true);

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var show = OptionsButton.IsChecked == true;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        if (Width < 300) Width = 300;
        if (Height < 300) Height = 300;
    }

    private void Character_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || CharacterBox.SelectedItem is not string s) return;
        _settings.Buffs.Character = s == ActiveChoice ? null : s;
        _settings.Save();
        _rows.Clear();
        Rebuild();
    }

    private void Others_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.Buffs.ShowOnOthers = OthersCheck.IsChecked == true;
        _settings.Save();
        Rebuild();
    }

    private void Backdrop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi) return;
        Backdrop.Opacity = _settings.Buffs.Backdrop = e.NewValue;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Remove_Click(sender, e); e.Handled = true; }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not BuffRow { IsHeader: false } r) return;
        _dismissed.Add(r.Key);
        Rebuild();
    }

    /// <summary>Snapshot hook (WHEREEQ_SNAPSHOT="out.png|buffs" or "|buffs|options").</summary>
    public void DevShow(string what)
    {
        if (what == "options") { OptionsButton.IsChecked = true; OptionsButton_Click(this, new RoutedEventArgs()); }
        UpdateLayout();
    }

    // ------------------------------------------------------------------ click-through lock + hotkey

    public void ApplyLock(bool locked)
    {
        _settings.Buffs.Locked = locked;
        _settings.Save();
        TitleBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        OptionsPanel.Visibility = Visibility.Collapsed;
        OptionsButton.IsChecked = false;
        Frame.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        if (locked) List.SelectedItem = null;
        if (_hwnd == null) return;
        var style = GetWindowLong(_hwnd.Handle, GWL_EXSTYLE);
        style = locked ? style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : style & ~WS_EX_TRANSPARENT & ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd.Handle, GWL_EXSTYLE, style);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ApplyLock(!_settings.Buffs.Locked);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private const int HotkeyId = 0x5742; // "WB"
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
