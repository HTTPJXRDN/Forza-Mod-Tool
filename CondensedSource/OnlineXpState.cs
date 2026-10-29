#if PROFILE_UNLOCK_TESTING
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Horizon Open rank XP, coupled driver XP and saved comparison counters.</summary>
public static class OnlineXpState
{
    const string Bucket = "CurrencyBucket_HorizonOpenXP_HorizonOpenXP";
    static readonly string[] Paths = { "/Main/XP", "/CompareStats/OnlineXP" };
    public sealed record Level(uint Xp, int Number, uint IntoLevel, uint? ToNextLevel);
    public sealed record Preview(Level Online, DriverXpState.Level Driver, long XpChange);

    public static bool TryRead(ProfileContainer? container, out Level? level, out string reason)
    {
        level = null; reason = "Online XP is unavailable; select matching game data for the level preview.";
        if (!TryFields(container, out _, out uint xp, out _, out _, out reason)) return false;
        if (TryCalculate(container, xp, out level)) return true;
        reason = "Select matching ObjectModelGame.zip for the online level preview."; return false;
    }
    public static bool TryCalculate(ProfileContainer? container, uint xp, out Level? level)
    {
        level = null; var thresholds = ProfileGameData.For(container)?.OnlineThresholds is { Length: >= 2 } live ? live : OnlineXpCatalog.Thresholds;
        int index = Array.BinarySearch(thresholds, xp); if (index < 0) index = ~index - 1;
        index = Math.Max(0, index);
        level = new(xp, index, xp - thresholds[index], index == thresholds.Length - 1 ? null : thresholds[index + 1] - xp); return true;
    }
    public static bool TryPreview(ProfileContainer? container, uint xp, out Preview? preview, out string reason)
    {
        preview = null;
        if (xp > int.MaxValue || !TryFields(container, out _, out uint previous, out _, out var values, out reason) || !TryCalculate(container, xp, out var online))
        { reason = "Online XP or matching game level data is not supported."; return false; }
        long delta = (long)xp - previous;
        if (values!.Any(value => value + delta is < 0 or > uint.MaxValue))
        { reason = "That value would put a linked driver/online XP counter outside its allowed range."; return false; }
        uint driverXp = checked((uint)(values![0] + delta));
        if (!DriverXpState.TryPreview(container, driverXp, out var driver, out reason)) return false;
        preview = new(online!, driver!, delta); reason = ""; return true;
    }
    public static bool TrySet(ProfileContainer? container, uint xp, out bool changed, out string reason)
    {
        changed = false;
        if (!TryPreview(container, xp, out var preview, out reason) || !TryFields(container, out _, out _, out _, out var values, out reason)
            || !ProfileXpEdit.TryDriver(container, preview!.Driver.Xp, out var edit, out reason)) return false;
        uint comparisonXp = checked((uint)(values![1] + preview!.XpChange));
        if (!edit!.TryOnline(container, xp, comparisonXp, out reason)) return false;
        return edit.TryCommit(container!, out changed, out reason);
    }
    static bool TryFields(ProfileContainer? container, out Bxml.Node? total, out uint xp,
        out PropertyTree.PropertyNode[]? fields, out long[]? values, out string reason)
    {
        total = null; xp = 0; fields = null; values = null; reason = "The linked online/driver XP fields are not recognized.";
        if (container?.SaveState is not { } bx || container.Properties is not { } properties) return false;
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" && n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == Bucket) == 1).ToArray();
        if (maps.Length != 1) return false;
        var nodes = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (nodes.Length != 1 || bx.GetAttribute(nodes[0], "type") != "CurrencyBucket") return false;
        Bxml.Node? Field(string id, string type)
        {
            var matches = nodes[0].Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
            return matches.Length == 1 && bx.GetAttribute(matches[0], "type") == type && matches[0].Children.Count == 0 ? matches[0] : null;
        }
        if (Field("Name", "string") is not { } name || bx.GetAttribute(name, "value") != "HorizonOpenXP"
            || Field("Type", "CurrencyTypes") is not { } type || bx.GetAttribute(type, "value") != "HorizonOpenXP"
            || Field("Total", "int") is not { } score || !uint.TryParse(bx.GetAttribute(score, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out xp) || xp > int.MaxValue) return false;
        var result = new List<PropertyTree.PropertyNode>(); var numbers = new List<long>();
        foreach (string path in Paths)
        {
            var matches = properties.Walk().Where(p => p.Path == path).Select(p => p.Node).ToArray();
            if (matches.Length != 1 || matches[0].TypeId is not (3 or 17) || matches[0].RawValue.Length != 4 || !matches[0].IsEditable
                || !uint.TryParse(matches[0].DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint number)) return false;
            result.Add(matches[0]); numbers.Add(number);
        }
        total = score; fields = result.ToArray(); values = numbers.ToArray(); reason = ""; return true;
    }
}
#endif
