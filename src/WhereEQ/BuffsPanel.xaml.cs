using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Buffs list (bottom half of the Timers window): spells on you (from their "lands on you" log lines, ended by
/// their "wears off" lines) and, optionally, spells you cast on others, each with a countdown and a draining bar.
/// Durations are estimates from the client's spells_us.txt (see LogScanner.TrackSpells).
/// </summary>
public partial class BuffsPanel : UserControl
{
    private static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x2E, 0x9D, 0x55));
    private static readonly Brush BadBrush = Frozen(Color.FromRgb(0xC6, 0x3B, 0x3B));
    private static readonly Brush OtherBrush = Frozen(Color.FromRgb(0x3B, 0x6E, 0xC6));
    public const string ActiveChoice = "(active character)";

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

    private Settings? _settings;
    private List<Character> _characters = new();
    private List<BuffRow> _rows = new();
    /// <summary>Spells removed by hand (until they're cast again: the key includes the start time).</summary>
    private readonly HashSet<string> _dismissed = new();

    public BuffsPanel() => InitializeComponent();

    public void Init(Settings settings) => _settings = settings;

    /// <summary>Characters with a log, for the options' character picker (first entry = follow the active one).</summary>
    public List<string> CharacterChoices =>
        new[] { ActiveChoice }.Concat(_characters.Where(c => c.Log != null).Select(c => c.Name)).ToList();

    public void Update(List<Character> characters)
    {
        _characters = characters;
        Rebuild();
    }

    /// <summary>Forget the current rows (character or option changed) and rebuild.</summary>
    public void Reset()
    {
        _rows.Clear();
        Rebuild();
    }

    private Character? Shown() =>
        _settings?.Buffs.Character is { } name
            ? _characters.FirstOrDefault(c => c.Log != null && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : _characters.Where(c => c.Log != null).OrderByDescending(c => c.Log!.LastLine ?? DateTime.MinValue).FirstOrDefault();

    public void Rebuild()
    {
        if (_settings == null) return;
        var now = DateTime.Now;
        var who = Shown();
        var spells = who?.Log is { } log ? SafeSpells(log) : Array.Empty<ActiveSpell>();
        var live = spells.Where(s => s.End > now && !_dismissed.Contains(KeyOf(s))).ToList();

        var rows = new List<BuffRow>();
        var onYou = live.Where(s => s.Target == null).OrderBy(s => s.End).ToList();
        var onOthers = _settings.Buffs.ShowOnOthers ? live.Where(s => s.Target != null).OrderBy(s => s.End).ToList() : new();
        if (onYou.Count > 0 || onOthers.Count > 0)
            rows.Add(new BuffRow { IsHeader = true, Header = $"On {who!.Name}" + (onYou.Count == 0 ? ": nothing" : "") });
        rows.AddRange(onYou.Select(Row));
        if (onOthers.Count > 0)
        {
            rows.Add(new BuffRow { IsHeader = true, Header = "Your spells on others" });
            rows.AddRange(onOthers.Select(Row));
        }
        foreach (var r in rows) r.Tick(now);

        EmptyText.Text = LogScanner.Effects.IsEmpty
            ? "Buffs: spells_us.txt not found. It's in your EverQuest folder: point Settings → Inventory files at that folder."
            : who == null ? "Buffs: no logs yet. Turn logging on in game with /log on."
            : $"No buffs on {who.Name}. They show up here when spells land on you.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Select(r => r.Key + r.Header).SequenceEqual(_rows.Select(r => r.Key + r.Header))) return;
        _rows = rows;
        List.ItemsSource = rows;
    }

    /// <summary>Spell file status for the options panel.</summary>
    public static string SpellDataText => LogScanner.Effects.IsEmpty
        ? "Buffs: spells_us.txt wasn't found in the EverQuest folder, so buffs can't be tracked."
        : $"Buffs: spell data from {LogScanner.Effects.Path} ({LogScanner.Effects.Count} spells). Times are estimates " +
          "(others' buffs assume the caster is at least your level). Right-click a buff to remove it.";

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

    /// <summary>Every second (from the Timers window's clock).</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        foreach (var r in _rows) r.Tick(now);
        if (_rows.Any(r => !r.IsHeader && r.End <= now)) Rebuild();
    }

    public void ClearSelection() => List.SelectedItem = null;

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
}
