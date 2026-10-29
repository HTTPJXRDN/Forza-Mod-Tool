#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

/// <summary>Working-copy reward inventory; Journal rewards require the combined validated Journal transaction.</summary>
public static class ForzaLinkPhraseUnlockState
{
    const string RecordName = "QuickChatSaveState";
    const int Contexts = 12;
    const int SlotsPerContext = 4;
    const int MaxPhrases = 10_000;
    public sealed record Phrase(ulong Id, string Name, bool JournalReward);
    public static IReadOnlyList<Phrase> AllRewardPhrases => ProfileGameData.Current?.Phrases ?? ForzaLinkPhraseCatalog.Items;
    public readonly record struct Status(int Unlocked, int Total, int JournalProtected, int InventoryEntries);
    sealed record Inventory(byte[] Header, List<ulong> Items);

    public static bool TryRead(ProfileContainer? container, out Status status)
    {
        using var definitions = ProfileGameData.Use(container);
        status = default;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        var ids = inventory!.Items.ToHashSet();
        status = new(AllRewardPhrases.Count(p => !p.JournalReward && ids.Contains(p.Id)),
            AllRewardPhrases.Count(p => !p.JournalReward), AllRewardPhrases.Count(p => p.JournalReward), ids.Count);
        return true;
    }

    public static bool TryUnlockNonJournal(ProfileContainer? container, out int added)
    {
        using var definitions = ProfileGameData.Use(container);
        return TryUnlockSelected(container, AllRewardPhrases.Where(p => !p.JournalReward).Select(p => p.Id), out added);
    }

    public static bool TryReadUnlocked(ProfileContainer? container, out ulong[] ids)
    {
        using var definitions = ProfileGameData.Use(container);
        ids = Array.Empty<ulong>();
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        ids = inventory!.Items.ToArray(); return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
        => TryUnlockSelectedCore(container, selectedIds, false, out added);

    // Only the combined Journal transaction may grant Journal reward phrases,
    // after its mapped category thresholds have been satisfied and validated.
    internal static bool TryUnlockJournalLinkedSelection(ProfileContainer container, IEnumerable<ulong> ids, out int added)
        => TryUnlockSelectedCore(container, ids, true, out added);

    static bool TryUnlockSelectedCore(ProfileContainer? container, IEnumerable<ulong>? selectedIds, bool includeJournal, out int added)
    {
        using var definitions = ProfileGameData.Use(container);
        added = 0;
        var selected = selectedIds?.ToHashSet();
        if (selected is null || selected.Count == 0 || selected.Any(id => !AllRewardPhrases.Any(p => p.Id == id && (includeJournal || !p.JournalReward)))) return false;
        if (includeJournal && !CollectionJournalState.RewardThresholdsMet(container, JournalRewardKind.Phrase, selected, out _)) return false;
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var inventory)) return false;
        var ids = inventory!.Items.ToHashSet();
        int changed = 0;
        // The legacy standalone API still excludes Journal rewards. The new
        // combined transaction includes only verified, satisfied Journal links.
        // Hidden/system phrases, free phrases and equipped slots stay untouched.
        foreach (var phrase in AllRewardPhrases.Where(p => (includeJournal || !p.JournalReward) && selected.Contains(p.Id)))
            if (ids.Add(phrase.Id)) { inventory.Items.Add(phrase.Id); changed++; }
        if (changed == 0) return true;
        if (inventory.Items.Count > MaxPhrases) return false;
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
        // Observed version 0: older saves have 11 of the 12 known context maps.
        // Each stored map has four keyed u64 selections; do not invent absent maps.
        if (bytes.Length < 12 || U32(bytes, 0) != 0) return false;
        uint contextCount = U32(bytes, 4);
        if (contextCount is < 11 or > Contexts) return false;
        var contexts = new HashSet<uint>();
        int offset = 8;
        for (int c = 0; c < contextCount; c++)
        {
            if (offset > bytes.Length - (8 + SlotsPerContext * 12)) return false;
            uint context = U32(bytes, offset), slots = U32(bytes, offset + 4); offset += 8;
            if (context >= Contexts || !contexts.Add(context) || slots != SlotsPerContext) return false;
            var keys = new HashSet<uint>();
            for (int s = 0; s < SlotsPerContext; s++, offset += 12)
            {
                uint slot = U32(bytes, offset);
                if (slot >= SlotsPerContext || !keys.Add(slot)) return false;
                // Selections can be free phrases not present in the reward inventory.
                // Carry their IDs through verbatim; do not add/remove/equip anything.
            }
        }
        if (offset > bytes.Length - 4) return false;
        uint count = U32(bytes, offset); int itemsOffset = offset + 4;
        if (count > MaxPhrases || bytes.Length != itemsOffset + count * 8L) return false;
        // Authentic post-reward saves can repeat ownership IDs. Ownership is a
        // set to callers, but the serialized inventory is an ordered list: keep
        // its multiplicity verbatim rather than rejecting or normalizing it.
        var items = new List<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong id = U64(bytes, itemsOffset + i * 8);
            if (id == 0) return false;
            items.Add(id);
        }
        var parsed = new Inventory(bytes[..offset], items);
        if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
        inventory = parsed;
        return true;
    }

    static byte[] Serialize(Inventory inventory)
    {
        int itemsOffset = inventory.Header.Length + 4;
        byte[] bytes = new byte[itemsOffset + inventory.Items.Count * 8];
        inventory.Header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(inventory.Header.Length), (uint)inventory.Items.Count);
        for (int i = 0; i < inventory.Items.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(itemsOffset + i * 8), inventory.Items[i]);
        return bytes;
    }
    static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8));
}
#endif
