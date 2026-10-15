#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

public static partial class CollectionJournalState
{
    public readonly record struct RewardUnlockChange(int Added, int LinkedRewards, Change Journal, bool Changed, string Requirements);

    public static string RewardRequirementDescription(JournalRewardKind kind, ulong id)
        => JournalRewardUnlockCatalog.ByItem.TryGetValue((kind, id), out var reward)
            ? $"Journal: {reward.CategoryName} · {reward.Required:n0} points" : "No Collection Journal requirement";

    public static bool TryPreviewUnlockRewards(ProfileContainer? container, JournalRewardKind kind, IEnumerable<ulong> ids,
        out RewardUnlockChange change, out string reason)
        => TryRewardPlan(container, kind, ids, out _, out change, out reason);

    public static bool TryUnlockRewards(ProfileContainer? container, JournalRewardKind kind, IEnumerable<ulong> ids,
        out RewardUnlockChange change, out string reason)
    {
        if (!TryRewardPlan(container, kind, ids, out var plan, out change, out reason)) return false;
        CommitPlan(container!, plan!);
        return true;
    }

    internal static bool RewardThresholdsMet(ProfileContainer? container, JournalRewardKind kind, IEnumerable<ulong> ids, out string reason)
    {
        reason = "";
        var links = ids.Select(id => JournalRewardUnlockCatalog.ByItem.GetValueOrDefault((kind, id))).Where(r => r != null).ToArray();
        if (links.Length == 0) return true;
        if (!TryState(container, out var state, out reason) || !TryStateTotals(container!, state!, out var totals, out reason)) return false;
        foreach (var link in links)
            if (!totals!.TryGetValue(CategoryKey(Categories[link!.Category]), out int value) || value < link.Required)
            { reason = $"{link.CategoryName} needs {link.Required:n0} Journal points. Nothing was changed."; return false; }
        return true;
    }

    static bool TryRewardPlan(ProfileContainer? container, JournalRewardKind kind, IEnumerable<ulong>? ids, out Plan? plan,
        out RewardUnlockChange change, out string reason)
    {
        plan = null; change = default; reason = "The selected reward inventory is not safely recognized; nothing was changed.";
        if (container?.Career == null || kind is not (JournalRewardKind.Horn or JournalRewardKind.Phrase)) return false;
        var selected = ids?.ToHashSet();
        if (selected == null || selected.Count == 0 || selected.Any(id => kind == JournalRewardKind.Horn
                ? !CarHornUnlockState.AllHorns.Any(h => h.Id == id) : !ForzaLinkPhraseUnlockState.AllRewardPhrases.Any(p => p.Id == id)))
        { reason = "Select recognized horns or reward phrases; nothing was changed."; return false; }
        var links = selected.Select(id => JournalRewardUnlockCatalog.ByItem.GetValueOrDefault((kind, id))).Where(r => r != null).ToArray();
        var goals = links.GroupBy(r => r!.Category).ToDictionary(g => g.Key, g => g.Max(r => r!.Required));
        Plan? journal = null;
        if (goals.Count > 0)
        {
            if (!TryState(container, out var state, out reason) || !TryStateTotals(container, state!, out var totals, out reason)) return false;
            var selector = new RewardRequirementSelector(state!, totals!, goals);
            if (!selector.TrySelect(out var challenges, out reason)) return false;
            if (challenges.Length > 0 && !TryPlan(container, challenges, out journal, out reason)) return false;
        }
        // The Journal's own private stage includes the exact validated native PBs.
        // Never rerun random PB generation or replace the caller's container.
        var staged = journal?.Staged ?? ProfileContainer.Parse(container.Serialize());
        if (!RewardThresholdsMet(staged, kind, selected, out reason)) return false;
        int added;
        bool unlocked = kind == JournalRewardKind.Horn
            ? CarHornUnlockState.TryUnlockSelected(staged, selected, out added)
            : ForzaLinkPhraseUnlockState.TryUnlockJournalLinkedSelection(staged, selected, out added);
        if (!unlocked) { reason = "The selected reward inventory is not safely recognized; nothing was changed."; return false; }
        string inventory = kind == JournalRewardKind.Horn ? "CarHornSaveState" : "QuickChatSaveState";
        var records = journal?.Records.ToList() ?? new();
        var old = container.Career.Records.Where(r => r.Name == inventory).ToArray();
        var next = staged.Career!.Records.Where(r => r.Name == inventory).ToArray();
        if (old.Length != 1 || next.Length != 1 || old[0].Serializer != next[0].Serializer) return false;
        if (!old[0].Payload.AsSpan().SequenceEqual(next[0].Payload)) records.Add((old[0], next[0].Payload));
        byte[] bytes = staged.Serialize();
        if (!ProfileContainer.Parse(bytes).Serialize().AsSpan().SequenceEqual(bytes)) return false;
        var result = journal?.Result ?? default;
        change = new(added, links.Length, result, added > 0 || result.Changed,
            string.Join(", ", goals.OrderBy(g => FriendlyCategory(Categories[g.Key])).Select(g => $"{FriendlyCategory(Categories[g.Key])}: {g.Value:n0} points")));
        plan = new(records, journal?.Attributes ?? new(), result, journal?.Properties, staged);
        reason = ""; return true;
    }

    // Choose supported underlying challenges, not the automatic bonus rows.
    // Completion-event counters are simulated using the same baseline/delta
    // semantics as TryPlan, then checked against the actual staged currencies.
    sealed class RewardRequirementSelector
    {
        readonly State state;
        readonly Dictionary<string, int> totals;
        readonly Dictionary<ulong, int> goals;
        readonly Dictionary<ulong, Entry> entries;
        readonly HashSet<ulong> baseline;
        HashSet<ulong> done;
        HashSet<ulong> selected = new();
        readonly Dictionary<ulong, uint> baselineCounts;
        int budget;

        internal RewardRequirementSelector(State state, Dictionary<string, int> totals, Dictionary<ulong, int> goals)
        {
            this.state = state; this.totals = totals; this.goals = goals;
            entries = state.Entries.ToDictionary(e => e.Definition.Id);
            baseline = state.Entries.Where(e => Completed(state.Payload, e)).Select(e => e.Definition.Id).ToHashSet();
            done = new(baseline);
            baselineCounts = Counts();
            budget = entries.Count * 20;
        }

        Dictionary<ulong, uint> Counts() => done.GroupBy(id => entries[id].Definition.Category).ToDictionary(g => g.Key, g => (uint)g.Count());
        uint Delta(CollectionJournalCatalog.Objective objective)
        {
            var counts = Counts();
            return checked((uint)(objective.GlobalDependency ? done.Count - baseline.Count
                : objective.Dependencies.Sum(id => (long)counts.GetValueOrDefault(id) - baselineCounts.GetValueOrDefault(id))));
        }
        long Value(Entry entry, int index) => U32(state.Record.Payload, entry.Objectives[index].ValueOffset) + (long)Delta(entry.Definition.Objectives[index]);
        void Closure()
        {
            bool added;
            do
            {
                added = false;
                // Freeze this pass's deltas just as the actual Journal writer does.
                var ready = entries.Values.Where(e => e.Supported && !done.Contains(e.Definition.Id) && Automatic(e.Definition)
                    && e.Objectives.All(o => o.Tag is 1 or 2 or 3)
                    && e.Definition.Objectives.Select((o, i) => Value(e, i) >= o.Target).All(v => v)
                    && e.Definition.Objectives.Any(o => Delta(o) > 0)).Select(e => e.Definition.Id).ToArray();
                foreach (var id in ready) added |= done.Add(id);
            } while (added);
        }
        int PointsFor(ulong category)
        {
            int extra = done.Except(baseline).Where(id => entries[id].Definition.Category == category).Sum(id => entries[id].Definition.Points);
            if (state.PartialCatalog) return totals.GetValueOrDefault(CategoryKey(Categories[category])) + extra;
            return done.Where(id => entries[id].Definition.Category == category).Sum(id => entries[id].Definition.Points);
        }
        bool Native(ulong id) => JournalRaceCatalog.Items.Any(m => m.Challenge == id) || PrStuntProgressState.ByChallenge.ContainsKey(id)
            || JobProgressState.ByChallenge.ContainsKey(id) || ExplorationProgressCatalog.ByChallenge.ContainsKey(id);

        bool Add(ulong id)
        {
            if (!entries.TryGetValue(id, out var entry) || !entry.Supported || Automatic(entry.Definition)) return false;
            var bundle = new HashSet<ulong> { id };
            if (PrStuntProgressState.ByChallenge.TryGetValue(id, out var stunt))
                bundle.UnionWith(PrStuntProgressState.ByChallenge.Values.Where(m => m.Stunt.Entity == stunt.Stunt.Entity && m.Stars <= stunt.Stars).Select(m => m.Challenge));
            if (ExplorationProgressCatalog.ByChallenge.TryGetValue(id, out var world))
            {
                if (world.Kind == "Region") bundle.UnionWith(ExplorationProgressCatalog.ByChallenge.Values.Where(m => m.Kind == "Region").Select(m => m.Challenge));
                if (world.Kind == "Story") bundle.UnionWith(ExplorationProgressCatalog.ByChallenge.Values.Where(m => m.Kind == "Story" && m.Key == world.Key && m.Stars <= world.Stars).Select(m => m.Challenge));
            }
            var race = JournalRaceCatalog.Items.FirstOrDefault(m => m.Challenge == id);
            if (race?.Win == true) bundle.UnionWith(JournalRaceCatalog.Items.Where(m => m.Race == race.Race && m.Mode == race.Mode && !m.Win).Select(m => m.Challenge));
            if (JobProgressState.ByChallenge.TryGetValue(id, out int level)) bundle.UnionWith(JobProgressState.ByChallenge.Where(p => p.Value <= level).Select(p => p.Key));
            if (bundle.Any(key => !entries.TryGetValue(key, out var e) || !e.Supported || Automatic(e.Definition))) return false;
            foreach (var key in bundle) { selected.Add(key); done.Add(key); }
            Closure(); return true;
        }

        IEnumerable<Entry> Candidates(ulong category, int gap) => entries.Values.Where(e => e.Supported && e.Definition.Category == category
                && !Automatic(e.Definition) && !done.Contains(e.Definition.Id) && e.Definition.Points > 0)
            // Avoid discovering all regions just to reach a small photo/landmark milestone.
            .OrderBy(e => ExplorationProgressCatalog.ByChallenge.TryGetValue(e.Definition.Id, out var w) && w.Kind == "Region" ? 1 : 0)
            .ThenBy(e => e.Definition.Points <= gap ? 0 : 1)
            .ThenBy(e => e.Definition.Points <= gap ? -e.Definition.Points : e.Definition.Points).ThenBy(e => e.Definition.Id);

        bool EnsureBonus(ulong id, HashSet<ulong> visiting)
        {
            if (done.Contains(id)) return true;
            if (--budget < 0 || !visiting.Add(id) || !entries.TryGetValue(id, out var e) || !e.Supported || !Automatic(e.Definition)
                || e.Objectives.Any(o => o.Tag is not (1 or 2 or 3))) return false;
            for (int i = 0; i < e.Objectives.Length; i++)
            {
                var objective = e.Definition.Objectives[i];
                while (Value(e, i) < objective.Target)
                {
                    if (--budget < 0) return false;
                    var categories = objective.GlobalDependency ? Categories.Keys.ToHashSet() : objective.Dependencies.ToHashSet();
                    bool added = false;
                    foreach (var candidate in categories.SelectMany(c => Candidates(c, Math.Max(1, goals.GetValueOrDefault(c) - PointsFor(c))))
                        .OrderByDescending(c => goals.GetValueOrDefault(c.Definition.Category) > PointsFor(c.Definition.Category)).ThenBy(c => c.Definition.Id))
                        if (Add(candidate.Definition.Id)) { added = true; break; }
                    if (!added)
                    {
                        foreach (var bonus in BonusCandidates(categories).Where(b => !visiting.Contains(b.Definition.Id)))
                        {
                            var savedDone = new HashSet<ulong>(done); var savedSelected = new HashSet<ulong>(selected);
                            if (EnsureBonus(bonus.Definition.Id, new(visiting))) { added = true; break; }
                            done = savedDone; selected = savedSelected;
                        }
                    }
                    if (!added) return false;
                }
            }
            Closure(); visiting.Remove(id);
            return done.Contains(id);
        }
        IEnumerable<Entry> BonusCandidates(HashSet<ulong> categories) => entries.Values.Where(e => e.Supported && !done.Contains(e.Definition.Id)
                && categories.Contains(e.Definition.Category) && Automatic(e.Definition))
            .OrderBy(e => e.Definition.Objectives.Select((o, i) => Math.Max(0, o.Target - Value(e, i))).Sum()).ThenBy(e => e.Definition.Id);

        internal bool TrySelect(out ulong[] ids, out string reason)
        {
            ids = Array.Empty<ulong>(); reason = "";
            foreach (var goal in goals.OrderBy(g => g.Key))
            {
                if (!totals.ContainsKey(CategoryKey(Categories[goal.Key])))
                { reason = $"{FriendlyCategory(Categories[goal.Key])} is not initialized in this save; nothing was changed."; return false; }
                while (PointsFor(goal.Key) < goal.Value)
                {
                    bool added = false;
                    foreach (var candidate in Candidates(goal.Key, goal.Value - PointsFor(goal.Key)))
                        if (Add(candidate.Definition.Id)) { added = true; break; }
                    if (!added)
                    {
                        foreach (var bonus in BonusCandidates(new() { goal.Key }))
                        {
                            var savedDone = new HashSet<ulong>(done); var savedSelected = new HashSet<ulong>(selected);
                            if (EnsureBonus(bonus.Definition.Id, new())) { added = true; break; }
                            done = savedDone; selected = savedSelected;
                        }
                    }
                    if (!added || --budget < 0)
                    { reason = $"{FriendlyCategory(Categories[goal.Key])} cannot safely reach {goal.Value:n0} points with this save's supported challenges. Nothing was changed."; return false; }
                }
            }
            // Repair native progress for already-owned/earned selections as well.
            foreach (var id in baseline.Where(id => goals.ContainsKey(entries[id].Definition.Category) && Native(id)).ToArray())
            {
                if (ExplorationProgressCatalog.ByChallenge.TryGetValue(id, out var w) && w.Kind == "Region"
                    && ExplorationProgressCatalog.ByChallenge.Values.Where(m => m.Kind == "Region").Any(m => !done.Contains(m.Challenge))) continue;
                Add(id);
            }
            ids = selected.Order().ToArray(); return true;
        }
    }
}
#endif
