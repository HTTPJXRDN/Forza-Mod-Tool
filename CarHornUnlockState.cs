#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

/// <summary>Source-only FH6 horn inventory grants; no file writes or equipment changes.</summary>
public static class CarHornUnlockState
{
    const string RecordName = "CarHornSaveState";
    const int MaxHorns = 10_000;
    public sealed record Horn(ulong Id, string Name, int Category);
    public static IReadOnlyList<Horn> AllHorns => CarHornCatalog.Items;
    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries);
    sealed record Inventory(byte[] Header, List<ulong> Items);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        var ids = inventory!.Items.ToHashSet();
        status = new(AllHorns.Count(horn => ids.Contains(horn.Id)), AllHorns.Count, ids.Count);
        return true;
    }

    public static bool TryUnlockAll(ProfileContainer? container, out int added)
        => TryUnlockSelected(container, AllHorns.Select(h => h.Id), out added);

    public static bool TryReadUnlocked(ProfileContainer? container, out ulong[] ids)
    {
        ids = Array.Empty<ulong>();
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        ids = inventory!.Items.ToArray(); return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
    {
        added = 0;
        var selected = selectedIds?.ToHashSet();
        if (selected is null || selected.Count == 0 || selected.Any(id => !AllHorns.Any(h => h.Id == id))) return false;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        var ids = inventory!.Items.ToHashSet();
        int changed = 0;
        // A natural wheelspin acquisition appended exactly one ID. Keep all
        // existing ordering; do not sort or reinterpret this list as clothing flags.
        foreach (var horn in AllHorns.Where(h => selected.Contains(h.Id)))
            if (ids.Add(horn.Id)) { inventory.Items.Add(horn.Id); changed++; }
        if (changed == 0) return true;
        if (inventory.Items.Count > MaxHorns) return false;
        byte[] edited = Serialize(inventory);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        added = changed;
        return true;
    }

    static bool TryRecord(ProfileContainer? container, out BinaryCareer.Record? record)
    {
        record = null;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray();
        if (records.Length != 1 || records[0].Serializer != RecordName) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == RecordName) == 1).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != RecordName ||
            values[0].Children.Count != 0 || values[0].Attributes.Count != 1) return false;
        record = records[0];
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < 17 || U32(bytes, 0) != 1 || bytes[4] != 1) return false;
        uint count = U32(bytes, 13);
        if (count > MaxHorns || bytes.Length != 17L + count * 8L) return false;
        var items = new List<ulong>(); var unique = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong id = U64(bytes, 17 + i * 8);
            if (id == 0 || !unique.Add(id)) return false;
            items.Add(id);
        }
        // Only the observed equipped-horn layout is supported. Missing records
        // and unknown header variants are rejected, not fabricated or repaired.
        if (!unique.Contains(U64(bytes, 5))) return false;
        var parsed = new Inventory(bytes[..13], items);
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        byte[] bytes = new byte[17 + inventory.Items.Count * 8];
        inventory.Header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(13), (uint)inventory.Items.Count);
        for (int i = 0; i < inventory.Items.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(17 + i * 8), inventory.Items[i]);
        return bytes;
    }
    static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8));
}
#endif
