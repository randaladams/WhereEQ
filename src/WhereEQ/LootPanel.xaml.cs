using System.Windows;
using System.Windows.Controls;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Loot list (bottom of the Timers &amp; buffs window): what your characters looted in the last two hours, newest
/// first, each with its pigparse.org average asking price (gold at or above the valuable-loot threshold).
/// </summary>
public partial class LootPanel : UserControl
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(2);
    private const int MaxRows = 40;

    public sealed record LootRow(string Key, string Item, string PriceText, int Price, bool Valuable, string AgoText, string Tip)
    {
        public bool Priced => Price > 0;
    }

    private Settings? _settings;
    private List<Character> _characters = new();
    private List<LootRow> _rows = new();
    private DateTime _lastRebuild;
    private bool _redrawQueued;

    public LootPanel()
    {
        InitializeComponent();
        // Prices arrive on a thread-pool thread; redraw once on the UI thread.
        PriceLookup.Updated += () =>
        {
            if (_redrawQueued) return;
            _redrawQueued = true;
            Dispatcher.BeginInvoke(() => { _redrawQueued = false; Rebuild(); });
        };
    }

    public void Init(Settings settings) => _settings = settings;

    public void Update(List<Character> characters)
    {
        _characters = characters;
        Rebuild();
    }

    /// <summary>Every second from the window's clock: refresh "5m ago" now and then.</summary>
    public void Tick()
    {
        if (DateTime.Now - _lastRebuild > TimeSpan.FromSeconds(30)) Rebuild();
    }

    public void Rebuild()
    {
        if (_settings == null) return;
        _lastRebuild = DateTime.Now;
        var now = DateTime.Now;
        var since = now - Window;
        if (_settings.Timers.LootClearedBefore is { } cb && cb > since) since = cb;
        var min = _settings.Sounds.LootMinPp;
        var several = _characters.Count(c => c.Log?.Loot.Count > 0) > 1;

        var loot = _characters.Where(c => c.Log != null)
            .SelectMany(c => SafeLoot(c.Log!).Select(l => (Char: c.Name, Server: PriceLookup.ServerName(c.Log!.Server), Loot: l)))
            .Where(x => x.Loot.Time > since)
            .OrderByDescending(x => x.Loot.Time)
            .Take(MaxRows);

        var rows = new List<LootRow>();
        foreach (var (who, server, l) in loot)
        {
            string priceText, tip;
            var price = 0;
            var valuable = false;
            if (!_settings.LootPrices)
            {
                priceText = "";
                tip = l.Item;
            }
            else if (PriceLookup.TryGetCached(server, l.Item, out var info))
            {
                priceText = info?.Text ?? "no price";
                price = info?.Price ?? 0;
                valuable = info?.Price >= min;
                tip = info?.Tip ?? $"{l.Item}: no price";
            }
            else
            {
                PriceLookup.Prefetch(server, l.Item);
                priceText = "…";
                tip = $"{l.Item}: looking up the price on pigparse.org";
            }
            tip += $"\nLooted {l.Time:HH:mm}{(several ? " by " + who : "")}{(l.Zone != null ? " in " + l.Zone : "")}";
            rows.Add(new LootRow($"{who}|{l.Time.Ticks}|{l.Item}", l.Item, priceText, price, valuable, Character.Ago(l.Time), tip));
        }

        EmptyText.Text = _settings.LootPrices
            ? "Nothing looted in the last 2 hours. Loot shows here with its pigparse.org price."
            : "Nothing looted in the last 2 hours. (Price lookups are off in Settings → Options.)";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var total = rows.Sum(r => r.Price);
        HeaderText.Text = rows.Count == 0 ? "Loot" : $"Loot · {rows.Count} item{(rows.Count == 1 ? "" : "s")}" +
                                                    (total > 0 ? $" · about {total:N0} pp" : "");
        if (rows.SequenceEqual(_rows)) return;
        _rows = rows;
        List.ItemsSource = rows;
    }

    private static LootRecord[] SafeLoot(LogState log)
    {
        for (var i = 0; i < 3; i++)
        {
            try { return log.Loot.ToArray(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return Array.Empty<LootRecord>();
    }

    public void ClearSelection() => List.SelectedItem = null;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        _settings.Timers.LootClearedBefore = DateTime.Now;
        _settings.Save();
        Rebuild();
    }
}
