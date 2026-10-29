#if PROFILE_UNLOCK_TESTING
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static FH6LocalCryptoTool.ObjectModelSource;

namespace FH6LocalCryptoTool;

/// <summary>Only model-specific ownership objectives, never Promo photos or other car-related challenges.</summary>
internal static class GarageCollectionJournalState
{
    internal const ulong CategoryId = 0x8D8EDF10324BBF91UL;

    internal static CollectionJournalCatalog.Challenge Reference(CollectionJournalCatalog.Challenge challenge)
    {
        if (challenge.Category != CategoryId || challenge.Objectives.Length != 1 || challenge.Objectives[0].Kind != "CarCollectionObjective") return challenge;
        // Generated reference metadata, independently checked against all 658
        // installed CarCollectionObjective.CarOrdinal values; not label guessing.
        var match = Regex.Match(challenge.Name, @" \[car ([1-9][0-9]{0,8})\]$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int car))
            throw new InvalidDataException("Reference Car Collection mapping is missing.");
        return challenge with { CollectionCarOrdinal = car };
    }

    internal static int ReadCar(XElement definition)
    {
        var objectives = Prop(definition, "Objectives").Elements("element").ToArray();
        if (objectives.Length != 1 || Value(objectives[0], "ObjectiveType") != "CarCollectionObjective" ||
            Value(objectives[0], "CanUncomplete") != "False" ||
            !int.TryParse(Value(objectives[0], "CarOrdinal"), NumberStyles.None, CultureInfo.InvariantCulture, out int car) || car <= 0) return 0;
        return car;
    }

    internal static CollectionJournalCatalog.Challenge FriendlyLabels(CollectionJournalCatalog.Challenge challenge)
    {
        int car = challenge.CollectionCarOrdinal > 0 ? challenge.CollectionCarOrdinal : challenge.PromoCarOrdinal;
        if (car <= 0 || FH6CarEditor.CarDisplayNames.ApprovedName(car) is not { } name) return challenge;
        // Some real models have no ChallengeData localization entry. Use the
        // approved model-ID name, not the hash text and not a hidden/test guess.
        string suffix = $" [car {car}]";
        return challenge with
        {
            Name = challenge.Name.StartsWith("ChallengeData.", StringComparison.Ordinal) ? name + suffix : challenge.Name,
            Description = challenge.Description.StartsWith("ChallengeData.", StringComparison.Ordinal)
                ? (challenge.CollectionCarOrdinal > 0 ? "Own the " : "Photograph the ") + name + suffix : challenge.Description,
        };
    }

    // Prepare on a private profile before the garage transaction. The session
    // adopts this result only after every car/part insert commits successfully.
    internal static ProfileContainer Stage(ProfileContainer original, IEnumerable<long> carIds, out string summary)
    {
        using var scope = ProfileGameData.Use(original);
        var cars = carIds.ToHashSet();
        var definitions = ProfileGameData.Current?.Challenges?.Values.AsEnumerable() ?? CollectionJournalCatalog.Data.Challenges;
        var candidates = definitions.Where(c => c.Category == CategoryId && c.CollectionCarOrdinal > 0 && cars.Contains(c.CollectionCarOrdinal)).ToArray();
        var matches = candidates.Where(c => !CollectionJournalCarPolicy.Excluded(c)).ToArray();
        int excluded = candidates.Length - matches.Length;
        string excludedSummary = $"{excluded} DLC Car Collection entry(s) excluded; saved Journal progress left unchanged.";
        if (matches.Length == 0) { summary = excluded > 0 ? excludedSummary : "No matching Car Collection Journal entries for these models."; return original; }
        if (!CollectionJournalState.TryRead(original, out var view, out string reason))
            throw new InvalidDataException("Car Collection Journal sync could not be verified; no cars were added. " + reason);
        var rows = view!.Rows.ToDictionary(r => r.Id);
        var supported = matches.Where(c => rows.TryGetValue(c.Id, out var row) && row.StateKnown && row.Supported).ToArray();
        var pending = supported.Where(c => !rows[c.Id].Completed).Select(c => c.Id).ToArray();
        int unavailable = matches.Length - supported.Length;
        summary = pending.Length == 0 ? "Car Collection Journal is already up to date." : "";
        var next = original;
        if (pending.Length > 0)
        {
            next = ProfileContainer.Parse(original.Serialize()); ProfileGameData.Copy(original, next);
            if (!CollectionJournalState.TryComplete(next, pending, out var change, out reason))
                throw new InvalidDataException("Car Collection Journal sync could not be verified; no cars were added. " + reason);
            // Ownership completion must not touch Promo captures or any other DB
            // table. The session's independent garage transaction owns the DB.
            if (!next.Database.AsSpan().SequenceEqual(original.Database))
                throw new InvalidDataException("Car Collection unexpectedly changed database content; no cars were added.");
            summary = $"Car Collection: {change.SelectedCompleted} entry(s) and {change.AutomaticCompleted} bonus(es) completed; points and wristbands synced.";
        }
        if (unavailable > 0)
            summary = (supported.Length == 0 ? "" : summary + " ") + $"{unavailable} matching Journal entry(s) missing or unsupported; left unchanged.";
        if (excluded > 0) summary += " " + excludedSummary;
        return next;
    }
}
#endif
