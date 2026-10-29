#if PROFILE_UNLOCK_TESTING
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Definition-backed Food Delivery promotions; no deliveries, reward claims or driver XP fabricated.</summary>
internal static class JobProgressState
{
    internal const string ThreadKey = "progression_thread_job_food_delivery";
    internal const string XpKey = "CurrencyBucket_JobXP_job_food_delivery";
    const string StatPath = "Career/Threads.Thread.job_food_delivery";
    // Installed ProgressionThreadsMap -> job_food_delivery -> nine exact
    // ProgressionThreadObjectiveData GUID links (IDs 10..18), 2026-10-15.
    // Matches natural level-1 / level-8 / level-10 snapshots. Level 10 is final,
    // non-repeatable. Preserve above-threshold Job XP, pending XP and all claims.
    internal static IReadOnlyList<int> Thresholds { get; } = Array.AsReadOnly(new[] { 0, 6750, 27650, 70800, 137200, 252135, 434480, 686930, 1075885, 1606400 });
    internal static IReadOnlyDictionary<ulong, int> ByChallenge { get; } = CollectionJournalCatalog.Data.Challenges
        .Where(c => c.Objectives.Length == 1 && c.Objectives[0].Kind == "SimpleStatObjective" &&
            c.Objectives[0].Target is >= 2 and <= 10 && c.Objectives[0].Stats.SequenceEqual(new[] { StatPath }))
        .ToDictionary(c => c.Id, c => (int)c.Objectives[0].Target);
    internal sealed record Edit(string Key, string Id, string Type, string Value);

    // Called on the Journal transaction clone. Return only the exact scalar
    // fields to commit later; preserve all opaque reward metadata/other threads.
    internal static bool TrySync(ProfileContainer container, IEnumerable<ulong> ids, out Edit[] edits, out int levelUpdated, out string reason)
    {
        edits = Array.Empty<Edit>(); levelUpdated = 0; reason = "";
        int requested = ids.Where(ByChallenge.ContainsKey).Select(id => ByChallenge[id]).DefaultIfEmpty(0).Max();
        if (requested == 0) return true;
        reason = "The Food Delivery XP/rank layout is not recognized; no Journal or job changes were made.";
        if (container.SaveState is not { } bx || !TryLayout(bx, out int xp, out int level)) return false;
        int xpLevel = Enumerable.Range(1, 10).Last(n => xp >= Thresholds[n - 1]);
        int desired = Math.Max(requested, Math.Max(level, xpLevel));
        var candidates = new[]
        {
            new Edit(XpKey, "Total", "int", Math.Max(xp, Thresholds[desired - 1]).ToString(CultureInfo.InvariantCulture)),
            new Edit(ThreadKey, "Level", "int", desired.ToString(CultureInfo.InvariantCulture)),
            new Edit(ThreadKey, "LastAchievedObjectiveID", "u32", (desired + 8).ToString(CultureInfo.InvariantCulture)),
            new Edit(ThreadKey, "CurrencyAtLastObjective", "int", Thresholds[desired - 1].ToString(CultureInfo.InvariantCulture))
        };
        var changes = candidates.Where(e => bx.GetAttribute(Field(bx, e.Key, e.Id, e.Type)!, "value") != e.Value).ToArray();
        foreach (var edit in changes) bx.SetAttribute(Field(bx, edit.Key, edit.Id, edit.Type)!, "value", edit.Value);
        if (!TryLayout(bx, out int nextXp, out int nextLevel) || nextXp < Thresholds[desired - 1] || nextLevel != desired) return false;
        edits = changes; levelUpdated = changes.Length == 0 ? 0 : desired;
        reason = ""; return true;
    }

    static bool TryLayout(Bxml bx, out int xp, out int level)
    {
        xp = level = 0;
        var bucket = Map(bx, XpKey, "CurrencyBucket"); var thread = Map(bx, ThreadKey, "ProgressionThreadSaveState");
        if (bucket is null || thread is null || bucket.Children.Count != 3 || thread.Children.Count != 5 ||
            Text(bx, XpKey, "Name", "string") != "job_food_delivery" || Text(bx, XpKey, "Type", "CurrencyTypes") != "JobXP" ||
            Text(bx, ThreadKey, "ForceUnlocked", "bool") is not ("true" or "false") ||
            thread.Children.Count(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == "ClaimedRewardObjectiveIds") != 1 ||
            !int.TryParse(Text(bx, XpKey, "Total", "int"), NumberStyles.None, CultureInfo.InvariantCulture, out xp) || xp < 0 ||
            !int.TryParse(Text(bx, ThreadKey, "Level", "int"), NumberStyles.None, CultureInfo.InvariantCulture, out level) || level is < 1 or > 10 ||
            !uint.TryParse(Text(bx, ThreadKey, "LastAchievedObjectiveID", "u32"), NumberStyles.None, CultureInfo.InvariantCulture, out uint last) ||
            !int.TryParse(Text(bx, ThreadKey, "CurrencyAtLastObjective", "int"), NumberStyles.None, CultureInfo.InvariantCulture, out int at)) return false;
        return last == (level == 1 ? uint.MaxValue : (uint)(level + 8)) && at == Thresholds[level - 1];
    }

    static string? Text(Bxml bx, string key, string id, string type) => Field(bx, key, id, type) is { } n ? bx.GetAttribute(n, "value") : null;
    internal static Bxml.Node? Field(Bxml bx, string key, string id, string type)
    {
        var parent = Map(bx, key, key == XpKey ? "CurrencyBucket" : "ProgressionThreadSaveState");
        var fields = parent?.Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id && bx.GetAttribute(n, "type") == type).ToArray();
        return fields is { Length: 1 } && fields[0].Attributes.Count == 3 && fields[0].Children.Count == 0 ? fields[0] : null;
    }
    static Bxml.Node? Map(Bxml bx, string key, string type)
    {
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key) == 1).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count != 2) return null;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value" && bx.GetAttribute(n, "type") == type).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
#endif
