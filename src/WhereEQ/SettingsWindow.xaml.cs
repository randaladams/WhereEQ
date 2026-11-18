using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>Edits a copy of <see cref="Settings"/>; <see cref="Result"/> is set when the user saves.</summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _original;
    private bool _ready;

    public Settings? Result { get; private set; }

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _original = settings;
        InventoryBox.Text = settings.InventoryFolder ?? "";
        SpellbookBox.Text = settings.SpellbookFolder ?? "";
        LogBox.Text = settings.LogFolder ?? "";
        IniBox.Text = settings.IniFolder ?? "";
        MapsBox.Text = settings.MapsFolder ?? "";
        SubfoldersCheck.IsChecked = settings.IncludeSubfolders;
        ServerBox.Text = settings.LogServer ?? "";
        TopmostCheck.IsChecked = settings.AlwaysOnTop;
        SoundFallCheck.IsChecked = settings.Sounds.Fall;
        SoundStandCheck.IsChecked = settings.Sounds.Stand;
        SoundTellCheck.IsChecked = settings.Sounds.Tell;
        BankSlotsBox.ItemsSource = Enumerable.Range(1, 16).ToList();
        BankSlotsBox.SelectedItem = Math.Clamp(settings.BankSlots, 1, 16);
        _ready = true;
        UpdateInfo();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TextBox box) return;
        var start = Directory.Exists(box.Text) ? box.Text
            : new[] { InventoryBox.Text, SpellbookBox.Text, LogBox.Text }.FirstOrDefault(Directory.Exists)
              ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dlg = new OpenFolderDialog { Title = "Choose folder", InitialDirectory = start };
        if (dlg.ShowDialog(this) != true) return;
        box.Text = dlg.FolderName;

        // Convenience: the first folder picked also fills any empty ones (they're often the same EQ folder).
        // The .ini box is left alone: blank already means "same as inventory".
        foreach (var other in new[] { InventoryBox, SpellbookBox, LogBox })
            if (other != box && string.IsNullOrWhiteSpace(other.Text)) other.Text = dlg.FolderName;
    }

    private void Folder_Changed(object sender, RoutedEventArgs e) => UpdateInfo();

    private void UpdateInfo()
    {
        if (!_ready) return;
        var sub = SubfoldersCheck.IsChecked == true;
        InventoryInfo.Text = Describe(InventoryBox.Text, "*-Inventory.txt", "inventory file", sub, "In game: /outputfile inventory");
        SpellbookInfo.Text = Describe(SpellbookBox.Text, "*-Spellbook.txt", "spellbook file", sub, "In game: /outputfile spellbook");
        LogInfo.Text = Describe(LogBox.Text, "eqlog_*.txt", "log file", sub, "In game: /log on (logs go to EverQuest\\Logs)");
        var iniFolder = string.IsNullOrWhiteSpace(IniBox.Text) ? InventoryBox.Text : IniBox.Text;
        var iniPrefix = string.IsNullOrWhiteSpace(IniBox.Text) ? "Using the inventory folder. " : "";
        IniInfo.Text = iniPrefix + Describe(iniFolder, "*_*.ini", "character .ini file", sub,
            "These are Name_server.ini in the EverQuest folder (they hold friends lists).",
            p => CharacterIniParser.TryParseFileName(p, out _, out _));

        var mapsGuess = string.IsNullOrWhiteSpace(InventoryBox.Text) ? null : Path.Combine(InventoryBox.Text, "maps");
        var mapsFolder = string.IsNullOrWhiteSpace(MapsBox.Text) ? mapsGuess : MapsBox.Text;
        var mapsPrefix = string.IsNullOrWhiteSpace(MapsBox.Text) ? "Using 'maps' under the inventory folder. " : "";
        var maps = MapData.Available(mapsFolder);
        var builtIn = MapData.Available(MapData.BuiltInFolder).Count;
        var fallback = $" Built-in maps ({builtIn} zones) fill in anything missing.";
        MapsInfo.Text = mapsFolder == null ? "Not set: using built-in maps only." + fallback :
            !Directory.Exists(mapsFolder) ? mapsPrefix + "Folder not found; using built-in maps." + fallback :
            maps.Count == 0 ? mapsPrefix + "No map files (zone.txt) found here." + fallback :
            $"{mapsPrefix}{maps.Count} zone maps: {string.Join(", ", maps.Take(6))}{(maps.Count > 6 ? ", …" : "")}.{fallback}";

        // Offer the servers found in the log folder.
        var current = ServerBox.Text;
        var servers = Find(LogBox.Text, "eqlog_*.txt", sub)
            .Select(p => LogScanner.TryParseFileName(p, out _, out var s) ? s : null)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        ServerBox.ItemsSource = new[] { "" }.Concat(servers).ToList();
        ServerBox.Text = current;
    }

    private static string Describe(string folder, string pattern, string noun, bool sub, string hint,
                                   Func<string, bool>? filter = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return $"Not set. {hint}";
        if (!Directory.Exists(folder)) return "Folder not found.";
        var files = Find(folder, pattern, sub).Where(filter ?? (_ => true)).ToList();
        if (files.Count == 0) return $"No {noun}s found here yet. {hint}";
        var names = files.Select(Path.GetFileNameWithoutExtension).Take(6);
        return $"{files.Count} {noun}{(files.Count == 1 ? "" : "s")}: {string.Join(", ", names)}{(files.Count > 6 ? ", …" : "")}";
    }

    private static List<string> Find(string folder, string pattern, bool sub)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = sub, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
            }).Take(500).ToList();
        }
        catch { return new(); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var missing = new[] { InventoryBox.Text, SpellbookBox.Text, LogBox.Text, IniBox.Text, MapsBox.Text }
            .Where(f => !string.IsNullOrWhiteSpace(f) && !Directory.Exists(f)).ToList();
        if (missing.Count > 0 &&
            MessageBox.Show(this, "These folders don't exist:\n\n" + string.Join("\n", missing) + "\n\nSave anyway?",
                "WhereEQ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var s = _original.Clone();
        s.InventoryFolder = Clean(InventoryBox.Text);
        s.SpellbookFolder = Clean(SpellbookBox.Text);
        s.LogFolder = Clean(LogBox.Text);
        s.IniFolder = Clean(IniBox.Text);
        s.MapsFolder = Clean(MapsBox.Text);
        s.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        s.LogServer = Clean(ServerBox.Text);
        s.AlwaysOnTop = TopmostCheck.IsChecked == true;
        s.BankSlots = BankSlotsBox.SelectedItem is int b ? b : InventoryParser.DefaultBankSlots;
        s.Sounds = new SoundSettings
        {
            Fall = SoundFallCheck.IsChecked == true,
            Stand = SoundStandCheck.IsChecked == true,
            Tell = SoundTellCheck.IsChecked == true,
        };
        Result = s;
        DialogResult = true;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name && Enum.TryParse<Sound>(name, out var sound))
            Sounds.Play(sound, force: true);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('"');
}
