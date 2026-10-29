using System.Buffers.Binary;
using System.Numerics;

namespace FH6LocalCryptoTool;

/// <summary>
/// FH6 version-3 road-discovery bitmap editing. The two inspected
/// saves have [u32 version=3][u32 entryCount] followed by a fixed bitmap region.
/// Structural checks and byte-preservation are verified; the user confirmed the
/// in-game map/statistic and persistence. The Brio layout targets 671 roads, not the
/// number of bitmap entries. Other progression counters/rewards are preserved.
/// </summary>
public static class RoadDiscoverySaveState
{
    public const string RecordName = "RoadDiscoveryBrio";
    public const string SerializerName = "RoadDiscoveryState";
    public const string StatisticPath = "/CompareStats/RoadsDiscovered";
    public const uint RoadCount = 671;
    public const int KnownEntryCount = 55089;
    const int HeaderBytes = 8;
    const int KnownPayloadBytes = 8201;

    public readonly record struct Info(int EntryCount, int DiscoveredEntries);

    public static bool TryRead(byte[]? payload, out Info info)
    {
        info = default;
        if (payload is null || payload.Length != KnownPayloadBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) != 3)
            return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
        // The total-road statistic is specific to this inspected FH6 map layout.
        // Do not apply it to a different map/version with an unknown road total.
        if (count != KnownEntryCount) return false;
        int completeBytes = (int)count / 8;
        int remainder = (int)count % 8;
        int visited = 0;
        for (int index = 0; index < completeBytes; index++)
            visited += BitOperations.PopCount((uint)payload[HeaderBytes + index]);
        int tailStart = HeaderBytes + completeBytes;
        if (remainder > 0)
        {
            byte mask = (byte)((1 << remainder) - 1);
            if ((payload[tailStart] & ~mask) != 0) return false;
            visited += BitOperations.PopCount((uint)payload[tailStart]);
            tailStart++;
        }
        // Unknown/nonzero data outside the declared entries is not portable.
        // Keep the unused region and final byte unchanged rather than filling
        // the entire payload and potentially changing another field.
        for (int index = tailStart; index < payload.Length; index++)
            if (payload[index] != 0) return false;
        info = new Info((int)count, visited);
        return true;
    }

    public static bool TryDiscoverAll(byte[]? payload, out byte[] edited, out Info before)
    {
        edited = payload ?? Array.Empty<byte>();
        if (!TryRead(payload, out before)) return false;
        edited = (byte[])payload!.Clone();
        int completeBytes = before.EntryCount / 8;
        int remainder = before.EntryCount % 8;
        edited.AsSpan(HeaderBytes, completeBytes).Fill(0xFF);
        if (remainder > 0)
            edited[HeaderBytes + completeBytes] |= (byte)((1 << remainder) - 1);
        return TryRead(edited, out Info after) &&
               after.EntryCount == before.EntryCount &&
               after.DiscoveredEntries == after.EntryCount;
    }

    public static bool TryReadProfile(ProfileContainer? container, out Info info, out uint roads)
        => TryGetTargets(container, out _, out _, out info, out roads);

    /// <summary>Updates the recognized map bitmap and its saved road statistic together.</summary>
    public static bool TryDiscoverAll(ProfileContainer? container, out Info before, out uint previousRoads)
    {
        if (!TryGetTargets(container, out var record, out var statistic, out before, out previousRoads) ||
            !TryDiscoverAll(record!.Payload, out byte[] edited, out _)) return false;

        // Validate and prepare both values before changing either working-copy section.
        byte[] counter = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(counter, RoadCount);
        container!.Career!.ReplacePayload(record, edited);
        statistic!.RawValue = counter;
        statistic.Edited = true;
        return true;
    }

    static bool TryGetTargets(ProfileContainer? container, out BinaryCareer.Record? record,
        out PropertyTree.PropertyNode? statistic, out Info info, out uint roads)
    {
        record = null; statistic = null; info = default; roads = 0;
        if (container?.Career is not { } career || container.Properties is not { } properties) return false;
        var records = career.Records.Where(item => item.Name == RecordName).ToList();
        var counters = properties.Walk().Where(pair => string.Equals(pair.Path, StatisticPath, StringComparison.Ordinal)).ToList();
        if (records.Count != 1 || counters.Count != 1) return false;
        var candidate = counters[0].Node;
        if (records[0].Serializer != SerializerName || !TryRead(records[0].Payload, out info) ||
            candidate.TypeId is not (3 or 17) || candidate.RawValue.Length != 4 || !candidate.IsEditable)
            return false;
        roads = BinaryPrimitives.ReadUInt32LittleEndian(candidate.RawValue);
        if (roads > RoadCount) return false;
        record = records[0]; statistic = candidate;
        return true;
    }
}
