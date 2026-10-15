#if PROFILE_UNLOCK_TESTING
using System.Globalization;

namespace FH6LocalCryptoTool;

public static partial class CollectionJournalState
{
    public sealed record Display(Row[] Rows, int? FestivalPoints, int? DiscoveryPoints, int Completed,
        int? SavedEntries, bool CanEdit, bool PartialCatalog, bool TotalsAgree, string Warning, string Diagnostics);

    static IEnumerable<Row> MissingRows(HashSet<ulong> present, string status) => Definitions.Values.Where(c => !present.Contains(c.Id)).Select(c =>
    {
        var category = Categories[c.Category];
        return new Row(c.Id, category.Festival ? "Horizon Festival" : "Discover Japan", FriendlyCategory(category), c.Name,
            c.Description, c.Subtitle, c.Points, false, Automatic(c), StateKnown: false, Supported: false, Compatibility: status);
    });

    /// <summary>Always supplies the reference list. Unsupported saves never acquire invented completion states.</summary>
    public static Display ReadForDisplay(ProfileContainer? container)
    {
        if (TryRead(container, out var view, out string reason))
        {
            var v = view!;
            bool progressionReady = WristbandProgressionTestState.CanSetBand(container, out string progressionReason);
            string warning = v.CompatibilityNote;
            if (!progressionReady) warning += (warning.Length > 0 ? " " : "") + "Journal progression is read-only: " + progressionReason;
            string diagnostics = $"Saved entries: {v.SavedEntries}; bundled definitions: {Definitions.Count}.\n"
                + string.Join("\n", v.Rows.Where(r => !r.Supported).Select(r => $"{r.Id:X16} · {r.Name}: {r.Compatibility}"));
            if (!progressionReady) diagnostics += "\nProgression guard: " + progressionReason;
            return new(v.Rows, v.FestivalPoints, v.DiscoveryPoints, v.Completed, v.SavedEntries,
                progressionReady && v.Rows.Any(r => r.Supported), v.PartialCatalog || !progressionReady, v.TotalsAgree, warning, diagnostics);
        }
        int? count = null;
        var records = container?.Career?.Records.Where(r => r.Name == RecordName).ToArray();
        string header = "Journal record unavailable or duplicated.";
        if (records is { Length: 1 })
        {
            var b = records[0].Payload;
            if (b.Length >= 12)
            {
                uint saved = U32(b, 8); if (saved <= 100_000) count = (int)saved;
                header = $"Journal format: {U32(b, 0)}/{U32(b, 4)}; declared entries: {saved}; catalog: {Definitions.Count}.";
            }
            else header = "Journal header is truncated.";
        }
        return new(MissingRows(new(), "Unknown — saved progress not read").ToArray(),
            ReadCampaignTotal(container, FestivalKey, "cc_festival", "FestivalCollectionCampaignProgress"),
            ReadCampaignTotal(container, DiscoveryKey, "cc_discovery", "DiscoveryCollectionCampaignProgress"),
            0, count, false, true, false, "Read-only reference list. Saved completion is unknown. " + reason,
            header + "\n" + reason + "\nNo save bytes changed. No unsupported entries or currencies are created.");
    }

    static int? ReadCampaignTotal(ProfileContainer? container, string key, string name, string type)
    {
        if (container?.SaveState is not { } bx) return null;
        var value = Map(bx, key); var total = TotalNode(bx, key);
        if (value is null || total is null || bx.GetAttribute(value, "type") != "CurrencyBucket" ||
            Field(bx, value, "Name", "string") is not { } n || Field(bx, value, "Type", "CurrencyTypes") is not { } t ||
            bx.GetAttribute(n, "value") != name || bx.GetAttribute(t, "value") != type ||
            !int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < 0) return null;
        return result;
    }
}
#endif
