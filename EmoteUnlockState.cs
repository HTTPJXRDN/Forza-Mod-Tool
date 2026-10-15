#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

/// <summary>FH6 emote ownership grants in the working copy; no file or equipment writes.</summary>
public static class EmoteUnlockState
{
    const string RecordName = "EmoteSaveState";
    const int MaxEmotes = 10_000;
    const int HeaderLength = 28;
    const int ItemsOffset = 32;
    public sealed record Emote(ulong Id, string Name);
    public static IReadOnlyList<Emote> AllEmotes => EmoteCatalog.Items;
    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries);
    sealed record Inventory(byte[] Header, List<ulong> Items);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        status = default;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        var ids = inventory!.Items.ToHashSet();
        status = new(AllEmotes.Count(emote => ids.Contains(emote.Id)), AllEmotes.Count, ids.Count);
        return true;
    }

    public static bool TryUnlockAll(ProfileContainer? container, out int added)
        => TryUnlockSelected(container, AllEmotes.Select(e => e.Id), out added);

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
        if (selected is null || selected.Count == 0 || selected.Any(id => !AllEmotes.Any(e => e.Id == id))) return false;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        var ids = inventory!.Items.ToHashSet();
        int changed = 0;
        foreach (var emote in AllEmotes.Where(e => selected.Contains(e.Id)))
            if (ids.Add(emote.Id)) { inventory.Items.Add(emote.Id); changed++; }
        if (changed == 0) return true;
        if (inventory.Items.Count > MaxEmotes) return false;
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
        // Observed version 0: three u64 equipped IDs, a u32 ownership count,
        // then exactly that many u64 IDs. This is NOT the horn/clothing layout.
        if (bytes.Length < ItemsOffset || U32(bytes, 0) != 0) return false;
        uint count = U32(bytes, HeaderLength);
        if (count > MaxEmotes || bytes.Length != ItemsOffset + count * 8L) return false;
        var items = new List<ulong>(); var unique = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong id = U64(bytes, ItemsOffset + i * 8);
            if (id == 0 || !unique.Add(id)) return false;
            items.Add(id);
        }
        // Preserve all three selections verbatim, including repeated selections.
        // Unset/unknown layouts are refused rather than filled with guessed defaults.
        foreach (int at in new[] { 4, 12, 20 })
            if (!unique.Contains(U64(bytes, at))) return false;
        var parsed = new Inventory(bytes[..HeaderLength], items);
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        byte[] bytes = new byte[ItemsOffset + inventory.Items.Count * 8];
        inventory.Header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(HeaderLength), (uint)inventory.Items.Count);
        for (int i = 0; i < inventory.Items.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(ItemsOffset + i * 8), inventory.Items[i]);
        return bytes;
    }
    static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8));
}
#endif
