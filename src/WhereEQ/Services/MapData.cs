using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace WhereEQ.Services;

public sealed record MapLine(Point A, Point B, Color Color);
public sealed record MapLabel(Point At, Color Color, int Size, string Text);

/// <summary>
/// An EverQuest client map (maps\&lt;short&gt;.txt plus optional _1.._3 layers):
///   L x1, y1, z1, x2, y2, z2, r, g, b
///   P x, y, z, r, g, b, size, Label_Text
/// Map coordinates relate to /loc ("Your Location is Y, X, Z") as map = (-X, -Y); see <see cref="FromLoc"/>.
/// </summary>
public sealed class MapData
{
    public required string ShortName { get; init; }
    public List<MapLine> Lines { get; } = new();
    public List<MapLabel> Labels { get; } = new();
    public Rect Bounds { get; private set; } = Rect.Empty;

    /// <summary>/loc prints Y then X; map files store (-X, -Y). Verified against logged locs in East Commonlands.</summary>
    public static Point FromLoc(double locY, double locX) => new(-locX, -locY);

    /// <summary>All map files available in a folder, by short name (layers like ecommons_1 fold into ecommons).</summary>
    public static List<string> Available(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
        return Directory.EnumerateFiles(folder, "*.txt")
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .Select(n => n.Length > 2 && n[^2] == '_' && char.IsDigit(n[^1]) ? n[..^2] : n)
            .Distinct().OrderBy(n => n).ToList();
    }

    public static MapData? Load(string? folder, string shortName)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var files = new[] { "", "_1", "_2", "_3" }
            .Select(s => Path.Combine(folder, shortName + s + ".txt"))
            .Where(File.Exists).ToList();
        if (files.Count == 0) return null;

        var map = new MapData { ShortName = shortName };
        foreach (var file in files)
            foreach (var raw in FileUtil.ReadLinesShared(file))
                map.ParseLine(raw);
        map.ComputeBounds();
        return map;
    }

    private void ParseLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length < 3) return;
        var kind = char.ToUpperInvariant(line[0]);
        var parts = line[1..].Split(',', StringSplitOptions.TrimEntries);
        try
        {
            if (kind == 'L' && parts.Length >= 9)
            {
                Lines.Add(new MapLine(new Point(D(parts[0]), D(parts[1])), new Point(D(parts[3]), D(parts[4])),
                                      Rgb(parts[6], parts[7], parts[8])));
            }
            else if (kind == 'P' && parts.Length >= 8)
            {
                var text = string.Join(",", parts.Skip(7)).Replace('_', ' ').Trim();
                Labels.Add(new MapLabel(new Point(D(parts[0]), D(parts[1])), Rgb(parts[3], parts[4], parts[5]),
                                        int.TryParse(parts[6], out var sz) ? sz : 2, text));
            }
        }
        catch (FormatException) { /* skip malformed lines */ }
    }

    private void ComputeBounds()
    {
        var r = Rect.Empty;
        foreach (var l in Lines) { r.Union(l.A); r.Union(l.B); }
        foreach (var p in Labels) r.Union(p.At);
        Bounds = r;
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Color Rgb(string r, string g, string b) =>
        Color.FromRgb(B(r), B(g), B(b));

    private static byte B(string s) => (byte)Math.Clamp(int.TryParse(s, out var v) ? v : 0, 0, 255);
}
