#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace FH6LocalCryptoTool;

/// <summary>
/// Source-only prototypes. Byte preservation is testable; gameplay behavior is not
/// confirmed. Never rewrite unknown records, challenge state, rewards or account data.
/// </summary>
public static class ProfileUnlockTestState
{
    public const string DecalRecordName = "LiveryDecalsSaveState";
    public const string WristbandRecordName = "PlayerWristbandSaveState";
    public const string BadgeRecordName = "BadgeChallengeSaveState";

    // IDs from the inspected FH6 LiveryDecalRewardMap (2026-08-31). This is deliberately
    // a mapped subset, not a claim to include every reward in newer game updates.
    public static IReadOnlyList<uint> MappedRewardDecals { get; } = Array.AsReadOnly(new uint[]
    {
        11024, 11025, 11026, 11041, 11042, 11043, 11044, 11045, 11046,
        11047, 11048, 11049, 11050, 11051, 11052, 11053, 11054, 11055,
        11056, 11058, 11059, 11060, 11061, 11062, 11063, 11064, 11065,
        11066, 11077,
    });

    public readonly record struct Wristband(byte Id, string Name);
    public static IReadOnlyList<Wristband> Wristbands { get; } = Array.AsReadOnly(new[]
    {
        new Wristband(0, "None"), new Wristband(1, "Yellow"),
        new Wristband(2, "Green"), new Wristband(3, "Blue"),
        new Wristband(4, "Pink"), new Wristband(5, "Orange"),
        new Wristband(6, "Purple"), new Wristband(7, "Gold"),
    });

    public static bool TryReadDecals(ProfileContainer? container, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (!TryGetRecord(container, DecalRecordName, DecalRecordName, out var record)) return false;
        return TryReadDecalPayload(record!.Payload, out ids);
    }

    public static bool TryReadDecalPayload(byte[]? payload, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        if (payload is null || payload.Length < 8 || U32(payload, 0) != 1) return false;
        uint count = U32(payload, 4);
        if (count > 10_000 || 8L + count * 4L != payload.Length) return false;
        uint[] parsed = new uint[(int)count];
        for (int i = 0; i < parsed.Length; i++) parsed[i] = U32(payload, 8 + i * 4);
        if (parsed.Any(id => id == 0) || parsed.Distinct().Count() != parsed.Length) return false;
        ids = parsed;
        return true;
    }

    /// <summary>Append mapped reward IDs to an existing version-1 list. Unknown IDs survive.</summary>
    public static bool TryUnlockMappedDecals(ProfileContainer? container, out int added)
    {
        added = 0;
        if (!TryGetRecord(container, DecalRecordName, DecalRecordName, out var record) ||
            !TryReadDecalPayload(record!.Payload, out uint[] existing)) return false;
        uint[] missing = MappedRewardDecals.Except(existing).ToArray();
        if (missing.Length == 0) return true;
        uint[] combined = existing.Concat(missing).ToArray();
        byte[] edited = new byte[checked(8 + combined.Length * 4)];
        WriteU32(edited, 0, 1); WriteU32(edited, 4, (uint)combined.Length);
        for (int i = 0; i < combined.Length; i++) WriteU32(edited, 8 + i * 4, combined[i]);
        if (!TryReadDecalPayload(edited, out _)) return false;
        container!.Career!.ReplacePayload(record, edited);
        added = missing.Length;
        return true;
    }

    public static bool TryReadWristbands(ProfileContainer? container, out byte[] ids)
    {
        ids = Array.Empty<byte>();
        if (!TryGetRecord(container, WristbandRecordName, "WristbandSaveState", out var record)) return false;
        return TryReadWristbandPayload(record!.Payload, out ids);
    }

    public static bool TryReadWristbandPayload(byte[]? payload, out byte[] ids)
    {
        ids = Array.Empty<byte>();
        if (payload is null || payload.Length < 9 || U32(payload, 0) != 1) return false;
        uint count = U32(payload, 4);
        if (count is < 1 or > 8 || 8L + count != payload.Length) return false;
        // Only the two observed contiguous earned-band layouts are understood.
        for (int i = 0; i < (int)count; i++) if (payload[8 + i] != i) return false;
        ids = payload[8..];
        return true;
    }

    /// <summary>
    /// Grant through a mapped band without removing any earned band. This is NOT a
    /// campaign-completion or active-rank setter; linked progression remains unchanged.
    /// </summary>
    public static bool TryGrantWristbands(ProfileContainer? container, byte through, out int added)
    {
        added = 0;
        if (through > 7 || !TryGetRecord(container, WristbandRecordName, "WristbandSaveState", out var record) ||
            !TryReadWristbandPayload(record!.Payload, out byte[] existing)) return false;
        int count = Math.Max(existing.Length, through + 1);
        if (count == existing.Length) return true;
        byte[] edited = new byte[8 + count];
        WriteU32(edited, 0, 1); WriteU32(edited, 4, (uint)count);
        for (int i = 0; i < count; i++) edited[8 + i] = (byte)i;
        if (!TryReadWristbandPayload(edited, out _)) return false;
        container!.Career!.ReplacePayload(record, edited);
        added = count - existing.Length;
        return true;
    }

    static bool TryGetRecord(ProfileContainer? container, string name, string serializer, out BinaryCareer.Record? record)
    {
        record = null;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var matches = career.Records.Where(r => r.Name == name).ToArray();
        if (matches.Length != 1 || matches[0].Serializer != serializer) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == name)).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        string expectedType = name == WristbandRecordName ? "WristbandSaveState" : name;
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != expectedType ||
            values[0].Children.Count != 0 || values[0].Attributes.Count != 1) return false;
        record = matches[0];
        return true;
    }

    /// <summary>Read-only, focused snapshot for controlled before/after comparisons; no full save or account ID.</summary>
    public static string SnapshotJson(ProfileContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        string[] recordNames = { BadgeRecordName, DecalRecordName, WristbandRecordName, "CampaignUnlockSaveState" };
        string[] propertyPaths = { "/Main/PlayerCardBadgeId", "/CompareStats/WristbandLevel" };
        var records = container.Career?.Records.Where(r => recordNames.Contains(r.Name)).Select(r => new
        {
            r.Name, r.Serializer, Length = r.Payload.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(r.Payload)), Payload = r.Payload,
        }).ToArray();
        var properties = container.Properties?.Walk().Where(p => propertyPaths.Contains(p.Path)).Select(p => new
        {
            p.Path, Type = p.Node.TypeName, Value = p.Node.DisplayValue(), RawValue = p.Node.RawValue,
        }).ToArray();
        byte? currentBand = WristbandProgressionTestState.TryReadCurrentBand(container, out byte band) ? band : null;
        string[] campaignKeys = { "CurrencyBucket_FestivalCollectionCampaignProgress_cc_festival",
            "main_outpost_festival_save_state", "legend_island_outpost_festival_save_state" };
        var bx = container.SaveState;
        var campaign = bx?.Root.Children.Where(n => n.Children.Any(c => bx.Strings[c.NameIndex] == "key" &&
            campaignKeys.Contains(bx.GetAttribute(c, "value")))).Select(n => new
        {
            Key = n.Children.Where(c => bx.Strings[c.NameIndex] == "key").Select(c => bx.GetAttribute(c, "value")).Single(),
            Fields = n.Walk().Where(p => bx.Strings[p.Node.NameIndex] == "property").Select(p => new
            {
                Id = bx.GetAttribute(p.Node, "id"), Type = bx.GetAttribute(p.Node, "type"), Value = bx.GetAttribute(p.Node, "value"),
            }).ToArray(),
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            Format = "Forza Mod Tool focused unlock test snapshot", SchemaVersion = 1,
            Note = "Raw challenge data is not interpreted. This snapshot is not an importable save.",
            Records = records, Properties = properties, CurrentWristband = currentBand, CampaignAccess = campaign,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    static uint U32(byte[] payload, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
    static void WriteU32(byte[] payload, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), value);
}
#endif
