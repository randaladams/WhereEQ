using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

public partial class MainWindow : Window
{
    private const string AllCharacters = "All characters";

    private readonly Settings _settings = Settings.Load();
    private readonly SpellDatabase _spellDb = new(Path.Combine(AppContext.BaseDirectory, "Data", "Spells"));
    private DataStore? _store;
    /// <summary>Visible characters (everything except hidden ones); what every tab works from.</summary>
    private List<Character> _characters = new();
    /// <summary>All characters including hidden ones (Characters tab with "Show hidden").</summary>
    private List<Character> _allCharacters = new();
    private bool _loading;

    public sealed record SpellRow(int Level, string Name, string Status);
    public sealed record SpellCharacterRow(string Name, string Summary, Character Character);
    public sealed record FriendRow(string Name, int? Level, string? Class, string? Race, string? Guild,
                                   DateTime? LastOnline, string? Zone, string FriendOf)
    {
        public string LastOnlineText => LastOnline is { } t ? Character.Ago(t) : "not seen";
    }

    public MainWindow()
    {
        InitializeComponent();
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v is { Major: > 0 } or { Minor: > 0 }) Title = $"WhereEQ {v.Major}.{v.Minor}.{v.Build}";
        RestoreWindow();
        OverrideClass.ItemsSource = new[] { "" }.Concat(EqClass.All).ToList();
        OverrideLevel.ItemsSource = new[] { "" }.Concat(Enumerable.Range(1, 60).Select(i => i.ToString())).ToList();
        TopmostCheck.IsChecked = _settings.AlwaysOnTop;
        Loaded += async (_, _) =>
        {
            if (!_settings.HasAnyFolder && Environment.GetEnvironmentVariable("WHEREEQ_SNAPSHOT") == null)
                ShowSettings(); // first run: ask where the files are
            await LoadDataAsync();
            if (_settings.Map.Open && Environment.GetEnvironmentVariable("WHEREEQ_SNAPSHOT") == null) OpenMap();
        };
        Closing += (_, _) =>
        {
            SaveWindow();
            // Remember the map was open so it comes back next time, then close it with us.
            if (_mapWindow != null) { var open = true; _mapWindow.Close(); _settings.Map.Open = open; _settings.Save(); }
            _store?.Dispose();
        };
        // Keep "5m ago" style text fresh.
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        tick.Tick += (_, _) => RefreshCharacters();
        tick.Start();
    }

    // ---------------------------------------------------------------- settings / loading

    /// <summary>Opens the settings dialog; returns true if the user saved changes.</summary>
    private bool ShowSettings()
    {
        var dlg = new SettingsWindow(_settings) { Owner = this, Topmost = Topmost };
        if (dlg.ShowDialog() != true || dlg.Result is not { } r) return false;
        _settings.InventoryFolder = r.InventoryFolder;
        _settings.SpellbookFolder = r.SpellbookFolder;
        _settings.LogFolder = r.LogFolder;
        _settings.IniFolder = r.IniFolder;
        _settings.IncludeSubfolders = r.IncludeSubfolders;
        _settings.LogServer = r.LogServer;
        _settings.AlwaysOnTop = r.AlwaysOnTop;
        _settings.BankSlots = r.BankSlots;
        _settings.MapsFolder = r.MapsFolder;
        _settings.Save();
        _mapWindow?.ReloadMaps();
        TopmostCheck.IsChecked = r.AlwaysOnTop;
        return true;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (ShowSettings()) await LoadDataAsync();
    }

    private void UpdateFolderText()
    {
        static string Short(string? f) => string.IsNullOrWhiteSpace(f) ? "(not set)" : f;
        var folders = new[] { _settings.InventoryFolder, _settings.SpellbookFolder, _settings.LogFolder };
        FolderText.Text = folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? Short(folders[0])
            : $"Inventory: {Short(_settings.InventoryFolder)}   ·   Spellbooks: {Short(_settings.SpellbookFolder)}   ·   Logs: {Short(_settings.LogFolder)}";
        FolderText.ToolTip = $"Inventory: {Short(_settings.InventoryFolder)}\nSpellbooks: {Short(_settings.SpellbookFolder)}\nLogs: {Short(_settings.LogFolder)}" +
                             (string.IsNullOrWhiteSpace(_settings.LogServer) ? "" : $"\nServer: {_settings.LogServer}");
    }

    private async Task LoadDataAsync()
    {
        _store?.Dispose();
        _store = null;
        UpdateFolderText();
        if (!_settings.HasAnyFolder)
        {
            _characters = new();
            RefreshAll();
            StatusText.Text = "No folders set. Click Settings… to choose where your inventory, spellbook and log files are.";
            return;
        }

        StatusText.Text = "Reading files… (the first read of a large log can take a few seconds)";
        _loading = true;

        var store = new DataStore(_settings, _spellDb);
        store.Changed += () => Dispatcher.InvokeAsync(RefreshAll);
        store.PositionChanged += () => Dispatcher.InvokeAsync(UpdateMap);
        _store = store;
        var sw = Stopwatch.StartNew();
        await store.StartAsync();
        _loading = false;
        RefreshAll();
        StatusText.Text += $"  ·  loaded in {sw.Elapsed.TotalSeconds:0.0}s";
        await DevSnapshotAsync();
    }

    /// <summary>
    /// Dev aid: WHEREEQ_SNAPSHOT="C:\out.png|3|Charname" renders the window (tab index, optional spell character)
    /// to a PNG and exits. Lets UI changes be checked without a screen.
    /// </summary>
    private async Task DevSnapshotAsync()
    {
        var spec = Environment.GetEnvironmentVariable("WHEREEQ_SNAPSHOT");
        if (string.IsNullOrEmpty(spec)) return;
        var parts = spec.Split('|');
        if (parts.Length > 1 && parts[1] == "map")
        {
            OpenMap();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(800);
            if (parts.Length > 2 && parts[2] == "fit") _mapWindow!.View.Follow = false;
            RenderToPng(_mapWindow!, parts[0]);
            Application.Current.Shutdown();
            return;
        }
        if (parts.Length > 1 && parts[1] is "settings" or "sync")
        {
            Window dlg = parts[1] == "sync" ? new SyncFriendsWindow(_characters) { Owner = this }
                                            : new SettingsWindow(_settings) { Owner = this };
            dlg.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(500);
            RenderToPng(dlg, parts[0]);
            Application.Current.Shutdown();
            return;
        }
        if (parts.Length > 1 && int.TryParse(parts[1], out var tab)) Tabs.SelectedIndex = tab;
        if (parts.Length > 2)
            SpellCharacters.SelectedItem = (SpellCharacters.ItemsSource as IEnumerable<SpellCharacterRow>)?.FirstOrDefault(r => r.Name == parts[2]);
        if (parts.Length > 3 && parts[3] == "missing") { ShowMissingSpells.IsChecked = true; ShowSpells(); }
        if (parts.Length > 2 && Tabs.SelectedIndex == 1)
        {
            BagsCharacterFilter.SelectedItem = parts[2];
            if (parts.Length > 3 && parts[3] == "bank") BagsInventory.IsChecked = false;
            ShowBagList();
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        RenderToPng(this, parts[0]);
        Application.Current.Shutdown();
    }

    private static void RenderToPng(Window w, string path)
    {
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        // The window's Mica backdrop isn't captured; paint a dark base so light text stays readable.
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
        bmp.Render(bg);
        bmp.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------------------------------------------------------------- map overlay

    private MapWindow? _mapWindow;

    private void Map_Click(object sender, RoutedEventArgs e)
    {
        if (_mapWindow != null) { _mapWindow.ApplyLock(false); _mapWindow.Activate(); return; }
        OpenMap();
    }

    private void OpenMap()
    {
        _mapWindow = new MapWindow(_settings);
        _mapWindow.Closed += (_, _) => _mapWindow = null;
        _settings.Map.Open = true;
        _settings.Save();
        _mapWindow.Show();
        UpdateMap();
    }

    /// <summary>Position-only update: refresh the map without rebuilding the grids.</summary>
    private void UpdateMap()
    {
        if (_mapWindow == null || _store == null) return;
        _mapWindow.Update(_store.GetCharacters().Where(c => !c.Hidden).ToList());
    }

    private void RefreshAll()
    {
        _allCharacters = _store?.GetCharacters() ?? new();
        _mapWindow?.Update(_allCharacters.Where(c => !c.Hidden).ToList());
        _characters = _allCharacters.Where(c => !c.Hidden).ToList();
        RefreshCharacterFilter();
        RunSearch();
        RefreshBags();
        RefreshCharacters();
        RefreshSpellCharacters();
        RefreshFriends();

        var inv = _characters.Count(c => c.Inventory != null);
        var books = _characters.Count(c => c.Spellbook != null);
        var logs = _characters.Count(c => c.Log != null);
        var spellNote = _spellDb.IsEmpty ? "  ·  no spell reference data installed" : $"  ·  spell lists for {_spellDb.Classes.Count} classes";
        var hidden = _allCharacters.Count - _characters.Count;
        StatusText.Text = $"{_characters.Count} characters{(hidden > 0 ? $" ({hidden} hidden)" : "")}  ·  {inv} inventories  ·  {books} spellbooks  ·  {logs} logs{spellNote}  ·  updated {DateTime.Now:HH:mm:ss}";
    }

    // ---------------------------------------------------------------- find item

    private void RefreshCharacterFilter()
    {
        var current = CharacterFilter.SelectedItem as string ?? AllCharacters;
        var names = new[] { AllCharacters }.Concat(_characters.Where(c => c.Inventory != null).Select(c => c.Name)).ToList();
        CharacterFilter.ItemsSource = names;
        CharacterFilter.SelectedItem = names.Contains(current) ? current : AllCharacters;
    }

    private void Search_Changed(object sender, RoutedEventArgs e) => RunSearch();

    private void RunSearch()
    {
        if (ResultsGrid == null) return; // during InitializeComponent
        var who = CharacterFilter.SelectedItem as string;
        var terms = SearchBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var items = _characters
            .Where(c => c.Inventory != null && (who == null || who == AllCharacters || c.Name == who))
            .SelectMany(c => c.Inventory!.Items)
            .Where(i => AreaVisible(i.Area))
            .Where(i => ShowBags.IsChecked == true || !i.IsContainer || terms.Length > 0)
            .Where(i => terms.Length == 0 || terms.Any(t => Matches(i, t)))
            .OrderBy(i => i.Name).ThenBy(i => i.Character).ThenBy(i => i.Area)
            .ToList();

        ResultsGrid.ItemsSource = items;

        if (terms.Length == 0)
        {
            SearchSummary.Text = $"{items.Count} items. Type part of an item name (or several, separated by commas). Right-click an item to search all characters for it; double-click to open it on the P1999 wiki.";
            return;
        }

        var totals = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ×{g.Sum(i => i.Count)} (" +
                         string.Join(", ", g.GroupBy(i => i.Character).Select(cg => $"{cg.Key} {cg.Sum(i => i.Count)}")) + ")")
            .Take(8);
        SearchSummary.Text = items.Count == 0 ? "Not found on any character." : string.Join("    ", totals);
    }

    private bool AreaVisible(ItemArea a) => a switch
    {
        ItemArea.Equipped => AreaEquipped.IsChecked == true,
        ItemArea.Inventory or ItemArea.Cursor => AreaInventory.IsChecked == true,
        ItemArea.Bank => AreaBank.IsChecked == true,
        _ => true,
    };

    private static bool Matches(InventoryItem i, string term) =>
        i.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        (int.TryParse(term, out var id) && i.Id == id);

    /// <summary>Right-click an item (Find item or Bags grid): search every character for it.</summary>
    private void ItemGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow) dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        var name = (dep as DataGridRow)?.Item switch
        {
            InventoryItem i => i.Name,
            BagRow b => b.ItemName,
            _ => null,
        };
        if (name == null) return;
        e.Handled = true;
        SearchAllCharacters(name);
    }

    private void SearchAllCharacters(string itemName)
    {
        CharacterFilter.SelectedItem = AllCharacters;
        AreaEquipped.IsChecked = AreaInventory.IsChecked = AreaBank.IsChecked = true;
        SearchBox.Text = itemName; // TextChanged runs the search
        RunSearch();
        Tabs.SelectedIndex = 0;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is InventoryItem i) OpenWiki(i.Name);
    }

    private static void OpenWiki(string name)
    {
        var page = Uri.EscapeDataString(name.Replace('`', '\'').Replace(' ', '_')).Replace("%27", "'");
        Process.Start(new ProcessStartInfo($"https://wiki.project1999.com/{page}") { UseShellExecute = true });
    }

    // ---------------------------------------------------------------- bags

    private void RefreshBags()
    {
        var current = BagsCharacterFilter.SelectedItem as string ?? AllCharacters;
        var names = new[] { AllCharacters }.Concat(_characters.Where(c => c.Inventory != null).Select(c => c.Name)).ToList();
        _refreshingBags = true;
        BagsCharacterFilter.ItemsSource = names;
        BagsCharacterFilter.SelectedItem = names.Contains(current) ? current : AllCharacters;
        _refreshingBags = false;
        ShowBagList();
    }

    private bool _refreshingBags;

    private void BagsFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_refreshingBags) ShowBagList();
    }

    /// <summary>A row on the Bags tab: an inventory bag, or any bank slot (bag, plain item or empty).</summary>
    public sealed record BagRow(string Character, string? ItemName, string AreaText, string Where,
                                int? Slots, int? Used, int? Free, int Order, string Status)
    {
        /// <summary>"None" (red), "Small" (yellow), "Full" (green), or "" for inventory rows.</summary>
        public string BagText => ItemName ?? "(empty)";
        public string StatusText => Status switch
        {
            "None" => ItemName == null ? "No bag" : "Not a bag",
            "Small" => $"{Slots}-slot bag",
            "Full" => "10-slot bag",
            _ => "",
        };
    }

    private const int BestBagSize = 10;

    private void ShowBagList()
    {
        if (BagsGrid == null) return; // during InitializeComponent
        var who = BagsCharacterFilter.SelectedItem as string;
        var rows = new List<BagRow>();
        foreach (var c in _characters.Where(c => c.Inventory != null && (who == null || who == AllCharacters || c.Name == who)))
        {
            if (BagsInventory.IsChecked == true)
                rows.AddRange(c.Inventory!.GeneralSlots.Select(s => SlotRow(c.Name, s, "Inventory", $"Inventory slot {s.Number}", 0)));
            if (BagsBank.IsChecked == true)
                rows.AddRange(c.Inventory!.BankSlots.Select(s => SlotRow(c.Name, s, "Bank", $"Bank {s.Number}", 100)));
        }
        rows = rows.OrderBy(r => r.Character).ThenBy(r => r.Order).ToList();
        BagsGrid.ItemsSource = rows;

        if (rows.Count == 0) { BagsSummary.Text = "No bags found."; return; }
        var perChar = rows.GroupBy(r => r.Character).Select(g =>
        {
            var bags = g.Where(r => r.Slots != null).ToList();
            string Tally(string area)
            {
                var a = g.Where(r => r.AreaText == area).ToList();
                return a.Count == 0 ? "" :
                    $"{area.ToLowerInvariant()} {a.Count(r => r.Status == "Full")}/{a.Count(r => r.Status == "Small")}/{a.Count(r => r.Status == "None")}";
            }
            var tallies = string.Join(", ", new[] { Tally("Inventory"), Tally("Bank") }.Where(t => t.Length > 0));
            return $"{g.Key}: {bags.Sum(b => b.Free)} of {bags.Sum(b => b.Slots)} bag slots free ({tallies})";
        });
        BagsSummary.Text = string.Join("    ·    ", perChar) +
                           ".  Counts are 10-slot / smaller / no bag.  Right-click a bag to find it on every character; double-click to open it on the P1999 wiki.";
    }

    private static BagRow SlotRow(string character, BankSlot s, string area, string where, int orderBase) => new(
        character, s.Item, area, where,
        s.IsBag ? s.Slots : null, s.IsBag ? s.Slots - s.Free : null, s.IsBag ? s.Free : null,
        orderBase + s.Number,
        !s.IsBag ? "None" : s.Slots >= BestBagSize ? "Full" : "Small");

    private static int SlotNumber(string rawLocation) =>
        int.TryParse(new string(rawLocation.Where(char.IsDigit).ToArray()), out var n) ? n : 0;

    private void Bags_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BagsGrid.SelectedItem is BagRow { ItemName: { } name }) OpenWiki(name);
    }

    // ---------------------------------------------------------------- characters

    private void RefreshCharacters()
    {
        if (_loading) return;
        var selected = SelectedCharacters().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = (ShowHiddenCheck.IsChecked == true ? _allCharacters : _characters).ToList();
        CharactersGrid.ItemsSource = list;
        foreach (var c in list.Where(c => selected.Contains(c.Name))) CharactersGrid.SelectedItems.Add(c);
        var hidden = _allCharacters.Count(c => c.Hidden);
        UnhideButton.IsEnabled = hidden > 0;
        ShowHiddenCheck.Content = hidden > 0 ? $"Show hidden ({hidden})" : "Show hidden";
    }

    private List<Character> SelectedCharacters() => CharactersGrid.SelectedItems.OfType<Character>().ToList();

    private void ShowHidden_Click(object sender, RoutedEventArgs e) => RefreshCharacters();

    private void HideCharacters_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedCharacters().Where(c => !c.Hidden).ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select one or more characters to hide.", "WhereEQ"); return; }
        foreach (var c in sel)
            if (!_settings.HiddenCharacters.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                _settings.HiddenCharacters.Add(c.Name);
        _settings.Save();
        RefreshAll();
    }

    private void UnhideCharacters_Click(object sender, RoutedEventArgs e)
    {
        // Unhide the selected hidden characters; with none selected, offer to unhide all.
        var sel = SelectedCharacters().Where(c => c.Hidden).Select(c => c.Name).ToList();
        if (sel.Count == 0)
        {
            if (_settings.HiddenCharacters.Count == 0) return;
            if (MessageBox.Show(this, $"Unhide all {_settings.HiddenCharacters.Count} hidden characters?\n\n" +
                                      string.Join(", ", _settings.HiddenCharacters), "WhereEQ",
                                MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            sel = _settings.HiddenCharacters.ToList();
        }
        _settings.HiddenCharacters.RemoveAll(h => sel.Contains(h, StringComparer.OrdinalIgnoreCase));
        _settings.Save();
        RefreshAll();
    }

    private void DeleteCharacterFiles_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedCharacters();
        var files = sel.SelectMany(c => c.Files.Select(f => (c.Name, f))).Where(x => File.Exists(x.f)).ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select one or more characters first.", "WhereEQ"); return; }
        if (files.Count == 0) { MessageBox.Show(this, "No files found for the selected characters.", "WhereEQ"); return; }

        var list = string.Join("\n", files.Select(x => $"  {x.Name}:  {x.f}"));
        var msg = $"Send these {files.Count} file(s) to the Recycle Bin?\n\n{list}\n\n" +
                  "You can restore them from the Recycle Bin. Log files hold that character's chat history.";
        if (MessageBox.Show(this, msg, "WhereEQ: delete character files", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        var failed = new List<string>();
        foreach (var (_, f) in files)
            if (!RecycleBin.Send(f)) failed.Add(f);
        if (failed.Count > 0)
            MessageBox.Show(this, "These files couldn't be moved (EverQuest may have them open):\n\n" + string.Join("\n", failed),
                            "WhereEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
        // The folder watcher drops the removed files; refresh now for anything it missed.
        _ = LoadDataAsync();
    }

    private void CharactersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CharactersGrid.SelectedItems.Count != 1 || CharactersGrid.SelectedItem is not Character c) return;
        OverrideName.Text = c.Name;
        OverrideClass.SelectedItem = c.Override?.Class ?? "";
        OverrideLevel.SelectedItem = c.Override?.Level?.ToString() ?? "";
    }

    private void OverrideSave_Click(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not Character c) return;
        int? level = int.TryParse(OverrideLevel.SelectedItem as string, out var l) ? l : null;
        var cls = OverrideClass.SelectedItem as string;
        _settings.Overrides[c.Name] = new CharacterOverride { Class = string.IsNullOrEmpty(cls) ? null : cls, Level = level };
        _settings.Save();
        RefreshAll();
    }

    private void OverrideClear_Click(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not Character c) return;
        _settings.Overrides.Remove(c.Name);
        _settings.Save();
        OverrideClass.SelectedItem = "";
        OverrideLevel.SelectedItem = "";
        RefreshAll();
    }

    // ---------------------------------------------------------------- friends

    private async void SyncFriends_Click(object sender, RoutedEventArgs e)
    {
        if (!_characters.Any(c => c.IniPath != null))
        {
            MessageBox.Show(this, "No character .ini files found. Check the \"Character .ini files\" folder in Settings " +
                                  "(normally your EverQuest folder, where Name_server.ini files live).", "WhereEQ");
            return;
        }
        var dlg = new SyncFriendsWindow(_characters) { Owner = this, Topmost = Topmost };
        if (dlg.ShowDialog() == true)
        {
            await LoadDataAsync();
            FriendsSummary.Text = $"Updated {dlg.Updated} friends list(s). " + FriendsSummary.Text;
        }
    }

    private void RefreshFriends()
    {
        // Who is a friend: each character's Name_server.ini [Friends] list, or its log when there's no .ini.
        // What we know about them: the most recent sighting in any character's log (/friends or /who).
        var sightings = _characters
            .SelectMany(c => c.Log?.Friends ?? new List<FriendInfo>())
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.LastOnline ?? DateTime.MinValue).First(),
                          StringComparer.OrdinalIgnoreCase);

        var withFriends = _characters.Where(c => c.FriendNames.Any()).ToList();
        var rows = withFriends
            .SelectMany(c => c.FriendNames.Select(name => (owner: c.Name, name)))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                sightings.TryGetValue(g.Key, out var s);
                var owners = string.Join(", ", g.Select(x => x.owner).Distinct());
                return new FriendRow(s?.Name ?? g.First().name, s?.Level, s?.Class, s?.Race, s?.Guild,
                                     s?.LastOnline, s?.Zone, owners);
            })
            .OrderByDescending(r => r.LastOnline ?? DateTime.MinValue).ThenBy(r => r.Name)
            .ToList();
        FriendsGrid.ItemsSource = rows;

        var fromIni = _characters.Count(c => c.IniFriends != null);
        var fromLog = withFriends.Count(c => c.IniFriends == null);
        var unknown = _characters.Count(c => c.FriendsSource == "");
        FriendsSummary.Text = rows.Count == 0
            ? "No friends found yet. Friends come from each character's Name_server.ini in the EverQuest folder, or from /friend and /friends in the logs."
            : $"{rows.Count} friends · lists from {fromIni} .ini file(s)" + (fromLog > 0 ? $" and {fromLog} log(s)" : "") +
              ". Level, class and zone come from the last /friends or /who that listed them." +
              (unknown > 0 ? $"  {unknown} character(s) have no .ini or log friends." : "");
    }

    // ---------------------------------------------------------------- spells

    private void RefreshSpellCharacters()
    {
        var selected = (SpellCharacters.SelectedItem as SpellCharacterRow)?.Name;
        var rows = _characters
            .Where(c => c.Spellbook != null || (c.Class != null && EqClass.CastsSpells(c.Class)))
            .Select(c => new SpellCharacterRow(c.Name, SpellSummary(c), c))
            .ToList();
        SpellCharacters.ItemsSource = rows;
        SpellCharacters.SelectedItem = rows.FirstOrDefault(r => r.Name == selected) ?? rows.FirstOrDefault();
    }

    private string SpellSummary(Character c)
    {
        var head = $"{c.ClassText} {c.LevelText}";
        if (c.Spellbook == null) return $"{head} · no spellbook file";
        if (c.Class == null || c.Level == null || _spellDb.For(c.Class).Count == 0) return $"{head} · {c.Spellbook.Count} spells";
        var missing = BuildSpellRows(c).Count(r => r.Status == "Missing");
        return missing == 0 ? $"{head} · complete" : $"{head} · {missing} missing";
    }

    private void SpellCharacters_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSpells();
    private void SpellFilter_Click(object sender, RoutedEventArgs e) => ShowSpells();

    private void ShowSpells()
    {
        if (SpellCharacters.SelectedItem is not SpellCharacterRow row)
        {
            SpellHeader.Text = "";
            SpellSubHeader.Text = "No characters with spellbooks yet. In game, type /outputfile spellbook.";
            SpellsGrid.ItemsSource = null;
            return;
        }
        var c = row.Character;
        SpellHeader.Text = $"{c.Name} — {c.ClassText} {c.LevelText}";

        if (c.Spellbook == null)
        {
            SpellSubHeader.Text = $"No spellbook file for {c.Name}. Log in and type /outputfile spellbook.";
            SpellsGrid.ItemsSource = null;
            return;
        }
        if (c.Class == null)
        {
            SpellSubHeader.Text = "Class unknown. Set it on the Characters tab, or /who yourself in game so it shows in the log.";
            SpellsGrid.ItemsSource = c.Spellbook.Select(s => new SpellRow(s.Level, s.Name, "In book")).ToList();
            return;
        }
        if (_spellDb.For(c.Class).Count == 0)
        {
            SpellSubHeader.Text = $"No reference spell list for {c.Class} (expected Data\\Spells\\{c.Class}.txt). Showing the spellbook only.";
            SpellsGrid.ItemsSource = c.Spellbook.Select(s => new SpellRow(s.Level, s.Name, "In book")).ToList();
            return;
        }

        var all = BuildSpellRows(c);
        var missing = all.Count(r => r.Status == "Missing");
        var eligible = all.Count(r => r.Status is "Missing" or "Have");
        SpellSubHeader.Text =
            $"Level from {c.LevelSource}, class from {c.ClassSource}.  " +
            (missing == 0 ? $"All {eligible} spells up to level {c.Level} are scribed." : $"Missing {missing} of {eligible} spells up to level {c.Level}.") +
            $"  Spellbook saved {c.SpellbookAge}. Double-click a spell to open the wiki.";

        SpellsGrid.ItemsSource = all
            .Where(r => ShowAllSpells.IsChecked == true || r.Status is "Missing" or "Future")
            .Where(r => r.Status != "Future" || ShowFuture.IsChecked == true)
            .ToList();
    }

    private List<SpellRow> BuildSpellRows(Character c)
    {
        var book = c.Spellbook!.Select(s => FileUtil.NormalizeSpellName(s.Name)).ToHashSet();
        var reference = _spellDb.For(c.Class!);
        var level = c.Level ?? 0;
        var rows = reference.Select(s =>
        {
            var have = book.Contains(FileUtil.NormalizeSpellName(s.Name));
            var status = have ? "Have" : s.Level <= level ? "Missing" : "Future";
            return new SpellRow(s.Level, s.Name, status);
        }).ToList();

        // Spells in the book that the reference list doesn't know about (data gaps or name mismatches).
        var known = reference.Select(s => FileUtil.NormalizeSpellName(s.Name)).ToHashSet();
        rows.AddRange(c.Spellbook!.Where(s => !known.Contains(FileUtil.NormalizeSpellName(s.Name)))
                                  .Select(s => new SpellRow(s.Level, s.Name, "Not in list")));
        return rows.OrderBy(r => r.Level).ThenBy(r => r.Name).ToList();
    }

    private void Spells_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SpellsGrid.SelectedItem is SpellRow r) OpenWiki(r.Name);
    }

    // ---------------------------------------------------------------- window

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        _settings.Save();
    }

    private void RestoreWindow()
    {
        if (_settings.WindowWidth is > 200 && _settings.WindowHeight is > 200)
        {
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
        }
        if (_settings.WindowLeft is { } l && _settings.WindowTop is { } t &&
            l > SystemParameters.VirtualScreenLeft - 50 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SaveWindow()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.Save();
    }
}
