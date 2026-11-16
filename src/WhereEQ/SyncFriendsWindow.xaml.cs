using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Makes several characters' friends lists the same: every ticked character's Name_server.ini gets exactly the
/// ticked friends (existing order kept, new names appended alphabetically).
/// </summary>
public partial class SyncFriendsWindow : Window
{
    public sealed class Item : INotifyPropertyChanged
    {
        private bool _isChecked = true;
        private string _detail = "";
        public required string Name { get; init; }
        public Character? Character { get; init; }
        public bool IsChecked { get => _isChecked; set { _isChecked = value; Changed(nameof(IsChecked)); } }
        public string Detail { get => _detail; set { _detail = value; Changed(nameof(Detail)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly List<Item> _friends;
    private readonly List<Item> _characters;

    /// <summary>Number of .ini files written (0 if cancelled).</summary>
    public int Updated { get; private set; }

    public SyncFriendsWindow(IReadOnlyList<Character> characters)
    {
        InitializeComponent();

        // Everyone who is a friend of any character (from .ini, or the log when a character has no .ini).
        _friends = characters
            .SelectMany(c => c.FriendNames.Select(f => (owner: c.Name, f)))
            .GroupBy(x => x.f, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Item
            {
                Name = g.First().f,
                Detail = "on " + string.Join(", ", g.Select(x => x.owner).Distinct()),
            })
            .ToList();
        _characters = characters.Where(c => c.IniPath != null)
            .Select(c => new Item { Name = c.Name, Character = c })
            .ToList();

        FriendsList.ItemsSource = _friends;
        CharactersList.ItemsSource = _characters;

        var noIni = characters.Where(c => c.IniPath == null).Select(c => c.Name).ToList();
        NoIniText.Text = noIni.Count == 0 ? ""
            : $"Not listed (no Name_server.ini found): {string.Join(", ", noIni)}. Log in once with each so the game creates it.";

        EqRunningBanner.Visibility = IsEverQuestRunning() ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private static bool IsEverQuestRunning()
    {
        try { return Process.GetProcessesByName("eqgame").Length > 0; }
        catch { return false; }
    }

    private List<string> Wanted => _friends.Where(f => f.IsChecked).Select(f => f.Name).ToList();

    /// <summary>The list a character ends up with: its current friends that are still wanted, then the new ones.</summary>
    private static List<string> FinalList(Character c, List<string> wanted)
    {
        var current = c.IniFriends ?? new List<string>();
        var keep = current.Where(f => wanted.Contains(f, StringComparer.OrdinalIgnoreCase));
        var add = wanted.Where(f => !current.Contains(f, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        return keep.Concat(add).ToList();
    }

    private void UpdatePreview()
    {
        var wanted = Wanted;
        var changes = 0;
        foreach (var item in _characters)
        {
            var c = item.Character!;
            var current = c.IniFriends ?? new List<string>();
            var adds = wanted.Count(f => !current.Contains(f, StringComparer.OrdinalIgnoreCase));
            var removes = current.Count(f => !wanted.Contains(f, StringComparer.OrdinalIgnoreCase));
            item.Detail = adds == 0 && removes == 0
                ? $"{current.Count} friends · no change"
                : $"{current.Count} friends · +{adds} / −{removes}";
            if (item.IsChecked && (adds > 0 || removes > 0)) changes++;
        }

        var tooMany = wanted.Count > CharacterIniParser.MaxFriends;
        ApplyButton.IsEnabled = changes > 0 && !tooMany;
        ApplyButton.Content = changes > 0 ? $"Update {changes} character{(changes == 1 ? "" : "s")}" : "Update";
        SummaryText.Text = tooMany
            ? $"{wanted.Count} friends ticked; EverQuest allows at most {CharacterIniParser.MaxFriends}. Untick some."
            : $"{wanted.Count} friends ticked. {changes} character(s) will change.";
    }

    private void Item_Click(object sender, RoutedEventArgs e) => UpdatePreview();

    private void FriendsAll_Click(object sender, RoutedEventArgs e) { _friends.ForEach(f => f.IsChecked = true); UpdatePreview(); }
    private void FriendsNone_Click(object sender, RoutedEventArgs e) { _friends.ForEach(f => f.IsChecked = false); UpdatePreview(); }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (IsEverQuestRunning() &&
            MessageBox.Show(this, "EverQuest is still running. Characters that are logged in will overwrite these changes when they camp.\n\nUpdate anyway?",
                "WhereEQ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var wanted = Wanted;
        var errors = new List<string>();
        foreach (var item in _characters.Where(i => i.IsChecked))
        {
            var c = item.Character!;
            var final = FinalList(c, wanted);
            if (final.SequenceEqual(c.IniFriends ?? new List<string>(), StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                CharacterIniParser.WriteFriends(c.IniPath!, final);
                Updated++;
            }
            catch (Exception ex) { errors.Add($"{c.Name}: {ex.Message}"); }
        }

        if (errors.Count > 0)
            MessageBox.Show(this, "Some files couldn't be updated:\n\n" + string.Join("\n", errors), "WhereEQ",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        DialogResult = Updated > 0;
    }
}
