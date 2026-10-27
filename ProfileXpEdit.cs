using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>One validated, atomic XP edit shared by the driver and online controls.</summary>
internal sealed class ProfileXpEdit(uint driverXp)
{
    internal const string CareerPrefix = "CurrencyBucket_CareerXP_";
    const string OnlineKey = "CurrencyBucket_HorizonOpenXP_HorizonOpenXP";
    // Despite their legacy names, TotalSkills (u64) and WristbandLevel (u32)
    // mirrored the CareerXP sum in all inspected saves and natural XP gains.
    // Main/Level and Main/TotalXP do not consistently mirror it; preserve them.
    static readonly string[] DriverPaths = { "/Main/XP", "/Main/TotalSkills", "/CompareStats/WristbandLevel" };
    sealed record PropertyChange(string Path, ulong Before, ulong After);
    sealed record Bucket(string Key, string Name, string Type, Bxml.Node Total, int Value);
    sealed record BucketChange(Bucket Before, int After);
    readonly List<PropertyChange> properties = new();
    readonly List<BucketChange> buckets = new();

    internal static bool TryDriver(ProfileContainer? container, uint xp, out ProfileXpEdit? edit, out string reason)
    {
        edit = null; reason = "The driver XP totals or linked counters are missing or not safely recognized.";
        if (!TryCareerBuckets(container, out var ledger)) return false;
        var plan = new ProfileXpEdit(xp);
        foreach (string path in DriverPaths)
        {
            if (!TryProperty(container, path, out _, out ulong old)) return false;
            if (old != xp) plan.properties.Add(new(path, old, xp));
        }
        // CareerXP buckets are the earned-XP ledger. Normal gains rebuild the
        // driver's XP from their sum; changing only Main/XP is temporary.
        long delta = (long)xp - ledger.Sum(b => (long)b.Value);
        // Prefer the existing skills-XP bucket for a manual adjustment. This is
        // XP, NOT CarPerkSaveState's spendable skill points or mastery history.
        // Split large increases across existing int-sized buckets. Decreases
        // consume existing balances without ever making a bucket negative.
        foreach (var bucket in ledger.OrderBy(b => b.Name == "skills" ? 0 : 1).ThenByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal))
        {
            if (delta == 0) break;
            long amount = delta > 0 ? Math.Min(delta, int.MaxValue - (long)bucket.Value) : -Math.Min(-delta, bucket.Value);
            if (amount == 0) continue;
            plan.buckets.Add(new(bucket, checked((int)(bucket.Value + amount))));
            delta -= amount;
        }
        if (delta != 0) { reason = "That XP value cannot fit the save's existing XP buckets. Nothing was changed."; return false; }
        edit = plan; reason = ""; return true;
    }

    internal bool TryOnline(ProfileContainer? container, uint xp, uint comparisonXp, out string reason)
    {
        reason = "The online XP bucket or comparison counter is not safely recognized.";
        if (xp > int.MaxValue || !TryBucket(container, OnlineKey, "HorizonOpenXP", "HorizonOpenXP", out var bucket)
            || !TryProperty(container, "/CompareStats/OnlineXP", out _, out ulong old)) return false;
        if (bucket!.Value != xp) buckets.Add(new(bucket, (int)xp));
        if (old != comparisonXp) properties.Add(new("/CompareStats/OnlineXP", old, comparisonXp));
        reason = ""; return true;
    }

    internal bool TryCommit(ProfileContainer container, out bool changed, out string reason)
    {
        changed = false; reason = "The XP edit could not be validated. Nothing was changed.";
        if (!TryPrepare(container, out var originals, out var originalTotals)) return false;
        if (properties.Count == 0 && buckets.Count == 0) { reason = ""; return true; }
        var staged = ProfileContainer.Parse(container.Serialize());
#if PROFILE_UNLOCK_TESTING
        ProfileGameData.Copy(container, staged);
#endif
        if (!TryPrepare(staged, out var stagedProperties, out var stagedTotals)) return false;
        for (int i = 0; i < properties.Count; i++)
            if (!stagedProperties[i].TrySetFromText(properties[i].After.ToString(CultureInfo.InvariantCulture))) return false;
        for (int i = 0; i < buckets.Count; i++)
            if (!staged.SaveState!.SetAttribute(stagedTotals[i], "value", buckets[i].After.ToString(CultureInfo.InvariantCulture))) return false;
        byte[] bytes = staged.Serialize(); var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes) || !TryCareerBuckets(verified, out var ledger)
            || ledger.Sum(b => (long)b.Value) != driverXp) return false;
        foreach (string path in DriverPaths)
            if (!TryProperty(verified, path, out _, out ulong value) || value != driverXp) return false;
        foreach (var change in properties)
            if (!TryProperty(verified, change.Path, out _, out ulong value) || value != change.After) return false;
        foreach (var change in buckets)
            if (!TryBucket(verified, change.Before.Key, change.Before.Name, change.Before.Type, out var bucket) || bucket!.Value != change.After) return false;
        // Commit only the already-validated leaves. Keep the UI's node identities
        // and every unrelated pending edit, inventory, reward and binary record.
        for (int i = 0; i < buckets.Count; i++) container.SaveState!.SetAttribute(originalTotals[i], "value", buckets[i].After.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < properties.Count; i++) { originals[i].RawValue = (byte[])stagedProperties[i].RawValue.Clone(); originals[i].Edited = true; }
        changed = true; reason = ""; return true;
    }

    bool TryPrepare(ProfileContainer container, out List<PropertyTree.PropertyNode> nodes, out List<Bxml.Node> totals)
    {
        nodes = new(); totals = new();
        foreach (var change in properties)
        {
            if (!TryProperty(container, change.Path, out var node, out ulong value) || value != change.Before) return false;
            nodes.Add(node!);
        }
        foreach (var change in buckets)
        {
            if (!TryBucket(container, change.Before.Key, change.Before.Name, change.Before.Type, out var bucket) || bucket!.Value != change.Before.Value) return false;
            totals.Add(bucket.Total);
        }
        return true;
    }

    static bool TryProperty(ProfileContainer? container, string path, out PropertyTree.PropertyNode? node, out ulong value)
    {
        node = null; value = 0;
        var matches = container?.Properties?.Walk().Where(p => p.Path == path).Select(p => p.Node).ToArray();
        if (matches is not { Length: 1 }) return false;
        var field = matches[0]; bool wide = path == "/Main/TotalSkills";
        if (field.IsBag || !field.IsEditable || field.RawValue.Length != (wide ? 8 : 4)
            || (wide ? field.TypeId != 4 : field.TypeId is not (3 or 17))
            || !ulong.TryParse(field.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out value) || (!wide && value > uint.MaxValue)) return false;
        node = field; return true;
    }

    static bool TryCareerBuckets(ProfileContainer? container, out List<Bucket> buckets)
    {
        buckets = new();
        if (container?.SaveState is not { } bx) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element"))
        {
            var keyNodes = map.Children.Where(n => bx.Strings[n.NameIndex] == "key").ToArray();
            var candidateKeys = keyNodes.Select(n => bx.GetAttribute(n, "value") ?? "").Where(k => k.StartsWith(CareerPrefix, StringComparison.Ordinal)).ToArray();
            // Also reject a CareerXP currency hidden under an unexpected key:
            // silently ignoring it would make the next game's sum differ again.
            bool careerType = map.Children.Where(n => bx.Strings[n.NameIndex] == "value")
                .Any(n => n.Children.Any(p => bx.GetAttribute(p, "id") == "Type" && bx.GetAttribute(p, "value") == "CareerXP"));
            if (candidateKeys.Length == 0) { if (careerType) return false; continue; }
            if (keyNodes.Length != 1 || candidateKeys.Length != 1 || !keys.Add(candidateKeys[0]) || keys.Count > 4096) return false;
            string key = candidateKeys[0], name = key[CareerPrefix.Length..];
            if (name.Length is < 1 or > 100 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
                || !TryBucket(container, key, name, "CareerXP", out var bucket)) return false;
            buckets.Add(bucket!);
        }
        return buckets.Count > 0 && buckets.Sum(b => (long)b.Value) <= uint.MaxValue;
    }

    static bool TryBucket(ProfileContainer? container, string key, string name, string type, out Bucket? bucket)
    {
        bucket = null;
        if (container?.SaveState is not { } bx) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element"
            && n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key)).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count(c => bx.Strings[c.NameIndex] == "key") != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != "CurrencyBucket") return false;
        Bxml.Node? Field(string id, string fieldType)
        {
            var nodes = values[0].Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
            return nodes.Length == 1 && bx.GetAttribute(nodes[0], "type") == fieldType && nodes[0].Children.Count == 0
                && nodes[0].Attributes.Count(a => bx.Strings[a.KeyIndex] == "value") == 1 ? nodes[0] : null;
        }
        if (Field("Name", "string") is not { } n || bx.GetAttribute(n, "value") != name
            || Field("Type", "CurrencyTypes") is not { } t || bx.GetAttribute(t, "value") != type
            || Field("Total", "int") is not { } total || !int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 0) return false;
        bucket = new(key, name, type, total, number); return true;
    }
}
