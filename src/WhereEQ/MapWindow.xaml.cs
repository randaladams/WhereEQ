using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WhereEQ.Models;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Transparent, always-on-top map overlay. Shows the tracked character's zone map, position, heading and trail
/// from /loc lines in the log. Lock (🔒 or Ctrl+Alt+M) makes it click-through so it can sit over EverQuest.
/// </summary>
public partial class MapWindow : Window
{
    private const string AutoCharacter = "Auto (last /loc)";
    private const string AutoMap = "Auto (current zone)";

    private readonly Settings _settings;
    private readonly Dictionary<string, MapData?> _mapCache = new(StringComparer.OrdinalIgnoreCase);
    private List<Character> _characters = new();
    private string? _mapOverride;
    // True until the constructor finishes: sliders fire ValueChanged while XAML is still loading.
    private bool _updatingUi = true;
    private HwndSource? _hwnd;

    public MapWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        var m = settings.Map;

        Width = m.Width;
        Height = m.Height;
        if (m.Left is { } l && m.Top is { } t && OnScreen(l, t)) { Left = l; Top = t; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _updatingUi = true;
        StyleBox.ItemsSource = new[] { "Bright", "Original", "White" };
        StyleBox.SelectedItem = m.LineStyle;
        LabelsCheck.IsChecked = m.ShowLabels;
        TrailCheck.IsChecked = m.ShowTrail;
        BackdropSlider.Value = m.Backdrop;
        OpacitySlider.Value = m.MapOpacity;
        FollowButton.IsChecked = m.Follow;
        _updatingUi = false;

        Backdrop.Opacity = m.Backdrop;
        View.Opacity = m.MapOpacity;
        View.LineStyle = m.LineStyle;
        View.ShowLabels = m.ShowLabels;
        View.Follow = m.Follow;
        // Own drag instead of DragMove: Windows Snap would maximize/tile the overlay at screen edges.
        WindowDrag.Attach(TitleBar, this);
        Loaded += (_, _) => WindowDrag.KeepOnScreen(this);
        View.UserPanned += () => { FollowButton.IsChecked = false; _settings.Map.Follow = false; };

        SourceInitialized += (_, _) =>
        {
            _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwnd?.AddHook(WndProc);
            RegisterHotKey(_hwnd!.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.M));
            ApplyLock(_settings.Map.Locked);
        };
        Closed += (_, _) =>
        {
            if (_hwnd != null) UnregisterHotKey(_hwnd.Handle, HotkeyId);
            SaveState();
        };
        LocationChanged += (_, _) => SaveState();
        // Kill markers fade and expire even when nothing new is logged.
        var fade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        fade.Tick += (_, _) => { if (IsVisible) Refresh(); };
        fade.Start();
        Closed += (_, _) => fade.Stop();
        SizeChanged += (_, _) => SaveState();
    }

    private static bool OnScreen(double l, double t) =>
        l > SystemParameters.VirtualScreenLeft - 100 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
        t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    private void SaveState()
    {
        var m = _settings.Map;
        if (WindowState == WindowState.Normal && IsLoaded) { m.Left = Left; m.Top = Top; m.Width = Width; m.Height = Height; }
        _settings.Save();
    }

    // ------------------------------------------------------------------ data

    /// <summary>Called by the main window whenever logs or files change.</summary>
    public void Update(List<Character> characters)
    {
        _characters = characters;
        var cur = _settings.Map.Character ?? AutoCharacter;
        var names = new[] { AutoCharacter }.Concat(characters.Where(c => c.Log != null).Select(c => c.Name)).ToList();
        _updatingUi = true;
        if (CharacterBox.Items.Count != names.Count || !names.SequenceEqual(CharacterBox.Items.Cast<string>()))
            CharacterBox.ItemsSource = names;
        CharacterBox.SelectedItem = names.Contains(cur) ? cur : AutoCharacter;

        var maps = new[] { AutoMap }.Concat(MapData.AvailableAll(_settings.EffectiveMapsFolder)).ToList();
        if (MapBox.Items.Count != maps.Count) MapBox.ItemsSource = maps;
        MapBox.SelectedItem = _mapOverride ?? AutoMap;
        _updatingUi = false;

        Refresh();
    }

    private Character? Tracked()
    {
        var name = _settings.Map.Character;
        if (name != null && _characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } c)
            return c;
        // Auto: the character whose log has the newest /loc (or newest line), i.e. the one being played.
        return _characters.Where(x => x.Log != null)
            .OrderByDescending(x => x.Log!.LocSeen ?? DateTime.MinValue)
            .ThenByDescending(x => x.Log!.LastLine ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private MapData? LoadMap(string shortName)
    {
        if (_mapCache.TryGetValue(shortName, out var m)) return m;
        m = MapData.LoadBest(_settings.EffectiveMapsFolder, shortName);
        _mapCache[shortName] = m;
        return m;
    }

    /// <summary>Forget cached maps (e.g. after the maps folder changes).</summary>
    public void ReloadMaps() { _mapCache.Clear(); Refresh(); }

    private void Refresh()
    {
        var c = Tracked();
        var log = c?.Log;
        var zone = log?.Zone;
        var shortName = _mapOverride ?? ZoneNames.ToShort(zone);
        var map = shortName != null ? LoadMap(shortName) : null;
        View.Map = map;

        var title = zone ?? (shortName != null ? ZoneNames.ToLong(shortName) : "No zone yet");

        // Only show the dot if the last /loc was taken in the zone this map is for.
        var locHere = log?.MapX is { } x && log.MapY is { } y &&
                      (_mapOverride == null ? log.LocZone == log.Zone : ZoneNames.ToShort(log.LocZone) == _mapOverride);
        Point? player = locHere ? new Point(log!.MapX!.Value, log.MapY!.Value) : null;
        var trail = locHere ? SafeTrail(log!, _settings.Map.TrailClearedAt) : new List<Point>();
        View.SetPlayer(player, locHere ? HeadingOf(log!, trail) : null, _settings.Map.ShowTrail ? trail : new List<Point>());
        View.SetKills(RecentKills(shortName));

        // Time · loc · heading go on the same line as the zone name (title bar when unlocked, caption when locked).
        var locInfo = map != null && locHere
            ? $"{Character.Ago(log!.LocSeen!.Value)} · {-log.MapY!.Value:0}, {-log.MapX!.Value:0}" +
              (log.Heading != null ? $" · {log.Heading}" : "")
            : null;
        SetZoneLine(TitleText, c == null ? "WhereEQ Map" : title, locInfo, c?.Name);
        SetZoneLine(ZoneCaption, title, locInfo, null);

        StatusText.Text =
            map == null ? (shortName == null ? $"No map name known for \"{zone}\". Pick one under ⚙ → Map."
                                             : $"No map for {shortName} in your maps folder or the built-in set.") :
            map.Lines.Count == 0 && map.Labels.Count == 0 ?
                $"{string.Join(", ", map.Files)}: no map lines could be read ({map.TotalLines} L/P lines" +
                (map.FirstSkipped != null ? $", e.g. \"{map.FirstSkipped}\"" : "") + ")" :
            !locHere ? "No /loc in this zone yet. Type /loc (or use a /loc hotkey)." :
            OutsideNote(map, player);
        StatusText.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        var tip = map == null ? null :
            $"Map ({map.Source}): {string.Join(", ", map.Files)}\n{map.Lines.Count} lines, {map.Labels.Count} labels" +
            (map.SkippedLines > 0 ? $", {map.SkippedLines} lines not understood" : "") +
            (map.Bounds.IsEmpty ? "" : $"\nBounds x {map.Bounds.Left:0}..{map.Bounds.Right:0}, y {map.Bounds.Top:0}..{map.Bounds.Bottom:0}");
        StatusText.ToolTip = tip;
        TitleText.ToolTip = tip;
    }

    /// <summary>"Zone  5s ago · 120, -340 · NE  (Name)": zone bold, loc and character in lighter text.</summary>
    private static void SetZoneLine(TextBlock target, string zone, string? locInfo, string? character)
    {
        target.Inlines.Clear();
        target.Inlines.Add(new Run(zone));
        if (locInfo != null)
            target.Inlines.Add(new Run("   " + locInfo) { FontWeight = FontWeights.Normal, Foreground = LocBrush });
        if (character != null)
            target.Inlines.Add(new Run("   " + character) { FontWeight = FontWeights.Normal, Foreground = DimBrush });
    }

    private static readonly Brush LocBrush = Frozen(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly Brush DimBrush = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA0));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Kill markers stay on the map this long.</summary>
    private static readonly TimeSpan KillMarkerTime = TimeSpan.FromMinutes(10);

    /// <summary>Kills in this map's zone in the last 10 minutes, from every character's log (map coordinates are
    /// the same for everyone), placed at the killer's last /loc.</summary>
    private List<(Point P, double Age)> RecentKills(string? shortName)
    {
        var list = new List<(Point, double)>();
        if (shortName == null) return list;
        var now = DateTime.Now;
        foreach (var c in _characters)
        {
            if (c.Log == null) continue;
            KillRecord[] kills;
            try { kills = c.Log.Kills.ToArray(); } catch (ArgumentException) { continue; } catch (InvalidOperationException) { continue; }
            foreach (var k in kills)
            {
                if (k.X is not { } x || k.Y is not { } y) continue;
                var age = now - k.Time;
                if (age < TimeSpan.Zero || age > KillMarkerTime) continue;
                if (!string.Equals(ZoneNames.ToShort(k.Zone), shortName, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((new Point(x, y), age / KillMarkerTime));
            }
        }
        return list;
    }

    /// <summary>Warn when the player is nowhere near the map: wrong map, or a map pack with different coordinates.</summary>
    private static string OutsideNote(MapData map, Point? player)
    {
        if (player is not { } p || map.Bounds.IsEmpty) return "";
        var b = map.Bounds;
        var margin = Math.Max(b.Width, b.Height) * 0.25 + 100;
        var inside = p.X > b.Left - margin && p.X < b.Right + margin && p.Y > b.Top - margin && p.Y < b.Bottom + margin;
        return inside ? "" : "You're outside this map's area (wrong map, or a map pack with other coordinates).";
    }

    private static List<Point> SafeTrail(LogState log, DateTime? after)
    {
        // The log reader appends on a background thread; retry if we catch it mid-update.
        for (var i = 0; i < 3; i++)
        {
            try { return log.Trail.ToArray().Where(t => after == null || t.T > after).Select(t => new Point(t.X, t.Y)).ToList(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return new List<Point>();
    }

    /// <summary>Direction of travel from the last moves; else the last "You think you are heading X".</summary>
    private static Vector? HeadingOf(LogState log, List<Point> trail)
    {
        for (var i = trail.Count - 1; i > 0 && i > trail.Count - 6; i--)
        {
            var v = trail[^1] - trail[i - 1];
            if (v.Length > 3) return v;
        }
        return log.Heading switch
        {
            // Map space: x = east, y = south.
            "North" => new Vector(0, -1), "NorthEast" => new Vector(1, -1), "East" => new Vector(1, 0),
            "SouthEast" => new Vector(1, 1), "South" => new Vector(0, 1), "SouthWest" => new Vector(-1, 1),
            "West" => new Vector(-1, 0), "NorthWest" => new Vector(-1, -1),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ UI

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        View.Follow = _settings.Map.Follow = FollowButton.IsChecked == true;
        if (View.Follow) View.Fit();
        _settings.Save();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => View.Fit();

    private void Options_Click(object sender, RoutedEventArgs e) =>
        OptionsPanel.Visibility = OptionsButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _settings.Map.Open = false;
        Close();
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => ApplyLock(true);

    private void CharacterBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        var sel = CharacterBox.SelectedItem as string;
        _settings.Map.Character = sel == null || sel == AutoCharacter ? null : sel;
        _settings.Save();
        Refresh();
        View.Fit();
    }

    private void MapBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        var sel = MapBox.SelectedItem as string;
        _mapOverride = sel == null || sel == AutoMap ? null : sel;
        Refresh();
        View.Fit();
    }

    private void StyleBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || StyleBox.SelectedItem is not string s) return;
        View.LineStyle = _settings.Map.LineStyle = s;
        View.InvalidateVisual();
        _settings.Save();
    }

    private void Labels_Click(object sender, RoutedEventArgs e)
    {
        View.ShowLabels = _settings.Map.ShowLabels = LabelsCheck.IsChecked == true;
        View.InvalidateVisual();
        _settings.Save();
    }

    private void Trail_Click(object sender, RoutedEventArgs e)
    {
        _settings.Map.ShowTrail = TrailCheck.IsChecked == true;
        _settings.Save();
        Refresh();
    }

    private void ClearTrail_Click(object sender, RoutedEventArgs e)
    {
        // Log timestamps have 1-second resolution: anything up to now is "before".
        _settings.Map.TrailClearedAt = DateTime.Now;
        _settings.Save();
        Refresh();
    }

    private void Backdrop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi) return;
        Backdrop.Opacity = _settings.Map.Backdrop = e.NewValue;
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi) return;
        View.Opacity = _settings.Map.MapOpacity = e.NewValue;
    }

    // ------------------------------------------------------------------ click-through lock + hotkey

    /// <summary>Locked: the window ignores the mouse (clicks reach the game) and hides its controls.</summary>
    public void ApplyLock(bool locked)
    {
        _settings.Map.Locked = locked;
        _settings.Save();
        TitleBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        OptionsPanel.Visibility = Visibility.Collapsed;
        OptionsButton.IsChecked = false;
        ZoneCaption.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Margin = new Thickness(8, locked ? 24 : 34, 8, 0);
        Frame.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;

        if (_hwnd == null) return;
        var style = GetWindowLong(_hwnd.Handle, GWL_EXSTYLE);
        style = locked ? style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : style & ~WS_EX_TRANSPARENT & ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd.Handle, GWL_EXSTYLE, style);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ApplyLock(!_settings.Map.Locked);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private const int HotkeyId = 0x5745; // "WE"
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
