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
        {
            map.Files.Add(Path.GetFileName(file));
            foreach (var raw in FileUtil.ReadLinesShared(file))
                map.ParseLine(raw);
        }
        map.ComputeBounds();
        return map;
    }

    /// <summary>Files that were read, and how many L/P lines were seen vs. understood (for diagnostics).</summary>
    public List<string> Files { get; } = new();
    public int TotalLines { get; private set; }
    public int SkippedLines { get; private set; }
    public string? FirstSkipped { get; private set; }

    private static readonly System.Text.RegularExpressions.Regex Number =
        new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Reads "L x1, y1, z1, x2, y2, z2, r, g, b" and "P x, y, z, r, g, b, size, Label". Pulls the numbers out with a
    /// regex rather than relying on exact comma/space layout, since map packs differ slightly.
    /// </summary>
    private void ParseLine(string raw)
    {
        var line = raw.Trim().TrimStart('﻿', 'ï', '»', '¿').Trim();
        if (line.Length < 3) return;
        var kind = char.ToUpperInvariant(line[0]);
        if (kind is not ('L' or 'P') || !(char.IsWhiteSpace(line[1]) || line[1] == ',')) return;
        TotalLines++;
        var body = line[1..];
        var nums = Number.Matches(body);

        if (kind == 'L' && nums.Count >= 6)
        {
            // Some packs omit the colour; default to black (drawn light grey in "Bright" style).
            var color = nums.Count >= 9 ? Rgb(nums[6].Value, nums[7].Value, nums[8].Value) : Colors.Black;
            Lines.Add(new MapLine(new Point(D(nums[0].Value), D(nums[1].Value)),
                                  new Point(D(nums[3].Value), D(nums[4].Value)), color));
            return;
        }
        if (kind == 'P' && nums.Count >= 7)
        {
            // Label is whatever follows the 7th number (size); usually after a comma.
            var afterSize = body[(nums[6].Index + nums[6].Length)..].TrimStart(',', ' ', '\t');
            Labels.Add(new MapLabel(new Point(D(nums[0].Value), D(nums[1].Value)),
                                    Rgb(nums[3].Value, nums[4].Value, nums[5].Value),
                                    (int)D(nums[6].Value), afterSize.Replace('_', ' ').Trim()));
            return;
        }
        SkippedLines++;
        FirstSkipped ??= line.Length > 80 ? line[..80] : line;
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

    private static byte B(string s) =>
        (byte)Math.Clamp(double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)v : 0, 0, 255);
}
