using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WhereEQ.Services;

namespace WhereEQ;

/// <summary>
/// Draws an EverQuest map with the player's position, heading and recent trail. Mouse wheel zooms around the
/// cursor, left-drag pans, double-click fits the zone. Map space: x = east, y = south (north is up).
/// </summary>
public sealed class MapView : FrameworkElement
{
    private MapData? _map;
    private List<(Color color, StreamGeometry geo)> _layers = new();
    private double _scale = 1;
    private Point _center;
    private bool _fitted;
    private Point? _dragStart;
    private Point _dragCenter;

    public Point? Player { get; private set; }
    /// <summary>Heading as a unit vector in map space, if known.</summary>
    public Vector? Heading { get; private set; }
    public IReadOnlyList<Point> Trail { get; private set; } = Array.Empty<Point>();
    public string LineStyle { get; set; } = "Bright";
    public bool ShowLabels { get; set; } = true;
    public bool Follow { get; set; } = true;

    /// <summary>Raised when the user pans by hand (so the window can turn Follow off).</summary>
    public event Action? UserPanned;

    public MapData? Map
    {
        get => _map;
        set
        {
            if (ReferenceEquals(_map, value)) return;
            _map = value;
            BuildLayers();
            _fitted = false;
            InvalidateVisual();
        }
    }

    public void SetPlayer(Point? player, Vector? heading, IReadOnlyList<Point> trail)
    {
        Player = player;
        Heading = heading;
        Trail = trail;
        if (Follow && player is { } p && _fitted) _center = p;
        InvalidateVisual();
    }

    public void Fit()
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;
        if (_map == null || _map.Bounds.IsEmpty)
        {
            // No walls to fit: show ~600 units around the player so the trail is a sensible size.
            if (Player is not { } pp) return;
            _scale = Math.Min(ActualWidth, ActualHeight) / 600;
            _center = pp;
            _fitted = true;
            InvalidateVisual();
            return;
        }
        var b = _map.Bounds;
        _scale = Math.Min(ActualWidth / Math.Max(b.Width, 1), ActualHeight / Math.Max(b.Height, 1)) * 0.92;
        _center = Follow && Player is { } p ? p : new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        _fitted = true;
        InvalidateVisual();
    }

    public void Zoom(double factor, Point? aroundScreen = null)
    {
        var anchor = aroundScreen ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var before = ToMap(anchor);
        _scale = Math.Clamp(_scale * factor, 0.005, 50);
        var after = ToMap(anchor);
        if (!Follow) _center += before - after;
        InvalidateVisual();
    }

    private Point ToScreen(Point m) => new((m.X - _center.X) * _scale + ActualWidth / 2, (m.Y - _center.Y) * _scale + ActualHeight / 2);
    private Point ToMap(Point s) => new((s.X - ActualWidth / 2) / _scale + _center.X, (s.Y - ActualHeight / 2) / _scale + _center.Y);

    private void BuildLayers()
    {
        _layers = new();
        if (_map == null) return;
        foreach (var group in _map.Lines.GroupBy(l => l.Color))
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
                foreach (var l in group)
                {
                    ctx.BeginFigure(l.A, false, false);
                    ctx.LineTo(l.B, true, false);
                }
            geo.Freeze();
            _layers.Add((group.Key, geo));
        }
    }

    private Color LineColor(Color c) => LineStyle switch
    {
        "White" => Colors.White,
        "Original" => c,
        // Bright: lift map colors toward white so dark walls show over the game.
        _ => Color.FromRgb((byte)(c.R + (255 - c.R) * 0.6), (byte)(c.G + (255 - c.G) * 0.6), (byte)(c.B + (255 - c.B) * 0.6)),
    };

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_fitted) Fit();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent but hit-testable, so the mouse works anywhere on the map.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_map == null && Player == null) return;
        if (!_fitted) Fit();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

        // Walls: geometry is in map space; draw it through the view transform with a constant on-screen width.
        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale,
            ActualWidth / 2 - _center.X * _scale, ActualHeight / 2 - _center.Y * _scale));
        foreach (var (color, geo) in _layers)
        {
            var pen = new Pen(new SolidColorBrush(LineColor(color)), 1.3 / _scale);
            dc.DrawGeometry(null, pen, geo);
        }
        dc.Pop();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (ShowLabels && _map != null)
        {
            foreach (var label in _map.Labels)
            {
                var s = ToScreen(label.At);
                if (s.X < -50 || s.Y < -20 || s.X > ActualWidth + 50 || s.Y > ActualHeight + 20) continue;
                dc.DrawEllipse(Brushes.Gold, null, s, 2.2, 2.2);
                DrawText(dc, label.Text, new Point(s.X + 4, s.Y - 7), 11, Brushes.White, dpi);
            }
        }

        // Trail: older points fade out.
        if (Trail.Count > 1)
        {
            for (var i = 1; i < Trail.Count; i++)
            {
                // A big jump between consecutive /locs is a gate, death or a gap in /loc spam: don't draw a line
                // across terrain you never walked.
                if ((Trail[i] - Trail[i - 1]).Length > 300) continue;
                var a = (byte)(40 + 180 * i / Trail.Count);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(a, 80, 200, 255)), 2);
                dc.DrawLine(pen, ToScreen(Trail[i - 1]), ToScreen(Trail[i]));
            }
        }

        if (Player is { } pl)
        {
            var s = ToScreen(pl);
            if (Heading is { } h && h.Length > 0)
            {
                var d = h / h.Length;
                var tip = s + d * 16;
                var side = new Vector(-d.Y, d.X) * 6;
                var arrow = new StreamGeometry();
                using (var ctx = arrow.Open())
                {
                    ctx.BeginFigure(tip, true, true);
                    ctx.LineTo(s + side, true, false);
                    ctx.LineTo(s - side, true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(220, 255, 70, 70)), null, arrow);
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 70, 70)), new Pen(Brushes.White, 2), s, 5.5, 5.5);
        }

        dc.Pop();
    }

    private static void DrawText(DrawingContext dc, string text, Point at, double size, Brush brush, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   new Typeface("Segoe UI"), size, Brushes.Black, dpi);
        // Dark outline so labels read over any background.
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            dc.DrawText(ft, new Point(at.X + dx, at.Y + dy));
        ft.SetForegroundBrush(brush);
        dc.DrawText(ft, at);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
        _dragStart = e.GetPosition(this);
        _dragCenter = _center;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } start || !IsMouseCaptured) return;
        var delta = e.GetPosition(this) - start;
        if (Follow && delta.Length < 4) return;
        if (Follow) { Follow = false; UserPanned?.Invoke(); }
        _center = _dragCenter - delta / _scale;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }
}
