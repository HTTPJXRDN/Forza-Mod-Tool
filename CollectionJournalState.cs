#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Local FH6 Collection Journal completion prototype; no save/game file writes.</summary>
public static partial class CollectionJournalState
{
    const string RecordName = "CollectionCampaignSaveState";
    const string Serializer = "BaseChallengeSaveState";
    static readonly Dictionary<ulong, CollectionJournalCatalog.Challenge> Definitions = CollectionJournalCatalog.Data.Challenges.ToDictionary(c => c.Id);
    static Dictionary<ulong, CollectionJournalCatalog.Challenge> ActiveDefinitions => ProfileGameData.Current?.Challenges ?? Definitions;
    static readonly Dictionary<ulong, CollectionJournalCatalog.Category> Categories = CollectionJournalCatalog.Data.Categories.ToDictionary(c => c.Id);
    static Dictionary<ulong, CollectionJournalCatalog.Category> ActiveCategories => ProfileGameData.Current?.Categories ?? Categories;
    static readonly HashSet<string> AllowedRecords = new[] { RecordName, "Stats", "PlayerWristbandSaveState", "CampaignUnlockSaveState", "CareerState" }.Concat(ExplorationProgressState.Records).ToHashSet(StringComparer.Ordinal);
    public sealed record Row(ulong Id, string Campaign, string Category, string Name, string Objective, string Subtitle, int Points, bool Completed, bool Automatic,
        bool StateKnown = true, bool Supported = true, string Compatibility = "Supported")
    {
        public string CompletionText => StateKnown ? Completed ? "Yes" : "No" : "Unknown";
        public string PointsDisplay => Supported ? Points.ToString(CultureInfo.InvariantCulture) : "—";
    }
    public sealed record View(Row[] Rows, int FestivalPoints, int DiscoveryPoints, int Completed, bool TotalsAgree,
        bool PartialCatalog = false, int SavedEntries = 0, string CompatibilityNote = "");
    public readonly record struct Change(int SelectedCompleted, int AutomaticCompleted, int PreviousFestivalPoints, int FestivalPoints,
        int PreviousDiscoveryPoints, int DiscoveryPoints, byte PreviousBand, byte Band, bool Changed, int RacesUpdated = 0, int PrStuntsUpdated = 0, int JobLevel = 0,
        int BoardsUpdated = 0, int MascotsUpdated = 0, int LandmarksUpdated = 0, bool RegionsUpdated = false, int StoriesUpdated = 0, int PromoCarsUpdated = 0);
    sealed record Objective(ulong Id, int FlagOffset, int ValueOffset, byte Tag);
    sealed record Entry(CollectionJournalCatalog.Challenge Definition, Objective[] Objectives, int TailOffset, bool Supported = true, uint SavedVersion = 0);
    sealed record State(BinaryCareer.Record Record, byte[] Payload, Entry[] Entries, byte[] Metadata, bool PartialCatalog = false);
    sealed record AttributeEdit(Bxml.Node Node, string Value);
    sealed record PropertyEdit(PropertyTree.PropertyNode Node, byte[] Bytes);
    sealed record Plan(List<(BinaryCareer.Record Record, byte[] Bytes)> Records, List<AttributeEdit> Attributes, Change Result, List<PropertyEdit>? Properties = null, ProfileContainer? Staged = null, byte[]? Database = null);

    public static bool TryRead(ProfileContainer? container, out View? view, out string reason)
    {
        using var catalogScope = ProfileGameData.Use(container);
        view = null;
        if (!TryState(container, out var state, out reason) || !TryStateTotals(container!, state!, out var totals, out reason)) return false;
        var points = Points(state!);
        var rows = state!.Entries.Where(e => !CollectionJournalCarPolicy.Excluded(e.Definition)).Select(e =>
        {
            var category = ActiveCategories.GetValueOrDefault(e.Definition.Category);
            if (category is null) return new Row(e.Definition.Id, "Unknown", "Unmapped content", e.Definition.Name, e.Definition.Description, "", 0, Completed(state.Payload, e), false, Supported: false, Compatibility: "Read-only: no matching game definition");
            return new Row(e.Definition.Id, category.Festival ? "Horizon Festival" : "Discover Japan", FriendlyCategory(category),
                e.Definition.Name, e.Definition.Description, e.Definition.Subtitle, e.Definition.Points, Completed(state.Payload, e), Automatic(e.Definition),
                Supported: e.Supported, Compatibility: e.Supported ? "Supported" : "Read-only: definition or native progression mapping differs");
        }).Concat(MissingRows(state.Entries.Select(e => e.Definition.Id).ToHashSet(), "Not present in this save")).ToArray();
        string note = state.PartialCatalog ? $"{state.Entries.Length:n0} saved entries · {ActiveDefinitions.Keys.Except(state.Entries.Select(e => e.Definition.Id)).Count():n0} not present · {state.Entries.Count(e => !e.Supported):n0} changed definitions preserved. Edits add verified points only." : "";
        view = new(rows, totals![FestivalKey], totals[DiscoveryKey], rows.Count(r => r.StateKnown && r.Completed),
            ActiveCategories.Values.Where(c => totals.ContainsKey(CategoryKey(c))).All(c => totals[CategoryKey(c)] == points.GetValueOrDefault(c.Id)) &&
            totals[FestivalKey] == Festival(points) && totals[DiscoveryKey] == Discovery(points), state.PartialCatalog, state.Entries.Length, note);
        return true;
    }

    public static bool TryPreview(ProfileContainer? container, IEnumerable<ulong> ids, out Change change, out string reason)
    {
        using var catalogScope = ProfileGameData.Use(container);
        change = default;
        if (!TryPlan(container, ids, out var plan, out reason)) return false;
        change = plan!.Result;
        return true;
    }

    public static bool TryComplete(ProfileContainer? container, IEnumerable<ulong> ids, out Change change, out string reason, Action<byte[]>? commitDatabase = null)
    {
        using var catalogScope = ProfileGameData.Use(container);
        change = default;
        if (!TryPlan(container, ids, out var plan, out reason)) return false;
        // Every record and attribute has already been validated on a private
        // parsed clone. Commit only the scoped differences, keeping original UI
        // node references and all unrelated pending edits intact.
        CommitPlan(container!, plan!, commitDatabase);
        change = plan!.Result;
        return true;
    }

    static void CommitPlan(ProfileContainer container, Plan plan, Action<byte[]>? commitDatabase = null)
    {
        using var catalogScope = ProfileGameData.Use(container);
        // The UI owns an open working SQLite connection. Commit its validated
        // database first; a failure must leave Journal/progression edits untouched.
        if (plan.Database != null) { commitDatabase?.Invoke(plan.Database); container.Database = plan.Database; }
        foreach (var edit in plan.Attributes) container.SaveState!.SetAttribute(edit.Node, "value", edit.Value);
        foreach (var edit in plan.Records) container.Career!.ReplacePayload(edit.Record, edit.Bytes);
        foreach (var edit in plan.Properties ?? new()) { edit.Node.RawValue = (byte[])edit.Bytes.Clone(); edit.Node.Edited = true; }
    }

    static bool TryPlan(ProfileContainer? container, IEnumerable<ulong> ids, out Plan? plan, out string reason)
    {
        using var catalogScope = ProfileGameData.Use(container);
        plan = null;
        reason = "Journal layout or progression fields are not safely recognized; nothing was changed.";
        if (!TryState(container, out var originalState, out reason) || !TryStateTotals(container!, originalState!, out var oldTotals, out reason)) return false;
        var selected = ids?.ToHashSet();
        if (selected is null || selected.Count == 0 || selected.Count > ActiveDefinitions.Count || selected.Any(id => !ActiveDefinitions.ContainsKey(id)))
        { reason = "Select at least one mapped journal challenge."; return false; }
        if (selected.Any(id => CollectionJournalCarPolicy.Excluded(ActiveDefinitions[id])))
        { reason = "DLC cars are excluded from Car Collection Journal completion. Garage additions remain available; nothing was changed."; return false; }
        var staged = ProfileContainer.Parse(container!.Serialize()); ProfileGameData.Copy(container, staged);
        if (!TryState(staged, out var state, out reason)) return false;
        if (!WristbandProgressionTestState.TryReadCurrentBand(staged, out byte oldBand))
        { reason = "The current wristband statistic is not safely recognized."; return false; }
        var entries = state!.Entries.ToDictionary(e => e.Definition.Id);
        if (selected.Any(id => !entries.TryGetValue(id, out var entry) || !entry.Supported))
        { reason = "A selected challenge is missing from this save or uses a different definition. It is read-only; nothing was changed."; return false; }
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
        if (!RaceProgressState.TrySync(staged, selected, out int racesUpdated, out reason)) return false;
        var selectedStunts = selected.Where(PrStuntProgressState.ByChallenge.ContainsKey)
            .Select(id => PrStuntProgressState.ByChallenge[id].Stunt.Entity).ToHashSet(StringComparer.Ordinal);
        // Repair to the highest medal already earned in this working Journal,
        // even when the user selects only a lower-star row for the same stunt.
        var stuntChallenges = state.Entries.Where(e => e.Supported && Completed(state.Payload, e) &&
            PrStuntProgressState.ByChallenge.TryGetValue(e.Definition.Id, out var mapping) && selectedStunts.Contains(mapping.Stunt.Entity))
            .Select(e => e.Definition.Id);
        if (!PrStuntProgressState.TrySync(staged, stuntChallenges, out string[] stuntKeys, out reason)) return false;
        var stuntAttributes = new List<AttributeEdit>();
        foreach (string key in stuntKeys)
        {
            if (!PrStuntProgressState.TryFields(container.SaveState!, key, out var oldComplete, out var oldBest) ||
                !PrStuntProgressState.TryFields(staged.SaveState!, key, out var nextComplete, out var nextBest))
            { reason = "PR-stunt validation failed; nothing was changed."; return false; }
            foreach (var pair in new[] { (oldComplete!, nextComplete!), (oldBest!, nextBest!) })
            {
                string value = staged.SaveState!.GetAttribute(pair.Item2, "value")!;
                if (container.SaveState!.GetAttribute(pair.Item1, "value") != value) stuntAttributes.Add(new(pair.Item1, value));
            }
        }
        var jobChallenges = selected.Any(JobProgressState.ByChallenge.ContainsKey)
            ? state.Entries.Where(e => e.Supported && Completed(state.Payload, e) && JobProgressState.ByChallenge.ContainsKey(e.Definition.Id)).Select(e => e.Definition.Id)
            : Enumerable.Empty<ulong>();
        if (!JobProgressState.TrySync(staged, jobChallenges, out var jobEdits, out int jobLevel, out reason)) return false;
        foreach (var edit in jobEdits)
        {
            var old = JobProgressState.Field(container.SaveState!, edit.Key, edit.Id, edit.Type);
            if (old is null) { reason = "Food Delivery rank validation failed; nothing was changed."; return false; }
            stuntAttributes.Add(new(old, edit.Value));
        }
        var completedIds = state.Entries.Where(e => e.Supported && Completed(state.Payload, e)).Select(e => e.Definition.Id).ToHashSet();
        if (!ExplorationProgressState.TrySync(staged, selected, completedIds, out var world, out reason)) return false;
        // Selecting any Promo row also repairs older completed Promo rows that
        // predate native photo synchronization. It does not create image files.
        var promoChallenges = selected.Any(id => entries[id].Definition.Category == PromoPhotoProgressState.CategoryId)
            ? state.Entries.Where(e => e.Supported && e.Definition.Category == PromoPhotoProgressState.CategoryId && Completed(state.Payload, e)).Select(e => e.Definition)
            : Enumerable.Empty<CollectionJournalCatalog.Challenge>();
        if (!PromoPhotoProgressState.TrySync(staged, promoChallenges, out var promo, out reason)) return false;
        foreach (var edit in world.Attributes)
        {
            var old = ExplorationProgressState.Field(container.SaveState!, edit.Key, "DiscountBoardSaveState", edit.Id, edit.Type);
            if (old is null) { reason = "Board save-state validation failed; nothing was changed."; return false; }
            stuntAttributes.Add(new(old, edit.Value));
        }
        var propertyEdits = new List<PropertyEdit>();
        var allowedProperties = world.Properties.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var originals = container.Properties?.Walk().ToDictionary(n => n.Path, n => n.Node);
        var nextProperties = staged.Properties?.Walk().ToDictionary(n => n.Path, n => n.Node);
        if (originals is null || nextProperties is null)
        {
            if (allowedProperties.Count > 0 || !container.Sections[0].Payload.AsSpan().SequenceEqual(staged.Sections[0].Payload)) return false;
            originals = new(); nextProperties = new();
        }
        if (!originals.Keys.ToHashSet().SetEquals(nextProperties.Keys)) return false;
        foreach (var pair in originals)
        {
            var next = nextProperties[pair.Key];
            if (pair.Value.TypeId != next.TypeId || pair.Value.CaseSensitive != next.CaseSensitive || pair.Value.HashBits != next.HashBits) return false;
            if (pair.Value.RawValue.AsSpan().SequenceEqual(next.RawValue)) continue;
            if (!allowedProperties.Contains(pair.Key) || next.TypeId is not (3 or 17) || next.RawValue.Length != 4) return false;
            propertyEdits.Add(new(pair.Value, (byte[])next.RawValue.Clone()));
        }
        if (explicitCount == 0)
        {
            var raceEdits = new List<(BinaryCareer.Record, byte[])>();
            if (racesUpdated > 0 || stuntKeys.Length > 0 || jobLevel > 0 || world.Changed || promo.Changed)
            {
                byte[] testBytes = staged.Serialize();
                if (!ProfileContainer.Parse(testBytes).Serialize().AsSpan().SequenceEqual(testBytes)) return false;
                if (!staged.Database.AsSpan().SequenceEqual(promo.Database ?? container.Database) || staged.Career!.Xuid != container.Career!.Xuid) return false;
                foreach (var next in staged.Career!.Records)
                {
                    var current = container.Career!.Records.Single(r => r.Name == next.Name);
                    if (current.Payload.AsSpan().SequenceEqual(next.Payload)) continue;
                    if (!AllowedRecords.Contains(next.Name)) return false;
                    raceEdits.Add((current, next.Payload));
                }
            }
            plan = new(raceEdits, stuntAttributes, new(0, 0, oldTotals![FestivalKey], oldTotals[FestivalKey], oldTotals[DiscoveryKey], oldTotals[DiscoveryKey], oldBand, oldBand,
                racesUpdated > 0 || stuntKeys.Length > 0 || jobLevel > 0 || world.Changed || promo.Changed, racesUpdated, stuntKeys.Length, jobLevel,
                world.Boards, world.Mascots, world.Landmarks, world.Regions, world.Stories, promo.Cars.Length), propertyEdits, staged, promo.Database);
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
            var deltas = ActiveCategories.Keys.ToDictionary(id => id, id => counts.GetValueOrDefault(id) - baselineCounts.GetValueOrDefault(id));
            uint totalDelta = checked((uint)deltas.Values.Sum(v => (long)v));
            foreach (var entry in state.Entries.Where(e => e.Supported && Automatic(e.Definition) && !Completed(state.Payload, e)))
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
        var newTotals = new Dictionary<string, int>(oldTotals!);
        if (state.PartialCatalog)
        {
            // Older/lazily initialized saves may omit whole categories and keep
            // older challenge revisions. Never reset their historical points to
            // today's catalog. Add ONLY verified new completion rewards instead.
            var baselinePoints = Points(originalState!); int festivalDelta = 0, discoveryDelta = 0;
            foreach (var category in ActiveCategories.Values)
            {
                int delta = points.GetValueOrDefault(category.Id) - baselinePoints.GetValueOrDefault(category.Id);
                if (delta == 0) continue;
                string key = CategoryKey(category);
                if (delta < 0 || !newTotals.TryGetValue(key, out int old) || old > int.MaxValue - delta)
                { reason = "Category points are missing or would overflow; nothing was changed."; return false; }
                newTotals[key] = old + delta;
                if (category.Festival) festivalDelta += delta; else discoveryDelta += delta;
            }
            if (oldTotals![FestivalKey] > int.MaxValue - festivalDelta || oldTotals[DiscoveryKey] > int.MaxValue - discoveryDelta)
            { reason = "Campaign points would overflow; nothing was changed."; return false; }
            newTotals[FestivalKey] += festivalDelta; newTotals[DiscoveryKey] += discoveryDelta;
        }
        else
        {
            foreach (var category in ActiveCategories.Values) newTotals[CategoryKey(category)] = points.GetValueOrDefault(category.Id);
            newTotals[FestivalKey] = Festival(points); newTotals[DiscoveryKey] = Discovery(points);
        }
        foreach (var total in newTotals)
            if (total.Value != oldTotals![total.Key] && !SetTotal(staged.SaveState!, total.Key, total.Value)) return false;
        if (!WristbandProgressionTestState.TrySyncFromFestivalPoints(staged, out var bandChange, out reason)) return false;
        byte[] stagedBytes = staged.Serialize();
        var roundTrip = ProfileContainer.Parse(stagedBytes);
        if (!roundTrip.Serialize().AsSpan().SequenceEqual(stagedBytes) || !TryState(roundTrip, out _, out reason)) return false;
        var recordEdits = new List<(BinaryCareer.Record, byte[])>();
        foreach (var record in staged.Career.Records)
        {
            var matchingRecords = container.Career!.Records.Where(r => r.Name == record.Name).ToArray();
            if (matchingRecords.Length != 1 || matchingRecords[0].Serializer != record.Serializer) return false;
            if (matchingRecords[0].Payload.AsSpan().SequenceEqual(record.Payload)) continue;
            if (!AllowedRecords.Contains(record.Name)) return false;
            recordEdits.Add((matchingRecords[0], record.Payload));
        }
        var attributeEdits = new List<AttributeEdit>(stuntAttributes);
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
        if (!staged.Database.AsSpan().SequenceEqual(promo.Database ?? container.Database) || staged.Career.Xuid != container.Career!.Xuid) return false;
        plan = new(recordEdits, attributeEdits, new(explicitCount, automaticCount, oldTotals[FestivalKey], newTotals[FestivalKey],
            oldTotals[DiscoveryKey], newTotals[DiscoveryKey], oldBand, bandChange.SelectedBand, true, racesUpdated, stuntKeys.Length, jobLevel,
            world.Boards, world.Mascots, world.Landmarks, world.Regions, world.Stories, promo.Cars.Length), propertyEdits, staged, promo.Database);
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
        using var catalogScope = ProfileGameData.Use(container);
        state = null; reason = "Journal record or save-state map is unavailable; showing the reference list read-only.";
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray(); var placeholder = Map(bx, RecordName);
        if (records.Length != 1 || records[0].Serializer != Serializer || placeholder is null ||
            bx.GetAttribute(placeholder, "type") != RecordName || placeholder.Children.Count != 0 || placeholder.Attributes.Count != 1) return false;
        byte[] b = (byte[])records[0].Payload.Clone();
        reason = "Journal header is truncated; saved completion cannot be read.";
        if (b.Length < 12) return false;
        reason = $"Journal format {U32(b, 0)}/{U32(b, 4)} is not supported (expected 2/1).";
        if (U32(b, 0) != 2 || U32(b, 4) != 1) return false;
        uint count = U32(b, 8);
        reason = $"Save declares {count:n0} Journal entries; the record is too large.";
        if (count > 100_000 || count > (b.Length - 12) / 61) return false;
        var seen = new HashSet<ulong>(); var entries = new List<Entry>(); int at = 12;
        for (int i = 0; i < count; i++)
        {
            reason = $"Journal entry {i + 1} has an unsupported or truncated layout.";
            if (at > b.Length - 32) return false;
            ulong id = U64(b, at);
            if (id == 0 || !seen.Add(id) || U64(b, at + 8) != id) return false;
            ActiveDefinitions.TryGetValue(id, out var definition);
            ulong linked = U64(b, at + 16); uint savedVersion = U32(b, at + 24), objectiveCount = U32(b, at + 28);
            if (objectiveCount is < 1 or > 4096) return false;
            bool supported = definition != null && linked == definition.LinkedId && savedVersion == definition.Version
                && objectiveCount == definition.Objectives.Length && SupportsDefinition(definition);
            reason = $"Challenge {id:X16} has an unsupported objective layout.";
            at += 32; var objectives = new List<Objective>();
            var actualDefinitions = new List<CollectionJournalCatalog.Objective>(); var objectiveIds = new HashSet<ulong>();
            for (int j = 0; j < objectiveCount; j++)
            {
                if (at > b.Length - 11 || b[at + 8] > 1) return false;
                ulong objectiveId = U64(b, at); if (objectiveId == 0 || !objectiveIds.Add(objectiveId)) return false;
                byte tag = b[at + 9]; int size = tag switch { 1 or 2 or 3 => 4, 13 => 1, _ => -1 };
                if (size < 0 || at > b.Length - 10 - size || (tag == 13 && b[at + 10] > 1)) return false;
                var expected = definition?.Objectives.ElementAtOrDefault(j);
                supported &= expected != null && objectiveId == expected.Id
                    && (expected.Kind is "CarCollectionObjective" or "TakePhotoObjective") == (tag == 13);
                actualDefinitions.Add(new(objectiveId, 0, "Unknown", Array.Empty<ulong>(), false, Array.Empty<string>()));
                objectives.Add(new(objectiveId, at + 8, at + 10, tag)); at += 10 + size;
            }
            if (at > b.Length - 29 || b[at] != 1 || U64(b, at + 9) > 1_000_000) return false;
            ulong active = U64(b, at + 1);
            if (active != 0 && !objectives.Any(o => o.Id == active)) return false;
            definition ??= new(id, linked, savedVersion, 0, $"Unmapped challenge {id:X16}",
                "No matching installed-game definition. Saved progress is preserved read-only.", "", 0, actualDefinitions.ToArray());
            entries.Add(new(definition, objectives.ToArray(), at, supported, savedVersion)); at += 29;
        }
        // Carry opaque trailing thumbnail/reward metadata unchanged, but bound
        // it and require an existing thumbnail-map count. Never invent missing metadata.
        reason = "Journal trailing metadata is not recognized; saved progress is read-only.";
        if (at > b.Length - 4 || b.Length - at > 2_000_000 || U32(b, at) > 100_000) return false;
        state = new(records[0], b, entries.ToArray(), b[at..], !seen.SetEquals(ActiveDefinitions.Keys) || entries.Any(e => !e.Supported)); reason = ""; return true;
    }

    // Existing native sync maps are valid only for the definition they were
    // verified against. New boolean and category-dependency objectives use the
    // known serializer; new race/PB/world mechanics remain visible read-only.
    static bool SupportsDefinition(CollectionJournalCatalog.Challenge c)
    {
        if (c.Category == PromoPhotoProgressState.CategoryId && c.PromoCarOrdinal <= 0) return false;
        if (c.Category == GarageCollectionJournalState.CategoryId && c.Objectives.Any(o => o.Kind == "CarCollectionObjective") && c.CollectionCarOrdinal <= 0) return false;
        if (ProfileGameData.Current?.Journal == null) return true;
        if (!Definitions.TryGetValue(c.Id, out var known))
            return c.Objectives.All(o => o.Kind is "CarCollectionObjective" or "TakePhotoObjective")
                || Automatic(c) && c.Objectives.All(o => o.Dependencies.All(ActiveCategories.ContainsKey));
        return c.LinkedId == known.LinkedId && c.Version == known.Version && c.Category == known.Category
            && c.Points == known.Points && c.PromoCarOrdinal == known.PromoCarOrdinal && c.CollectionCarOrdinal == known.CollectionCarOrdinal && c.Objectives.Length == known.Objectives.Length
            && c.Objectives.Zip(known.Objectives).All(p => p.First.Id == p.Second.Id && p.First.Kind == p.Second.Kind
                && p.First.Target == p.Second.Target && p.First.GlobalDependency == p.Second.GlobalDependency
                && p.First.Dependencies.SequenceEqual(p.Second.Dependencies) && p.First.Stats.SequenceEqual(p.Second.Stats));
    }

    static bool Completed(byte[] b, Entry entry) => U64(b, entry.TailOffset + 9) > 0;
    static bool Automatic(CollectionJournalCatalog.Challenge c) => c.Objectives.All(o => o.Dependencies.Length > 0 || o.GlobalDependency);
    static Dictionary<ulong, int> Points(State state) => state.Entries.Where(e => Completed(state.Payload, e)).GroupBy(e => e.Definition.Category)
        .ToDictionary(g => g.Key, g => g.Sum(e => e.Definition.Points));
    static int Festival(Dictionary<ulong, int> points) => ActiveCategories.Values.Where(c => c.Festival).Sum(c => points.GetValueOrDefault(c.Id));
    static int Discovery(Dictionary<ulong, int> points) => ActiveCategories.Values.Where(c => !c.Festival).Sum(c => points.GetValueOrDefault(c.Id));
    static string FriendlyCategory(CollectionJournalCatalog.Category c) => c.Name.StartsWith("CollectionCampaignCategory.", StringComparison.Ordinal) ? "Drift Attack" : c.Name;
    const string FestivalKey = "CurrencyBucket_FestivalCollectionCampaignProgress_cc_festival";
    const string DiscoveryKey = "CurrencyBucket_DiscoveryCollectionCampaignProgress_cc_discovery";
    static string CategoryKey(CollectionJournalCatalog.Category c) => "CurrencyBucket_CollectionCategoryProgress_" + c.Bucket;
    static bool TryTotals(ProfileContainer container, out Dictionary<string, int>? totals, out string reason)
        => TryCategoryTotals(container, ActiveCategories.Values, out totals, out reason);
    static bool TryStateTotals(ProfileContainer container, State state, out Dictionary<string, int>? totals, out string reason)
        => state.PartialCatalog
            ? TryCategoryTotals(container, ActiveCategories.Values.Where(c => state.Entries.Any(e => e.Definition.Category == c.Id)), out totals, out reason)
            : TryTotals(container, out totals, out reason);
    static bool TryCategoryTotals(ProfileContainer container, IEnumerable<CollectionJournalCatalog.Category> categories, out Dictionary<string, int>? totals, out string reason)
    {
        using var catalogScope = ProfileGameData.Use(container);
        totals = null; reason = "Journal category/campaign currencies are not safely recognized.";
        if (container.SaveState is not { } bx) return false;
        var result = new Dictionary<string, int>();
        foreach (var definition in categories.Select(c => (Key: CategoryKey(c), Name: c.Bucket, Type: "CollectionCategoryProgress"))
            .Append((Key: FestivalKey, Name: "cc_festival", Type: "FestivalCollectionCampaignProgress"))
            .Append((Key: DiscoveryKey, Name: "cc_discovery", Type: "DiscoveryCollectionCampaignProgress")))
        {
            var value = Map(bx, definition.Key); var total = TotalNode(bx, definition.Key);
            var name = Field(bx, value, "Name", "string"); var type = Field(bx, value, "Type", "CurrencyTypes");
            if (value is null || bx.GetAttribute(value, "type") != "CurrencyBucket" || total is null || name is null || type is null || bx.GetAttribute(name, "value") != definition.Name || bx.GetAttribute(type, "value") != definition.Type ||
                !int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out int points) || points < 0)
            { reason = $"Journal currency '{definition.Name}' is missing or not recognized; editing is disabled."; return false; }
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
