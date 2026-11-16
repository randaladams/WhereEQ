using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        BackdropSlider.Value = m.Backdrop;
        OpacitySlider.Value = m.MapOpacity;
        FollowButton.IsChecked = m.Follow;
        _updatingUi = false;

        Backdrop.Opacity = m.Backdrop;
        View.Opacity = m.MapOpacity;
        View.LineStyle = m.LineStyle;
        View.ShowLabels = m.ShowLabels;
        View.Follow = m.Follow;
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

        var maps = new[] { AutoMap }.Concat(MapData.Available(_settings.EffectiveMapsFolder)).ToList();
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
        m = MapData.Load(_settings.EffectiveMapsFolder, shortName);
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
        TitleText.Text = c == null ? "WhereEQ Map" : $"{c.Name} · {title}";
        ZoneCaption.Text = title;

        // Only show the dot if the last /loc was taken in the zone this map is for.
        var locHere = log?.MapX is { } x && log.MapY is { } y &&
                      (_mapOverride == null ? log.LocZone == log.Zone : ZoneNames.ToShort(log.LocZone) == _mapOverride);
        Point? player = locHere ? new Point(log!.MapX!.Value, log.MapY!.Value) : null;
        var trail = locHere ? SafeTrail(log!) : new List<Point>();
        View.SetPlayer(player, locHere ? HeadingOf(log!, trail) : null, trail);

        StatusText.Text =
            _settings.EffectiveMapsFolder == null ? "Maps folder not set: open Settings in WhereEQ." :
            map == null ? (shortName == null ? $"No map name known for \"{zone}\". Pick one under ⚙ → Map."
                                             : $"No map file for {shortName} in the maps folder.") :
            !locHere ? "No /loc in this zone yet. Type /loc (or use a /loc hotkey)." :
            $"{Character.Ago(log!.LocSeen!.Value)} · {-log.MapY!.Value:0}, {-log.MapX!.Value:0}" +
            (log.Heading != null ? $" · {log.Heading}" : "");
    }

    private static List<Point> SafeTrail(LogState log)
    {
        // The log reader appends on a background thread; retry if we catch it mid-update.
        for (var i = 0; i < 3; i++)
        {
            try { return log.Trail.ToArray().Select(t => new Point(t.X, t.Y)).ToList(); }
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

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

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
