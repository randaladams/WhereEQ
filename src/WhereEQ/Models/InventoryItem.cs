namespace WhereEQ.Models;

public enum ItemArea { Equipped, Inventory, Bank, Cursor }

/// <summary>One non-empty row from a Name-Inventory.txt (/outputfile inventory) file.</summary>
public sealed class InventoryItem
{
    public required string Character { get; init; }
    public required string Name { get; init; }
    public int Id { get; init; }
    public int Count { get; init; }
    /// <summary>Raw location from the file, e.g. "General3-Slot4".</summary>
    public required string RawLocation { get; init; }
    public ItemArea Area { get; init; }
    /// <summary>Human-readable location, e.g. "Bag 3 (Light Burlap Sack), slot 4".</summary>
    public required string Where { get; init; }
    public bool IsContainer { get; init; }
    /// <summary>For containers: number of slots inside, and how many are empty.</summary>
    public int BagSlots { get; init; }
    public int BagFreeSlots { get; init; }
    public int BagUsedSlots => BagSlots - BagFreeSlots;
    public DateTime SnapshotTime { get; init; }

    public string AreaText => Area.ToString();
}

public sealed class CharacterInventory
{
    public required string Character { get; init; }
    public required string FilePath { get; init; }
    public DateTime SnapshotTime { get; init; }
    public List<InventoryItem> Items { get; } = new();
    public int BagSlotsTotal { get; set; }
    public int BagSlotsFree { get; set; }
    public int BankSlotsTotal { get; set; }
    public int BankSlotsFree { get; set; }
}
