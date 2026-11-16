using System.IO;
using System.Text.RegularExpressions;
using WhereEQ.Models;

namespace WhereEQ.Services;

/// <summary>
/// Parses the tab-separated output of /outputfile inventory:
///   Location  Name  ID  Count  Slots
/// Top-level slots are "Ear", "General3", "Bank5", "SharedBank1"; a container's contents follow it as
/// "General3-Slot4" etc. A top-level row is a container exactly when "-SlotN" rows exist for it.
/// </summary>
public static partial class InventoryParser
{
    [GeneratedRegex(@"^(?<area>General|Bank|SharedBank)(?<n>\d+)(?:-Slot(?<slot>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerLocation();

    private sealed record Row(string Loc, string Name, int Id, int Count, bool Empty);

    public static string CharacterFromFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash > 0 ? name[..dash] : name;
    }

    public static CharacterInventory Parse(string path)
    {
        var character = CharacterFromFile(path);
        var snapshot = File.GetLastWriteTime(path);
        var inv = new CharacterInventory { Character = character, FilePath = path, SnapshotTime = snapshot };

        var rows = new List<Row>();
        foreach (var line in FileUtil.ReadLinesShared(path))
        {
            var cols = line.Split('\t');
            if (cols.Length < 4 || cols[0].Equals("Location", StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(cols[2], out var id);
            int.TryParse(cols[3], out var count);
            var name = cols[1].Trim();
            rows.Add(new Row(cols[0].Trim(), name, id, count, id == 0 && name.Equals("Empty", StringComparison.OrdinalIgnoreCase)));
        }

        // Pass 1: which top-level slots are containers, and what are they called.
        var containers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var m = ContainerLocation().Match(r.Loc);
            if (m.Success && m.Groups["slot"].Success)
                containers.TryAdd($"{m.Groups["area"].Value}{m.Groups["n"].Value}", "");
        }
        foreach (var r in rows)
            if (containers.ContainsKey(r.Loc)) containers[r.Loc] = r.Name;

        // Pass 2: build items and count free space.
        foreach (var r in rows)
        {
            var m = ContainerLocation().Match(r.Loc);
            // P1999 has no shared bank; the client still writes empty SharedBank rows.
            if (m.Success && m.Groups["area"].Value.Equals("SharedBank", StringComparison.OrdinalIgnoreCase)) continue;
            ItemArea area;
            string where;
            var isContainer = false;

            if (m.Success)
            {
                var kind = m.Groups["area"].Value.ToLowerInvariant();
                var n = m.Groups["n"].Value;
                var top = $"{m.Groups["area"].Value}{n}";
                area = kind == "general" ? ItemArea.Inventory : ItemArea.Bank;
                var label = kind == "general" ? $"Bag {n}" : $"Bank {n}";

                if (m.Groups["slot"].Success)
                {
                    var bag = containers.TryGetValue(top, out var bn) && bn.Length > 0 ? $"{label} ({bn})" : label;
                    where = $"{bag}, slot {m.Groups["slot"].Value}";
                    CountSlot(inv, area, r.Empty);
                }
                else
                {
                    isContainer = containers.ContainsKey(top);
                    where = kind == "general" ? $"Inventory slot {n}" : label;
                    // A bag's capacity is counted through its child rows; anything else is one slot.
                    if (!isContainer) CountSlot(inv, area, r.Empty);
                }
            }
            else if (r.Loc.Equals("Held", StringComparison.OrdinalIgnoreCase) || r.Loc.Equals("Cursor", StringComparison.OrdinalIgnoreCase))
            {
                area = ItemArea.Cursor;
                where = "Cursor";
            }
            else
            {
                area = ItemArea.Equipped;
                where = r.Loc;
            }

            if (r.Empty) continue;

            inv.Items.Add(new InventoryItem
            {
                Character = character,
                Name = r.Name,
                Id = r.Id,
                Count = Math.Max(r.Count, 1),
                RawLocation = r.Loc,
                Area = area,
                Where = where,
                IsContainer = isContainer,
                SnapshotTime = snapshot,
            });
        }

        return inv;
    }

    private static void CountSlot(CharacterInventory inv, ItemArea area, bool empty)
    {
        switch (area)
        {
            case ItemArea.Inventory:
                inv.BagSlotsTotal++;
                if (empty) inv.BagSlotsFree++;
                break;
            case ItemArea.Bank:
                inv.BankSlotsTotal++;
                if (empty) inv.BankSlotsFree++;
                break;
        }
    }
}
