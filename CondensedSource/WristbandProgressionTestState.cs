#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>
/// Source-only wristband/access editor, mapped from FH6 EntityModel and two save
/// layouts. Serialization is checked in tests; the user verified current-band and
/// access changes in-game. Does not fabricate completed races or paid rewards.
/// </summary>
public static class WristbandProgressionTestState
{
    const string StatsName = "Stats";
    const string FeaturesName = "CampaignUnlockSaveState";
    const string FestivalCurrencyName = "CurrencyBucket_FestivalCollectionCampaignProgress_cc_festival";
    const string LegendFeature = "FestivalLegend";
    // FNV-1a identifiers confirmed against GameTunableSettings/Stats.xml and both saves.
    const ulong CurrentBandStat = 0x5FB89E7F5981EAF8; // Global/CurrentWristband
    const ulong BrioTrack = 0x949DBE1845545D4D;       // 820
    const ulong FollowingStat = 0x60942A5B203C8F1C;
    static readonly int[] FestivalThresholds = { 0, 1250, 2500, 6000, 10000, 15000, 20000, 32500 };

    public readonly record struct Change(byte PreviousBand, byte SelectedBand, int BandsAdded,
        int BandsRemoved, int PreviousFestivalPoints, int FestivalPoints, bool Changed)
    {
        public bool Downgrade => BandsRemoved > 0 || SelectedBand < PreviousBand;
    }

    public static bool TryReadCurrentBand(ProfileContainer? container, out byte band)
    {
        band = 0;
        return TryRecord(container, StatsName, "StatsSaveState", out var record) &&
            TryBandStat(record!.Payload, out _, out _, out band);
    }

    public static bool CanSetBand(ProfileContainer? container, out string reason) =>
        TryPrepare(container, 7, out _, out reason);

    /// <summary>Journal-driven tier synchronization; preserve the exact earned point total.</summary>
    public static bool TrySyncFromFestivalPoints(ProfileContainer? container, out Change change, out string reason)
    {
        change = default;
        reason = "Festival points are not safely recognized.";
        if (container?.SaveState is not { } bx) return false;
        var currency = MapValue(bx, FestivalCurrencyName, "CurrencyBucket");
        string? text = PropertyValue(bx, currency, "Total", "int");
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int points) || points < 0) return false;
        byte selected = 1; // Yellow is the starting wristband, not an unlockable None tier.
        for (byte tier = 2; tier <= 7; tier++) if (points >= FestivalThresholds[tier]) selected = tier;
        if (!TryPrepare(container, selected, out var plan, out reason, keepPoints: true)) return false;
        foreach (var edit in plan!.Records)
            if (!edit.Record.Payload.AsSpan().SequenceEqual(edit.Payload)) container.Career!.ReplacePayload(edit.Record, edit.Payload);
        foreach (var edit in plan.Attributes) bx.SetAttribute(edit.Node, "value", edit.Value);
        change = plan.Result;
        return true;
    }

    /// <summary>
    /// Set the selected tier exactly, including downgrades. Higher earned bands are
    /// removed, but completed events, reward claims, discovery and inventories survive.
    /// Validate ALL targeted layouts before mutating the working copy.
    /// </summary>
    public static bool TrySetBand(ProfileContainer? container, byte selected, out Change change, out string reason)
    {
        change = default;
        if (!TryPrepare(container, selected, out var plan, out reason)) return false;
        foreach (var edit in plan!.Records)
            if (!edit.Record.Payload.AsSpan().SequenceEqual(edit.Payload))
                container!.Career!.ReplacePayload(edit.Record, edit.Payload);
        foreach (var edit in plan.Attributes)
            container!.SaveState!.SetAttribute(edit.Node, "value", edit.Value);
        change = plan.Result;
        return true;
    }

    sealed class Plan
    {
        public List<(BinaryCareer.Record Record, byte[] Payload)> Records = new();
        public List<(Bxml.Node Node, string Value)> Attributes = new();
        public Change Result;
    }

    static bool TryPrepare(ProfileContainer? container, byte selected, out Plan? plan, out string reason, bool keepPoints = false)
    {
        plan = null;
        reason = "Unsupported wristband/campaign save layout; nothing was changed.";
        if (selected > 7 || container?.SaveState is not { } bx || container.Career is null ||
            !TryRecord(container, ProfileUnlockTestState.WristbandRecordName, "WristbandSaveState", out var bandsRecord) ||
            !ProfileUnlockTestState.TryReadWristbandPayload(bandsRecord!.Payload, out byte[] bands) ||
            !TryRecord(container, StatsName, "StatsSaveState", out var statsRecord) ||
            !TryBandStat(statsRecord!.Payload, out int statOffset, out bool statPresent, out byte current) ||
            !TryRecord(container, FeaturesName, "CampaignFeatureUnlockSaveState", out var featuresRecord) ||
            !TryFeatures(featuresRecord!.Payload, out string[] features)) return false;

        var currency = MapValue(bx, FestivalCurrencyName, "CurrencyBucket");
        var total = Property(bx, currency, "Total", "int");
        if (PropertyValue(bx, currency, "Name", "string") != "cc_festival" ||
            PropertyValue(bx, currency, "Type", "CurrencyTypes") != "FestivalCollectionCampaignProgress" ||
            total is null || !int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None,
                CultureInfo.InvariantCulture, out int points) || points < 0) return false;

        var candidate = new Plan();
        // Existing records only; don't create guessed structures for incomplete saves.
        foreach (var site in new[] { ("main_outpost_festival_save_state", selected >= 1),
                                    ("legend_island_outpost_festival_save_state", selected == 7) })
        {
            var value = MapValue(bx, site.Item1, "FestivalSiteState");
            foreach (string field in new[] { "Activated", "Built" })
            {
                var node = Property(bx, value, field, "bool");
                if (node is null || bx.GetAttribute(node, "value") is not ("true" or "false")) return false;
                candidate.Attributes.Add((node, site.Item2 ? "true" : "false"));
            }
        }

        byte[] earned = new byte[9 + selected];
        Put32(earned, 0, 1); Put32(earned, 4, (uint)(selected + 1));
        for (int i = 0; i <= selected; i++) earned[8 + i] = (byte)i;
        candidate.Records.Add((bandsRecord, earned));
        byte[] statsPayload = SetBandStat(statsRecord.Payload, statOffset, statPresent, selected);
        if (!TryBandStat(statsPayload, out _, out _, out byte verifiedBand) || verifiedBand != selected) return false;
        candidate.Records.Add((statsRecord, statsPayload));
        var targetFeatures = features.Where(f => f != LegendFeature).ToList();
        if (selected == 7) targetFeatures.Add(LegendFeature);
        targetFeatures.Sort(StringComparer.Ordinal);
        // Keep the original payload byte-for-byte when membership has not changed.
        byte[] featurePayload = targetFeatures.Count == features.Length && targetFeatures.All(features.Contains)
            ? featuresRecord.Payload : FeaturePayload(targetFeatures);
        if (!TryFeatures(featurePayload, out _)) return false;
        candidate.Records.Add((featuresRecord, featurePayload));

        byte priorTier = (byte)Math.Max(current, bands[^1]);
        int threshold = FestivalThresholds[selected];
        int targetPoints = keepPoints ? points : selected < priorTier || points < threshold ||
            (selected < 7 && points >= FestivalThresholds[selected + 1]) ? threshold : points;
        candidate.Attributes.Add((total, targetPoints.ToString(CultureInfo.InvariantCulture)));
        bool changed = candidate.Records.Any(e => !e.Record.Payload.AsSpan().SequenceEqual(e.Payload)) ||
            candidate.Attributes.Any(e => bx.GetAttribute(e.Node, "value") != e.Value);
        candidate.Result = new Change(current, selected, Math.Max(0, selected + 1 - bands.Length),
            Math.Max(0, bands.Length - selected - 1), points, targetPoints, changed);
        plan = candidate;
        reason = "";
        return true;
    }

    // Narrow version-2 StatsSaveState signature. The number of unrelated stats
    // varies between accounts/updates (685 and 692 are both observed); it is not
    // a format version. Unknown branches still fail closed. The named neighbor
    // bounds the entry, including when no
    // Track=820 value exists yet. No blind replace of arbitrary occurrences of 0/7.
    static bool TryBandStat(byte[] data, out int offset, out bool present, out byte band)
    {
        offset = -1; present = false; band = 0;
        if (data.Length < 32 || U32(data, 0) != 2 || U32(data, 4) is 0 or > 100_000 || U32(data, 4) > (data.Length - 8) / 12) return false;
        byte[] id = BitConverter.GetBytes(CurrentBandStat);
        int match = data.AsSpan(8).IndexOf(id);
        if (match < 0) return false;
        offset = 8 + match;
        if (data.AsSpan(offset + 8).IndexOf(id) >= 0 || offset > data.Length - 24) return false;
        uint count = U32(data, offset + 8);
        int end = offset + 12;
        if (count == 1)
        {
            if (end > data.Length - 28 || U64(data, end) != BrioTrack) return false;
            long value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(end + 8, 8));
            if (value is < 0 or > 7) return false;
            band = (byte)value; present = true; end += 16;
        }
        else if (count != 0) return false;
        return end <= data.Length - 12 && U64(data, end) == FollowingStat && U32(data, end + 8) <= 100_000;
    }

    static byte[] SetBandStat(byte[] data, int offset, bool present, byte band)
    {
        if (!present && band == 0) return data;
        if (present)
        {
            byte[] edited = (byte[])data.Clone();
            BinaryPrimitives.WriteInt64LittleEndian(edited.AsSpan(offset + 20, 8), band);
            return edited;
        }
        byte[] inserted = new byte[checked(data.Length + 16)];
        int end = offset + 12;
        data.AsSpan(0, end).CopyTo(inserted);
        Put32(inserted, offset + 8, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(inserted.AsSpan(end, 8), BrioTrack);
        BinaryPrimitives.WriteInt64LittleEndian(inserted.AsSpan(end + 8, 8), band);
        data.AsSpan(end).CopyTo(inserted.AsSpan(end + 16));
        return inserted;
    }

    // Observed CampaignFeatureUnlockSaveState framing: three u32 words, middle
    // word counts length-prefixed UTF-8 flags. Both outside words must be zero.
    // This is an observational prototype, not a general decoder of every version.
    static bool TryFeatures(byte[] data, out string[] features)
    {
        features = Array.Empty<string>();
        if (data.Length < 12 || U32(data, 0) != 0 || U32(data, 8) != 0 || U32(data, 4) > 1000) return false;
        var parsed = new List<string>();
        int offset = 12;
        for (uint i = 0; i < U32(data, 4); i++)
        {
            if (offset > data.Length - 4) return false;
            uint len = U32(data, offset); offset += 4;
            if (len is < 1 or > 256 || len > data.Length - offset) return false;
            string flag = Encoding.ASCII.GetString(data, offset, (int)len);
            if (flag.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || parsed.Contains(flag)) return false;
            parsed.Add(flag); offset += (int)len;
        }
        if (offset != data.Length) return false;
        features = parsed.ToArray();
        return true;
    }

    static byte[] FeaturePayload(List<string> flags)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.ASCII);
        writer.Write(0u); writer.Write((uint)flags.Count); writer.Write(0u);
        foreach (string flag in flags) { byte[] text = Encoding.ASCII.GetBytes(flag); writer.Write((uint)text.Length); writer.Write(text); }
        return buffer.ToArray();
    }

    static bool TryRecord(ProfileContainer? container, string name, string serializer, out BinaryCareer.Record? record)
    {
        record = null;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == name).ToArray();
        var value = MapValue(bx, name, serializer);
        if (records.Length != 1 || records[0].Serializer != serializer || value is null ||
            value.Children.Count != 0 || value.Attributes.Count != 1) return false;
        record = records[0]; return true;
    }

    static Bxml.Node? MapValue(Bxml bx, string key, string type)
    {
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key)).ToArray();
        if (maps.Length != 1) return null;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        return values.Length == 1 && bx.GetAttribute(values[0], "type") == type ? values[0] : null;
    }

    static Bxml.Node? Property(Bxml bx, Bxml.Node? parent, string id, string type)
    {
        if (parent is null) return null;
        var nodes = parent.Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
        return nodes.Length == 1 && bx.GetAttribute(nodes[0], "type") == type && nodes[0].Children.Count == 0 &&
            nodes[0].Attributes.Count(a => bx.Strings[a.KeyIndex] == "value") == 1 ? nodes[0] : null;
    }
    static string? PropertyValue(Bxml bx, Bxml.Node? parent, string id, string type) =>
        Property(bx, parent, id, type) is { } node ? bx.GetAttribute(node, "value") : null;
    static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i, 4));
    static ulong U64(byte[] b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i, 8));
    static void Put32(byte[] b, int i, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i, 4), value);
}
#endif
