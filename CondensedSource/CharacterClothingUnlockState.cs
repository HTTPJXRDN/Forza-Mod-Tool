#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.IO;

namespace FH6LocalCryptoTool;

/// <summary>
/// Source-testing clothing grants from the installed game's player wardrobe
/// definitions, with acquisition framing checked against natural FH6 snapshots.
/// </summary>
public static class CharacterClothingUnlockState
{
    const string RecordName = "CharacterCustomisation";
    const string Serializer = "CharacterCustomisationState";
    const int MaxItems = 10_000;
    public sealed record Clothing(ulong Id, string Name, string Slot);
    public static IReadOnlyList<Clothing> AllClothing => ProfileGameData.Current?.Clothing ?? CharacterClothingCatalog.Items;
    // The game resets these VIP-themed entries to a different first-flag state
    // despite inventory grants. Preserve their full state; this editor does not
    // represent or repair ownership of the VIP entitlement.
    static readonly HashSet<ulong> EntitlementManagedIds = new() { 0x0710CA5E54312B16UL, 0xF9FCC4F16FBE87D4UL };
    public static bool IsEntitlementManaged(ulong id) => EntitlementManagedIds.Contains(id);
    public static int SaveManagedTotal => AllClothing.Count(item => !IsEntitlementManaged(item.Id));
    public readonly record struct Status(int Unlocked, int Total, int InventoryEntries, int Missing, int StoredLocked, int EntitlementManaged);
    public readonly record struct UnlockResult(int Added, int UnlockedExisting)
    {
        public int Changed => Added + UnlockedExisting;
    }
    readonly record struct Flags(byte First, byte Second);
    sealed record Inventory(byte[] Header, SortedDictionary<ulong, Flags> Items, byte Trailer);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        using var definitions = ProfileGameData.Use(container);
        status = default;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        int unlocked = 0, missing = 0, storedLocked = 0;
        foreach (var item in AllClothing)
            if (IsEntitlementManaged(item.Id)) continue;
            else if (!inventory!.Items.TryGetValue(item.Id, out var flags)) missing++;
            else if (flags.First == 1) unlocked++;
            else storedLocked++;
        status = new(unlocked, AllClothing.Count, inventory!.Items.Count, missing, storedLocked, EntitlementManagedIds.Count);
        return true;
    }

    /// <summary>
    /// Grant catalogued wardrobe items using the natural game's 1,1 acquisition
    /// flags. Preserve already-owned entries and their viewed/new flag, equipment,
    /// non-wardrobe items, rewards and account. No save or game file is written.
    /// </summary>
    public static bool TryUnlockAll(ProfileContainer? container, out UnlockResult result)
    {
        using var definitions = ProfileGameData.Use(container);
        return TryUnlockSelected(container, AllClothing.Where(c => !IsEntitlementManaged(c.Id)).Select(c => c.Id), out result);
    }

    public static bool TryReadUnlocked(ProfileContainer? container, out ulong[] ids)
    {
        using var definitions = ProfileGameData.Use(container);
        ids = Array.Empty<ulong>();
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        ids = inventory!.Items.Where(p => p.Value.First == 1).Select(p => p.Key).ToArray(); return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out UnlockResult result)
    {
        using var definitions = ProfileGameData.Use(container);
        result = default;
        var selected = selectedIds?.ToHashSet();
        if (selected is null || selected.Count == 0 || selected.Any(id => !AllClothing.Any(c => c.Id == id) || IsEntitlementManaged(id))) return false;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        int added = 0, unlockedExisting = 0;
        foreach (var item in AllClothing.Where(c => selected.Contains(c.Id)))
            if (IsEntitlementManaged(item.Id)) continue;
            else if (inventory!.Items.TryAdd(item.Id, new Flags(1, 1))) added++;
            else if (inventory.Items[item.Id].First == 0)
            {
                inventory.Items[item.Id] = new Flags(1, 1);
                unlockedExisting++;
            }
        if (added + unlockedExisting == 0) return true;
        if (inventory!.Items.Count > MaxItems) return false;
        byte[] edited = Serialize(inventory);
        if (!TryParse(edited, out _)) return false;
        container!.Career!.ReplacePayload(record!, edited);
        result = new(added, unlockedExisting);
        return true;
    }

    static bool TryRecord(ProfileContainer? container, out BinaryCareer.Record? record)
    {
        record = null;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray();
        if (records.Length != 1 || records[0].Serializer != Serializer) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == RecordName) == 1).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != Serializer ||
            values[0].Children.Count != 0 || values[0].Attributes.Count != 1) return false;
        record = records[0];
        return true;
    }

    static bool TryParse(byte[] bytes, out Inventory? inventory)
    {
        inventory = null;
        if (bytes.Length < 13 || U32(bytes, 0) != 3) return false;
        uint slots = U32(bytes, 4);
        if (slots is < 1 or > 64) return false;
        int countAt = 8 + (int)slots * 16 + 1;
        if (countAt > bytes.Length - 5) return false;
        // Preserve the equipped header/selected avatar byte opaquely. Require
        // unambiguous equipped slot identities, but never change their values.
        var slotIds = new HashSet<ulong>();
        for (int i = 0; i < slots; i++)
            if (!slotIds.Add(U64(bytes, 8 + i * 16))) return false;
        uint count = U32(bytes, countAt);
        if (count > MaxItems || bytes.Length != countAt + 4L + count * 10L + 1 || bytes[^1] > 1) return false;
        var items = new SortedDictionary<ulong, Flags>();
        ulong previous = 0;
        for (int i = 0; i < count; i++)
        {
            int at = countAt + 4 + i * 10;
            ulong id = U64(bytes, at);
            byte first = bytes[at + 8], second = bytes[at + 9];
            if (id == 0 || first > 1 || second > 1 || (i > 0 && id <= previous) || !items.TryAdd(id, new(first, second))) return false;
            previous = id;
        }
        var parsed = new Inventory(bytes[..countAt], items, bytes[^1]);
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(inventory.Header);
        writer.Write((uint)inventory.Items.Count);
        foreach (var item in inventory.Items)
        {
            writer.Write(item.Key); writer.Write(item.Value.First); writer.Write(item.Value.Second);
        }
        writer.Write(inventory.Trailer);
        return buffer.ToArray();
    }
    static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8));
}
#endif
