#if PROFILE_UNLOCK_TESTING
using System.Globalization;
using System.Text.RegularExpressions;

namespace FH6LocalCryptoTool;

/// <summary>Journal-linked local PR completion/PBs; never online times, rewards or licenses.</summary>
internal static class PrStuntProgressState
{
    internal sealed record Mapping(ulong Challenge, PrStuntCatalog.Stunt Stunt, int Stars);
    static readonly Regex Stat = new(@"\AAmbient/ActivityCompletedShortWithEntity\.EntityName\.(?<entity>[a-z0-9_]+)\.Medal\.(?<medal>Bronze|Silver|Gold)\.Activity\.(?<activity>SpeedTrap|SpeedZone|DangerSign|DriftZone|Trailblazer)\.Track\.820\z", RegexOptions.CultureInvariant);
    internal static IReadOnlyDictionary<ulong, Mapping> ByChallenge { get; } = BuildMappings();

    static Dictionary<ulong, Mapping> BuildMappings()
    {
        var result = new Dictionary<ulong, Mapping>();
        foreach (var challenge in CollectionJournalCatalog.Data.Challenges)
        {
            if (challenge.Objectives.Length is < 1 or > 3 || challenge.Objectives.Any(o => o.Kind != "SimpleStatObjective" || o.Target != 1 || o.Stats.Length != 1)) continue;
            var paths = challenge.Objectives.Select(o => Stat.Match(o.Stats[0])).ToArray();
            if (paths.Any(p => !p.Success)) continue;
            string entity = paths[0].Groups["entity"].Value;
            if (!PrStuntCatalog.ByEntity.TryGetValue(entity, out var stunt) || paths.Any(p => p.Groups["entity"].Value != entity || p.Groups["activity"].Value != stunt.Activity)) continue;
            int[] medals = paths.Select(p => p.Groups["medal"].Value switch { "Gold" => 3, "Silver" => 2, _ => 1 }).Order().ToArray();
            int stars = medals[0];
            // Each row accepts its medal or any better medal. Require that exact
            // OR-list, not localized row names, number suffixes or guessed hashes.
            if (!medals.SequenceEqual(Enumerable.Range(stars, 4 - stars))) continue;
            result.Add(challenge.Id, new(challenge.Id, stunt, stars));
        }
        return result;
    }

    // Called only on the Journal transaction's private clone. Validate every
    // requested object before changing any attribute; unknown/missing layouts fail closed.
    internal static bool TrySync(ProfileContainer container, IEnumerable<ulong> ids, out string[] changedKeys, out string reason)
    {
        changedKeys = Array.Empty<string>(); reason = "";
        var requests = ids.Distinct().Where(ByChallenge.ContainsKey).Select(id => ByChallenge[id])
            .GroupBy(m => m.Stunt.Entity).Select(g => g.MaxBy(m => m.Stars)!).ToArray();
        if (requests.Length == 0) return true;
        if (container.SaveState is not { } bx) { reason = "PR-stunt save state is unavailable; nothing was changed."; return false; }
        var edits = new List<(Bxml.Node Node, string Value)>(); var keys = new List<string>();
        foreach (var request in requests)
        {
            var stunt = request.Stunt;
            if (!TryFields(bx, stunt.Key, out var complete, out var best) ||
                bx.GetAttribute(complete!, "value") is not ("true" or "false") ||
                !float.TryParse(bx.GetAttribute(best!, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out float old) || !float.IsFinite(old) || old < 0)
            { reason = $"The saved PR-stunt layout for {stunt.Entity} is not recognized; no Journal or stunt changes were made."; return false; }
            bool changed = false;
            if (bx.GetAttribute(complete!, "value") != "true") { edits.Add((complete!, "true")); changed = true; }
            float threshold = stunt.Threshold(request.Stars);
            // Preserve ALL better existing PBs, including a real score well above
            // our generated range. Repeating completion never rerolls a valid PB.
            if (old < threshold)
            {
                float generated = Generate(threshold, stunt.Activity == "DriftZone");
                edits.Add((best!, generated.ToString("R", CultureInfo.InvariantCulture))); changed = true;
            }
            if (changed) keys.Add(stunt.Key);
        }
        foreach (var edit in edits) bx.SetAttribute(edit.Node, "value", edit.Value);
        changedKeys = keys.ToArray(); return true;
    }

    internal static float Generate(float threshold, bool integral)
    {
        double upper = threshold * 1.02d;
        double value = threshold + Random.Shared.NextDouble() * (upper - threshold);
        if (integral) value = Math.Floor(value);
        float score = Math.Max(threshold, (float)value);
        // Float round-up may cross the promised 2% ceiling by one ULP.
        if (score > upper) score = float.BitDecrement(score);
        return score;
    }

    internal static bool TryFields(Bxml bx, string key, out Bxml.Node? complete, out Bxml.Node? best)
    {
        complete = best = null;
        var stunt = PrStuntCatalog.Items.SingleOrDefault(s => s.Key == key);
        if (stunt is null) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key) == 1).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count != 2) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != stunt.SaveType || values[0].Children.Count != 2) return false;
        var fields = values[0].Children;
        var completeFields = fields.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == "Completed" && bx.GetAttribute(n, "type") == "bool").ToArray();
        var bestFields = fields.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == "PersonalBest" && bx.GetAttribute(n, "type") == "float").ToArray();
        if (completeFields.Length != 1 || bestFields.Length != 1) return false;
        complete = completeFields[0]; best = bestFields[0];
        return complete.Attributes.Count == 3 && best.Attributes.Count == 3;
    }
}
#endif
