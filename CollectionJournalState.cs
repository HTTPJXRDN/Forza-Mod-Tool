#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Local FH6 Collection Journal completion prototype; no save/game file writes.</summary>
public static class CollectionJournalState
{
    const string RecordName = "CollectionCampaignSaveState";
    const string Serializer = "BaseChallengeSaveState";
    static readonly Dictionary<ulong, CollectionJournalCatalog.Challenge> Definitions = CollectionJournalCatalog.Data.Challenges.ToDictionary(c => c.Id);
    static readonly Dictionary<ulong, CollectionJournalCatalog.Category> Categories = CollectionJournalCatalog.Data.Categories.ToDictionary(c => c.Id);
    static readonly HashSet<string> AllowedRecords = new() { RecordName, "Stats", "PlayerWristbandSaveState", "CampaignUnlockSaveState" };
    public sealed record Row(ulong Id, string Campaign, string Category, string Name, string Objective, string Subtitle, int Points, bool Completed, bool Automatic);
    public sealed record View(Row[] Rows, int FestivalPoints, int DiscoveryPoints, int Completed, bool TotalsAgree);
    public readonly record struct Change(int SelectedCompleted, int AutomaticCompleted, int PreviousFestivalPoints, int FestivalPoints,
        int PreviousDiscoveryPoints, int DiscoveryPoints, byte PreviousBand, byte Band, bool Changed);
    sealed record Objective(ulong Id, int FlagOffset, int ValueOffset, byte Tag);
    sealed record Entry(CollectionJournalCatalog.Challenge Definition, Objective[] Objectives, int TailOffset);
    sealed record State(BinaryCareer.Record Record, byte[] Payload, Entry[] Entries, byte[] Metadata);
    sealed record AttributeEdit(Bxml.Node Node, string Value);
    sealed record Plan(List<(BinaryCareer.Record Record, byte[] Bytes)> Records, List<AttributeEdit> Attributes, Change Result);

    public static bool TryRead(ProfileContainer? container, out View? view, out string reason)
    {
        view = null;
        if (!TryState(container, out var state, out reason) || !TryTotals(container!, out var totals, out reason)) return false;
        var points = Points(state!);
        var rows = state!.Entries.Select(e =>
        {
            var category = Categories[e.Definition.Category];
            return new Row(e.Definition.Id, category.Festival ? "Horizon Festival" : "Discover Japan", FriendlyCategory(category),
                e.Definition.Name, e.Definition.Description, e.Definition.Subtitle, e.Definition.Points, Completed(state.Payload, e), Automatic(e.Definition));
        }).ToArray();
        view = new(rows, Festival(points), Discovery(points), rows.Count(r => r.Completed),
            Categories.Values.All(c => totals![CategoryKey(c)] == points.GetValueOrDefault(c.Id)) &&
            totals![FestivalKey] == Festival(points) && totals[DiscoveryKey] == Discovery(points));
        return true;
    }

    public static bool TryPreview(ProfileContainer? container, IEnumerable<ulong> ids, out Change change, out string reason)
    {
        change = default;
        if (!TryPlan(container, ids, out var plan, out reason)) return false;
        change = plan!.Result;
        return true;
    }

    public static bool TryComplete(ProfileContainer? container, IEnumerable<ulong> ids, out Change change, out string reason)
    {
        change = default;
        if (!TryPlan(container, ids, out var plan, out reason)) return false;
        // Every record and attribute has already been validated on a private
        // parsed clone. Commit only the scoped differences, keeping original UI
        // node references and all unrelated pending edits intact.
        foreach (var edit in plan!.Attributes) container!.SaveState!.SetAttribute(edit.Node, "value", edit.Value);
        foreach (var edit in plan.Records) container!.Career!.ReplacePayload(edit.Record, edit.Bytes);
        change = plan.Result;
        return true;
    }

    static bool TryPlan(ProfileContainer? container, IEnumerable<ulong> ids, out Plan? plan, out string reason)
    {
        plan = null;
        reason = "Journal layout or progression fields are not safely recognized; nothing was changed.";
        if (!TryState(container, out _, out reason) || !TryTotals(container!, out var oldTotals, out reason)) return false;
        var selected = ids?.ToHashSet();
        if (selected is null || selected.Count == 0 || selected.Count > Definitions.Count || selected.Any(id => !Definitions.ContainsKey(id)))
        { reason = "Select at least one mapped journal challenge."; return false; }
        var staged = ProfileContainer.Parse(container!.Serialize());
        if (!TryState(staged, out var state, out reason)) return false;
        if (!WristbandProgressionTestState.TryReadCurrentBand(staged, out byte oldBand))
        { reason = "The current wristband statistic is not safely recognized."; return false; }
        var entries = state!.Entries.ToDictionary(e => e.Definition.Id);
        var baselineCounts = state.Entries.Where(e => Completed(state.Payload, e)).GroupBy(e => e.Definition.Category).ToDictionary(g => g.Key, g => (uint)g.Count());
        int explicitCount = 0, automaticCount = 0;
        ulong now = checked((ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000));
        foreach (ulong id in selected)
        {
            var entry = entries[id];
            if (Completed(state.Payload, entry)) continue;
            if (Automatic(entry.Definition)) { reason = "Category-completion bonuses are automatic. Complete their underlying challenges instead."; return false; }
            Complete(state.Payload, entry, now);
            explicitCount++;
        }
        if (explicitCount == 0)
        {
            plan = new(new(), new(), new(0, 0, oldTotals![FestivalKey], oldTotals[FestivalKey], oldTotals[DiscoveryKey], oldTotals[DiscoveryKey], oldBand, oldBand, false));
            return true;
        }
        // Definition-backed category-count objectives form the journal's bonus
        // dependency graph. Re-evaluate until no additional bonus is completed.
        // Existing completed states and their timestamps are never re-awarded.
        bool added;
        int passes = 0;
        do
        {
            added = false;
            var counts = state.Entries.Where(e => Completed(state.Payload, e)).GroupBy(e => e.Definition.Category).ToDictionary(g => g.Key, g => (uint)g.Count());
            var deltas = Categories.Keys.ToDictionary(id => id, id => counts.GetValueOrDefault(id) - baselineCounts.GetValueOrDefault(id));
            uint totalDelta = checked((uint)deltas.Values.Sum(v => (long)v));
            foreach (var entry in state.Entries.Where(e => Automatic(e.Definition) && !Completed(state.Payload, e)))
            {
                bool complete = true, affected = false;
                for (int i = 0; i < entry.Objectives.Length; i++)
                {
                    var definition = entry.Definition.Objectives[i];
                    uint delta = definition.GlobalDependency ? totalDelta : checked((uint)definition.Dependencies.Sum(id => (long)deltas.GetValueOrDefault(id)));
                    var objective = entry.Objectives[i];
                    if (objective.Tag is not (1 or 2 or 3)) { reason = "Unknown category dependency value layout."; return false; }
                    // These objectives consume completion events. Preserve the
                    // game's original counter, adding only newly completed IDs;
                    // don't reset unrelated historical progress to a guessed count.
                    uint originalValue = U32(state.Record.Payload, objective.ValueOffset);
                    if (delta > uint.MaxValue - originalValue) { reason = "Category bonus progress would overflow; nothing was changed."; return false; }
                    uint value = originalValue + delta;
                    affected |= delta > 0;
                    Put32(state.Payload, objective.ValueOffset, value);
                    state.Payload[objective.FlagOffset] = value >= definition.Target ? (byte)1 : (byte)0;
                    complete &= value >= definition.Target;
                }
                if (!complete || !affected) continue;
                Complete(state.Payload, entry, now, preserveObjectiveValues: true);
                automaticCount++; added = true;
            }
            if (++passes > 100) { reason = "Journal dependency graph did not converge."; return false; }
        } while (added);
        staged.Career!.ReplacePayload(state.Record, state.Payload);
        if (!TryState(staged, out var verified, out reason) || !verified!.Metadata.AsSpan().SequenceEqual(state.Metadata)) return false;
        var points = Points(verified);
        foreach (var category in Categories.Values)
            if (!SetTotal(staged.SaveState!, CategoryKey(category), points.GetValueOrDefault(category.Id))) return false;
        if (!SetTotal(staged.SaveState!, FestivalKey, Festival(points)) || !SetTotal(staged.SaveState!, DiscoveryKey, Discovery(points)) ||
            !WristbandProgressionTestState.TrySyncFromFestivalPoints(staged, out var bandChange, out reason)) return false;
        byte[] stagedBytes = staged.Serialize();
        var roundTrip = ProfileContainer.Parse(stagedBytes);
        if (!roundTrip.Serialize().AsSpan().SequenceEqual(stagedBytes) || !TryState(roundTrip, out _, out reason)) return false;
        var recordEdits = new List<(BinaryCareer.Record, byte[])>();
        foreach (var record in staged.Career.Records)
        {
            var originals = container.Career!.Records.Where(r => r.Name == record.Name).ToArray();
            if (originals.Length != 1 || originals[0].Serializer != record.Serializer) return false;
            if (originals[0].Payload.AsSpan().SequenceEqual(record.Payload)) continue;
            if (!AllowedRecords.Contains(record.Name)) return false;
            recordEdits.Add((originals[0], record.Payload));
        }
        var attributeEdits = new List<AttributeEdit>();
        foreach (string key in oldTotals!.Keys)
        {
            var old = TotalNode(container.SaveState!, key); var next = TotalNode(staged.SaveState!, key);
            if (old is null || next is null) return false;
            string value = staged.SaveState!.GetAttribute(next, "value")!;
            if (container.SaveState!.GetAttribute(old, "value") != value) attributeEdits.Add(new(old, value));
        }
        foreach (string site in new[] { "main_outpost_festival_save_state", "legend_island_outpost_festival_save_state" })
            foreach (string id in new[] { "Activated", "Built" })
            {
                var old = Field(container.SaveState!, Map(container.SaveState!, site), id, "bool");
                var next = Field(staged.SaveState!, Map(staged.SaveState!, site), id, "bool");
                if (old is null || next is null) return false;
                string value = staged.SaveState!.GetAttribute(next, "value")!;
                if (container.SaveState!.GetAttribute(old, "value") != value) attributeEdits.Add(new(old, value));
            }
        if (!staged.Sections[0].Payload.AsSpan().SequenceEqual(container.Sections[0].Payload) ||
            !staged.Sections[3].Payload.AsSpan().SequenceEqual(container.Sections[3].Payload) || staged.Career.Xuid != container.Career!.Xuid) return false;
        plan = new(recordEdits, attributeEdits, new(explicitCount, automaticCount, oldTotals[FestivalKey], Festival(points),
            oldTotals[DiscoveryKey], Discovery(points), oldBand, bandChange.SelectedBand, true));
        reason = "";
        return true;
    }

    static void Complete(byte[] payload, Entry entry, ulong timestamp, bool preserveObjectiveValues = false)
    {
        for (int i = 0; i < entry.Objectives.Length; i++)
        {
            var objective = entry.Objectives[i]; payload[objective.FlagOffset] = 1;
            if (preserveObjectiveValues) continue;
            if (objective.Tag == 13) payload[objective.ValueOffset] = 1;
            else Put32(payload, objective.ValueOffset, entry.Definition.Objectives[i].Target);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(entry.TailOffset + 1), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(entry.TailOffset + 9), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(entry.TailOffset + 17), timestamp);
        // The final definition fingerprint remains byte-exact (as it did during
        // the observed natural photo completion). No cache or reward metadata is rewritten.
    }

    static bool TryState(ProfileContainer? container, out State? state, out string reason)
    {
        state = null; reason = "Collection Journal layout/catalog mismatch; editing is disabled.";
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray(); var placeholder = Map(bx, RecordName);
        if (records.Length != 1 || records[0].Serializer != Serializer || placeholder is null ||
            bx.GetAttribute(placeholder, "type") != RecordName || placeholder.Children.Count != 0 || placeholder.Attributes.Count != 1) return false;
        byte[] b = (byte[])records[0].Payload.Clone();
        if (b.Length < 12 || U32(b, 0) != 2 || U32(b, 4) != 1 || U32(b, 8) != Definitions.Count) return false;
        var seen = new HashSet<ulong>(); var entries = new List<Entry>(); int at = 12;
        for (int i = 0; i < Definitions.Count; i++)
        {
            if (at > b.Length - 32) return false;
            ulong id = U64(b, at);
            if (!Definitions.TryGetValue(id, out var definition) || !seen.Add(id) || U64(b, at + 8) != id ||
                U64(b, at + 16) != definition.LinkedId || U32(b, at + 24) != definition.Version || U32(b, at + 28) != definition.Objectives.Length) return false;
            at += 32; var objectives = new List<Objective>();
            foreach (var objective in definition.Objectives)
            {
                if (at > b.Length - 11 || U64(b, at) != objective.Id || b[at + 8] > 1) return false;
                byte tag = b[at + 9]; int size = tag switch { 1 or 2 or 3 => 4, 13 => 1, _ => -1 };
                if (size < 0 || at > b.Length - 10 - size || (tag == 13 && b[at + 10] > 1) ||
                    (objective.Kind is "CarCollectionObjective" or "TakePhotoObjective") != (tag == 13)) return false;
                objectives.Add(new(objective.Id, at + 8, at + 10, tag)); at += 10 + size;
            }
            if (at > b.Length - 29 || b[at] != 1 || U64(b, at + 9) > 1_000_000) return false;
            ulong active = U64(b, at + 1);
            if (active != 0 && !objectives.Any(o => o.Id == active)) return false;
            entries.Add(new(definition, objectives.ToArray(), at)); at += 29;
        }
        // Carry opaque trailing thumbnail/reward metadata unchanged, but bound
        // it and require an existing thumbnail-map count. Never invent missing metadata.
        if (at > b.Length - 4 || b.Length - at > 2_000_000 || U32(b, at) > 100_000) return false;
        state = new(records[0], b, entries.ToArray(), b[at..]); reason = ""; return true;
    }

    static bool Completed(byte[] b, Entry entry) => U64(b, entry.TailOffset + 9) > 0;
    static bool Automatic(CollectionJournalCatalog.Challenge c) => c.Objectives.All(o => o.Dependencies.Length > 0 || o.GlobalDependency);
    static Dictionary<ulong, int> Points(State state) => state.Entries.Where(e => Completed(state.Payload, e)).GroupBy(e => e.Definition.Category)
        .ToDictionary(g => g.Key, g => g.Sum(e => e.Definition.Points));
    static int Festival(Dictionary<ulong, int> points) => Categories.Values.Where(c => c.Festival).Sum(c => points.GetValueOrDefault(c.Id));
    static int Discovery(Dictionary<ulong, int> points) => Categories.Values.Where(c => !c.Festival).Sum(c => points.GetValueOrDefault(c.Id));
    static string FriendlyCategory(CollectionJournalCatalog.Category c) => c.Name.StartsWith("CollectionCampaignCategory.", StringComparison.Ordinal) ? "Drift Attack" : c.Name;
    const string FestivalKey = "CurrencyBucket_FestivalCollectionCampaignProgress_cc_festival";
    const string DiscoveryKey = "CurrencyBucket_DiscoveryCollectionCampaignProgress_cc_discovery";
    static string CategoryKey(CollectionJournalCatalog.Category c) => "CurrencyBucket_CollectionCategoryProgress_" + c.Bucket;
    static bool TryTotals(ProfileContainer container, out Dictionary<string, int>? totals, out string reason)
    {
        totals = null; reason = "Journal category/campaign currencies are not safely recognized.";
        if (container.SaveState is not { } bx) return false;
        var result = new Dictionary<string, int>();
        foreach (var definition in Categories.Values.Select(c => (Key: CategoryKey(c), Name: c.Bucket, Type: "CollectionCategoryProgress"))
            .Append((Key: FestivalKey, Name: "cc_festival", Type: "FestivalCollectionCampaignProgress"))
            .Append((Key: DiscoveryKey, Name: "cc_discovery", Type: "DiscoveryCollectionCampaignProgress")))
        {
            var value = Map(bx, definition.Key); var total = TotalNode(bx, definition.Key);
            var name = Field(bx, value, "Name", "string"); var type = Field(bx, value, "Type", "CurrencyTypes");
            if (value is null || bx.GetAttribute(value, "type") != "CurrencyBucket" || total is null || name is null || type is null || bx.GetAttribute(name, "value") != definition.Name || bx.GetAttribute(type, "value") != definition.Type ||
                !int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out int points) || points < 0) return false;
            result.Add(definition.Key, points);
        }
        totals = result; reason = ""; return true;
    }
    static bool SetTotal(Bxml bx, string key, int total) => TotalNode(bx, key) is { } node && bx.SetAttribute(node, "value", total.ToString(CultureInfo.InvariantCulture));
    static Bxml.Node? TotalNode(Bxml bx, string key) => Field(bx, Map(bx, key), "Total", "int");
    static Bxml.Node? Map(Bxml bx, string key)
    {
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" && n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key) == 1).ToArray();
        if (maps.Length != 1) return null; var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    static Bxml.Node? Field(Bxml bx, Bxml.Node? parent, string id, string type)
    {
        if (parent is null) return null; var nodes = parent.Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
        return nodes.Length == 1 && bx.GetAttribute(nodes[0], "type") == type && nodes[0].Children.Count == 0 &&
            nodes[0].Attributes.Count(a => bx.Strings[a.KeyIndex] == "value") == 1 ? nodes[0] : null;
    }
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
    static void Put32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at, 4), value);
}
#endif
