#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Scoped native world progress on the Journal's private transaction clone.</summary>
internal static class ExplorationProgressState
{
    internal sealed record AttributeEdit(string Key, string Id, string Type, string Value);
    internal sealed record PropertyEdit(string Path, string Value);
    internal sealed record Change(AttributeEdit[] Attributes, PropertyEdit[] Properties, int Boards, int Mascots, int Landmarks, bool Regions, int Stories)
    {
        internal bool Changed => Boards + Mascots + Landmarks + Stories > 0 || Regions || Attributes.Length > 0 || Properties.Length > 0;
    }
    internal static readonly HashSet<string> Records = new(StringComparer.Ordinal)
    { "LandmarkSaveStateBrio", "MapRegionsStateBrio", "DiscoveryStateBrio", "BucketListChallengeState" };
    static readonly HashSet<string> Permanent = ExplorationProgressCatalog.Landmarks.ToHashSet(StringComparer.Ordinal);

    internal static bool TrySync(ProfileContainer container, ISet<ulong> selected, ISet<ulong> completed, out Change change, out string reason)
    {
        change = new(Array.Empty<AttributeEdit>(), Array.Empty<PropertyEdit>(), 0, 0, 0, false, 0); reason = "";
        var requests = selected.Where(ExplorationProgressCatalog.ByChallenge.ContainsKey).Select(id => ExplorationProgressCatalog.ByChallenge[id]).ToArray();
        if (requests.Length == 0) return true;
        reason = "World-progress layout is not recognized; no Journal or world edits were made.";
        if (container.SaveState is not { } bx || container.Career is null || container.Properties is null) return false;
        var attributes = new List<AttributeEdit>(); var properties = new List<PropertyEdit>();
        int boards = 0, mascots = 0, landmarks = 0, stories = 0; bool regions = false;
        bool PropertyFloor(string path, int minimum)
        {
            var nodes = container.Properties.Walk().Where(n => n.Path == path).ToArray();
            if (nodes.Length != 1 || nodes[0].Node.TypeId is not (3 or 17) || nodes[0].Node.RawValue.Length != 4) return false;
            uint old = BinaryPrimitives.ReadUInt32LittleEndian(nodes[0].Node.RawValue);
            if (old >= minimum) return true;
            string value = minimum.ToString(CultureInfo.InvariantCulture);
            if (!nodes[0].Node.TrySetFromText(value)) return false;
            properties.Add(new(path, value)); return true;
        }
        foreach (var request in requests.Where(r => r.Kind is "Board" or "Mascot"))
        {
            string key = request.Entity + "_save_state";
            var node = Field(bx, key, "DiscountBoardSaveState", "IsCollected", "bool");
            if (node is null || bx.GetAttribute(node, "value") is not ("true" or "false")) return false;
            if (bx.GetAttribute(node, "value") == "true") continue;
            bx.SetAttribute(node, "value", "true"); attributes.Add(new(key, "IsCollected", "bool", "true"));
            if (request.Kind == "Board") boards++; else mascots++;
        }
        if (requests.Any(r => r.Kind == "Board"))
        {
            reason = "The board counter layout is not recognized; no Journal or board changes were made.";
            var all = ExplorationProgressCatalog.ByChallenge.Values.Where(r => r.Kind == "Board").ToArray();
            if (all.Length != 200) return false;
            int Count(string? group)
            {
                int count = 0;
                foreach (var entry in all.Where(e => group is null || e.Entity.StartsWith("discount_board_xp_" + group + "_", StringComparison.Ordinal)))
                {
                    var field = Field(bx, entry.Entity + "_save_state", "DiscountBoardSaveState", "IsCollected", "bool");
                    if (field is null || bx.GetAttribute(field, "value") is not ("true" or "false")) return -1;
                    if (bx.GetAttribute(field, "value") == "true") count++;
                }
                return count;
            }
            foreach (var spec in new[] { ("/CompareStats/DiscountBoardsSmashed", (string?)null), ("/CompareStats/SmallValueBoardsSmashed", "a"), ("/CompareStats/MediumValueBoardsSmashed", "b"), ("/CompareStats/HighValueBoardsSmashed", "c") })
            { int n = Count(spec.Item2); if (n < 0 || !PropertyFloor(spec.Item1, n)) return false; }
        }
        var locationRequests = requests.Where(r => r.Kind == "Landmark").ToArray();
        if (locationRequests.Length > 0)
        {
            reason = "The permanent-landmark layout is not recognized; no Journal or discovery changes were made.";
            if (!TrySet(container, "LandmarkSaveStateBrio", "LandmarkSaveState", out var record, out var old)) return false;
            var next = old!.ToList();
            // Full Journal landmark discovery also covers seven permanent scene
            // landmarks without their own Journal row. Temporary meets excluded.
            bool full = ExplorationProgressCatalog.ByChallenge.Values.Where(m => m.Kind == "Landmark").All(m => selected.Contains(m.Challenge) && completed.Contains(m.Challenge));
            var keys = full ? ExplorationProgressCatalog.Landmarks.Select(ExplorationProgressCatalog.Hash)
                : locationRequests.Where(r => Permanent.Contains(r.Entity)).Select(r => r.Key);
            foreach (ulong key in keys.Distinct()) if (!next.Contains(key)) { next.Add(key); landmarks++; }
            if (landmarks > 0) container.Career.ReplacePayload(record!, SerializeSet(next));
            if (!PropertyFloor("/MiscStats/LocationsDiscovered", next.Count)) return false;
        }
        if (requests.Any(r => r.Kind == "Region"))
        {
            reason = "The region/fog-map layout is not recognized; no Journal or discovery changes were made.";
            // Full-region discovery needs all ten verified Journal requirements
            // met. Already-complete rows count, so Ctrl+A with Unfinished only
            // works without needing to reselect those rows.
            if (!ExplorationProgressCatalog.ByChallenge.Values.Where(m => m.Kind == "Region").All(m => completed.Contains(m.Challenge)))
            { reason = "Native region sync currently requires all ten region Journal entries completed. Select the remaining region discovery rows together; nothing was changed."; return false; }
            if (!TrySet(container, "MapRegionsStateBrio", "MapRegionsSaveState", out var record, out var old)) return false;
            var next = old!.ToList(); foreach (ulong key in ExplorationProgressCatalog.AllRegions) if (!next.Contains(key)) next.Add(key);
            if (next.Count != old!.Count) { container.Career.ReplacePayload(record!, SerializeSet(next)); regions = true; }
            if (!TryRevealFog(container, out bool fogChanged)) return false;
            regions |= fogChanged;
        }
        var storyRequests = requests.Where(r => r.Kind == "Story").Select(r => r.Key).ToHashSet();
        if (storyRequests.Count > 0)
        {
            reason = "The Story chapter layout is not recognized; no Journal or Story changes were made.";
            var records = container.Career.Records.Where(r => r.Name == "BucketListChallengeState").ToArray();
            if (records.Length != 1 || records[0].Serializer != "HorizonStoryChapterState" || !Placeholder(bx, "BucketListChallengeState", "HorizonStoryChapterState")) return false;
            var source = records[0].Payload;
            if (source.Length < 8 || U32(source, 0) != 2 || U32(source, 4) > 100000 || source.Length != 8L + 24L * U32(source, 4)) return false;
            var offsets = new Dictionary<ulong, int>();
            for (int at = 8; at < source.Length; at += 24)
                if (!offsets.TryAdd(U64(source, at), at) || source[at + 8] > 1 || source[at + 9] > 1 || source[at + 10] > 1 || source[at + 15] > 1 || U32(source, at + 11) > 3) return false;
            var bytes = (byte[])source.Clone();
            foreach (ulong chapter in storyRequests)
            {
                if (!offsets.TryGetValue(chapter, out int at)) return false;
                uint stars = (uint)ExplorationProgressCatalog.ByChallenge.Values.Where(m => m.Kind == "Story" && m.Key == chapter && completed.Contains(m.Challenge)).Max(m => m.Stars);
                bool touched = false;
                // The leading completion bit and star count agree in all three
                // natural fixtures. Preserve all other flags and chronology.
                if (bytes[at + 8] != 1) { bytes[at + 8] = 1; touched = true; }
                if (U32(bytes, at + 11) < stars) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 11), stars); touched = true; }
                if (touched) stories++;
            }
            if (stories > 0) container.Career.ReplacePayload(records[0], bytes);
            // The final i64 is a last-played UTC timestamp, NOT a finishing
            // time. No verified local PB-time field exists in this record.
            // Do not write a random performance time over that timestamp.
        }
        change = new(attributes.ToArray(), properties.ToArray(), boards, mascots, landmarks, regions, stories);
        reason = ""; return true;
    }

    static bool TryRevealFog(ProfileContainer container, out bool changed)
    {
        changed = false;
        var records = container.Career!.Records.Where(r => r.Name == "DiscoveryStateBrio").ToArray();
        if (records.Length != 1 || records[0].Serializer != "DiscoveryState" || !Placeholder(container.SaveState!, "DiscoveryStateBrio", "DiscoveryState")) return false;
        var b = records[0].Payload; if (b.Length < 32 || U32(b, 0) != 2) return false;
        long at = 4;
        for (int i = 0; i < 3; i++)
        {
            if (at > b.Length - 4) return false;
            uint n = U32(b, (int)at); at += 4;
            if (n > 100000 || at + n * 8L > b.Length) return false;
            at += n * 8L;
        }
        if (at > b.Length - 12 || U32(b, (int)at) != 1024 || U32(b, (int)at + 4) != 1024 ||
            U32(b, (int)at + 8) != 1048576 || b.Length - at != 1048712) return false;
        int start = checked((int)at + 12);
        if (b.AsSpan(start, 1048576).IndexOfAnyExcept((byte)0) < 0) return true;
        byte[] bytes = (byte[])b.Clone(); bytes.AsSpan(start, 1048576).Clear();
        container.Career.ReplacePayload(records[0], bytes); changed = true; return true;
    }
    internal static Bxml.Node? Field(Bxml bx, string key, string type, string id, string fieldType)
    {
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" && n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key)).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count != 2) return null;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value" && bx.GetAttribute(n, "type") == type).ToArray();
        if (values.Length != 1 || values[0].Attributes.Count != 1 || values[0].Children.Count != 1) return null;
        var nodes = values[0].Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id && bx.GetAttribute(n, "type") == fieldType).ToArray();
        return nodes is { Length: 1 } && nodes[0].Attributes.Count == 3 && nodes[0].Children.Count == 0 ? nodes[0] : null;
    }
    static bool TrySet(ProfileContainer c, string name, string serializer, out BinaryCareer.Record? record, out List<ulong>? values)
    {
        record = null; values = null;
        var records = c.Career!.Records.Where(r => r.Name == name).ToArray();
        if (records.Length != 1 || records[0].Serializer != serializer || !Placeholder(c.SaveState!, name, serializer)) return false;
        var b = records[0].Payload; if (b.Length < 8 || U32(b, 0) != 1 || U32(b, 4) > 100000 || b.Length != 8L + U32(b, 4) * 8L) return false;
        var result = new List<ulong>(); var unique = new HashSet<ulong>();
        for (int at = 8; at < b.Length; at += 8) { ulong id = U64(b, at); if (!unique.Add(id)) return false; result.Add(id); }
        record = records[0]; values = result; return true;
    }
    static byte[] SerializeSet(List<ulong> values)
    {
        var bytes = new byte[checked(8 + values.Count * 8)]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)values.Count);
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8 + i * 8), values[i]);
        return bytes;
    }
    static bool Placeholder(Bxml bx, string key, string type)
    {
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" && n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key)).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count != 2) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value" && bx.GetAttribute(n, "type") == type).ToArray();
        return values.Length == 1 && values[0].Attributes.Count == 1 && values[0].Children.Count == 0;
    }
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
}
#endif
