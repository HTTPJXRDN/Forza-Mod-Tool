#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

/// <summary>Definition-backed local badge challenge completion; never equips a badge or edits Journal/stat data.</summary>
public static class BadgeUnlockState
{
    const string RecordName = "BadgeChallengeSaveState";
    static readonly Dictionary<ulong, BadgeCatalog.Badge> Definitions = BadgeCatalog.Items.ToDictionary(b => b.SaveId);
    public sealed record Row(uint Id, string Name, string Description, string Category, bool Unlocked, bool Supported,
        DateTimeOffset? EarnedUtc = null);
    public sealed record EarnedDate(uint Id, string Name, DateTimeOffset Utc, bool Playlist);
    sealed record Entry(BadgeCatalog.Badge Definition, int[] ObjectiveOffsets, int TailOffset, bool Supported);

    public static bool TryRead(ProfileContainer? container, out Row[] rows)
    {
        rows = Array.Empty<Row>();
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var entries)) return false;
        rows = entries!.Select(e => new Row(e.Definition.Id, e.Definition.Name, e.Definition.Description,
            e.Definition.Category, U64(record!.Payload, e.TailOffset + 9) != 0, e.Supported,
            DecodeDate(U64(record!.Payload, e.TailOffset + 17)))).OrderBy(r => r.Id).ToArray();
        return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds, out int added)
        => TryUnlockSelected(container, selectedIds, null, out added, out _);

    /// <summary>Plans new completions only. Existing earned dates are never restamped by unlocking.</summary>
    public static bool TryPlanUnlockDates(ProfileContainer? container, IEnumerable<ulong>? selectedIds,
        DateTimeOffset? earnedUtc, out EarnedDate[] dates, out string error)
    {
        dates = Array.Empty<EarnedDate>();
        if (!TryTargets(container, selectedIds, out var record, out var targets, out error)) return false;
        var planned = new List<EarnedDate>(); var now = DateTimeOffset.UtcNow;
        foreach (var entry in targets!)
        {
            if (U64(record!.Payload, entry.TailOffset + 9) != 0) continue;
            if (!BadgeEarnedDates.TryChoose(entry.Definition.Id, earnedUtc, now, out var date, out error)) return false;
            planned.Add(new(entry.Definition.Id, entry.Definition.Name, date, entry.Definition.Category == "Festival Playlist"));
        }
        dates = planned.ToArray(); return true;
    }

    public static bool TryUnlockSelected(ProfileContainer? container, IEnumerable<ulong>? selectedIds,
        DateTimeOffset? earnedUtc, out int added, out string error)
    {
        added = 0;
        // Materialize caller input once, so validation and application use exactly the same selection.
        var selected = selectedIds?.ToArray();
        if (!TryPlanUnlockDates(container, selected, earnedUtc, out var dates, out error) ||
            !TryTargets(container, selected, out var record, out var targets, out error)) return false;
        byte[] bytes = (byte[])record!.Payload.Clone(); int count = 0;
        foreach (var entry in targets!)
        {
            if (U64(bytes, entry.TailOffset + 9) != 0) continue;
            for (int i = 0; i < entry.ObjectiveOffsets.Length; i++)
            {
                int at = entry.ObjectiveOffsets[i]; var objective = entry.Definition.Objectives[i];
                bytes[at + 8] = 1;
                uint value = objective.Exact ? objective.Target : Math.Max(U32(bytes, at + 10), objective.Target);
                Put32(bytes, at + 10, value);
            }
            Put64(bytes, entry.TailOffset + 1, 0);
            Put64(bytes, entry.TailOffset + 9, 1);
            Put64(bytes, entry.TailOffset + 17, EncodeDate(dates.Single(d => d.Id == entry.Definition.Id).Utc));
            count++;
        }
        if (count == 0) return true;
        if (!TryParse(bytes, out _)) return false;
        container!.Career!.ReplacePayload(record, bytes);
        added = count; return true;
    }

    /// <summary>Edits only the eight-byte earned timestamps of already-unlocked, supported badges.</summary>
    public static bool TrySetEarnedDate(ProfileContainer? container, IEnumerable<ulong>? selectedIds,
        DateTimeOffset earnedUtc, out int changed, out string error)
    {
        changed = 0;
        if (!TryTargets(container, selectedIds, out var record, out var targets, out error)) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in targets!)
        {
            if (U64(record!.Payload, entry.TailOffset + 9) == 0)
            { error = "Select unlocked badges to change their earned date. Use Unlock selected badges for locked ones."; return false; }
            if (!BadgeEarnedDates.TryChoose(entry.Definition.Id, earnedUtc, now, out _, out error)) return false;
        }
        byte[] bytes = (byte[])record!.Payload.Clone(); ulong timestamp = EncodeDate(earnedUtc); int count = 0;
        foreach (var entry in targets!)
        {
            if (U64(bytes, entry.TailOffset + 17) == timestamp) continue;
            Put64(bytes, entry.TailOffset + 17, timestamp); count++;
        }
        if (count == 0) return true;
        if (!TryParse(bytes, out _)) return false;
        container!.Career!.ReplacePayload(record, bytes); changed = count; return true;
    }

    static bool TryTargets(ProfileContainer? container, IEnumerable<ulong>? selectedIds,
        out BinaryCareer.Record? record, out Entry[]? targets, out string error)
    {
        record = null; targets = null; error = "The badge layout or selection is not safely supported. Nothing was changed.";
        var selected = selectedIds?.ToHashSet();
        if (selected is null || selected.Count == 0 || selected.Any(id => !BadgeCatalog.Items.Any(b => b.Id == id))) return false;
        if (!TryRecord(container, out record) || !TryParse(record!.Payload, out var entries)) return false;
        targets = entries!.Where(e => selected.Contains(e.Definition.Id)).ToArray();
        if (targets.Length != selected.Count)
        { error = "A selected badge has no challenge record in this save. Nothing was changed."; return false; }
        // A changed live-service definition is displayed but never guessed or downgraded.
        if (targets.Any(e => !e.Supported))
        { error = "A selected badge definition has changed. Its progress and date are preserved."; return false; }
        error = ""; return true;
    }

    static DateTimeOffset? DecodeDate(ulong timestamp)
        => timestamp is > 0 and <= 253402300799999999UL ? DateTimeOffset.UnixEpoch.AddTicks((long)timestamp * 10) : null;
    static ulong EncodeDate(DateTimeOffset date) => checked((ulong)((date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10));

    static bool TryRecord(ProfileContainer? container, out BinaryCareer.Record? record)
    {
        record = null;
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray();
        if (records.Length != 1 || records[0].Serializer != "BaseChallengeSaveState") return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == RecordName) == 1).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != RecordName || values[0].Children.Count != 0 || values[0].Attributes.Count != 1) return false;
        record = records[0]; return true;
    }

    static bool TryParse(byte[] bytes, out Entry[]? entries)
    {
        entries = null;
        // The leading four-byte generation field varies between observed saves;
        // preserve it opaquely. Older saves can contain only a subset of definitions.
        // Validate every stored identity/objective, and never synthesize missing badges.
        if (bytes.Length < 16 || U32(bytes, 4) != 1) return false;
        uint count = U32(bytes, 8);
        if (count == 0 || count > Definitions.Count) return false;
        var parsed = new List<Entry>(); var seen = new HashSet<ulong>(); int at = 12;
        for (int index = 0; index < count; index++)
        {
            if (at > bytes.Length - 32) return false;
            ulong id = U64(bytes, at);
            if (!Definitions.TryGetValue(id, out var def) || !seen.Add(id) || U64(bytes, at + 8) != id || U64(bytes, at + 16) != def.LinkedId || U32(bytes, at + 28) != def.Objectives.Length) return false;
            bool supported = U32(bytes, at + 24) == def.Version;
            at += 32; var offsets = new List<int>();
            foreach (var objective in def.Objectives)
            {
                if (at > bytes.Length - 14 || U64(bytes, at) != objective.Id || bytes[at + 8] > 1 || bytes[at + 9] is not (1 or 2 or 3)) return false;
                offsets.Add(at); at += 14;
            }
            if (at > bytes.Length - 29 || bytes[at] != 1 || U64(bytes, at + 9) > 1) return false;
            ulong active = U64(bytes, at + 1);
            if (active != 0 && !def.Objectives.Any(o => o.Id == active)) return false;
            parsed.Add(new(def, offsets.ToArray(), at, supported)); at += 29;
        }
        // Observed trailing metadata is a counted u32 list, empty in some saves.
        // Its meaning is not assumed; edits preserve the whole suffix verbatim.
        if (at > bytes.Length - 4 || U32(bytes, at) > 100_000 ||
            bytes.Length - at != 4L + U32(bytes, at) * 4L) return false;
        entries = parsed.ToArray(); return true;
    }
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
    static void Put32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at, 4), value);
    static void Put64(byte[] b, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at, 8), value);
}
#endif
