#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static FH6LocalCryptoTool.ObjectModelSource;

namespace FH6LocalCryptoTool;

internal static class RuntimeProfileCatalog
{
    internal static ProfileGameData.Snapshot Read(string path)
    {
        using var source = new ObjectModelSource(path);
        var labels = new GameLabels(Path.Combine(Path.GetDirectoryName(path)!, "stripped", "StringTables", "EN"));
        var warnings = new List<string>();
        T Part<T>(string name, Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or OverflowException or ArgumentException or IOException or KeyNotFoundException)
            { warnings.Add(name + ": " + ex.Message); return fallback; }
        }
        var online = Part("Online XP thresholds", () =>
        {
            var entries = Prop(source.Single("HorizonOpenXPMap"), "HorizonOpenXPMap").Elements("map_element")
                .ToDictionary(n => int.Parse((string)n.Element("key")!.Attribute("value")!, CultureInfo.InvariantCulture), n => uint.Parse(Value(n.Element("value")!, "XPRequired"), CultureInfo.InvariantCulture));
            if (entries.Count is < 2 or > 10001 || !entries.Keys.Order().SequenceEqual(Enumerable.Range(0, entries.Count))) throw new InvalidDataException("Online level keys are not contiguous.");
            var thresholds = entries.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
            if (thresholds[0] != 0 || thresholds.Skip(1).Where((v, i) => v <= thresholds[i]).Any()) throw new InvalidDataException("Online XP thresholds are not increasing.");
            return thresholds;
        }, OnlineXpCatalog.Thresholds);
        var slots = new HashSet<string> { "UpperBodyLayer1", "UpperBodyLayer2", "LowerBody", "Feet", "Hat", "Outfit", "Socks", "Dress", "Hands", "Face", "LeftWrist", "Eye" };
        var clothing = Part("Clothes", () => source.Data("ForzaCharacterCustomisationItemData").Where(p => slots.Contains(Value(p.Value, "SlotID")) && Value(p.Value, "IsRemoveItem") == "False")
            .Select(p => new CharacterClothingUnlockState.Clothing(p.Key, labels.Resolve(Value(p.Value, "DisplayName"), Value(p.Value, "ItemID")), Value(p.Value, "SlotID"))).OrderBy(p => p.Id).ToArray(), CharacterClothingCatalog.Items.ToArray());
        var horns = Part("Horns", () =>
        {
            var data = source.Data("CarHornData"); CheckMap(source.Single("CarHornMap"), data.Values.Select(n => Value(n, "HornId")));
            return data.Select(p =>
            {
                int category = int.Parse(Value(Prop(p.Value, "Category"), "Key"), CultureInfo.InvariantCulture);
                if (category is < 0 or > 2) throw new InvalidDataException("Unknown horn category.");
                return new CarHornUnlockState.Horn(p.Key, labels.Resolve(Value(p.Value, "DisplayName"), Value(p.Value, "AudioHornName")), category);
            }).OrderBy(p => p.Id).ToArray();
        }, CarHornCatalog.Items.ToArray());
        var emotes = Part("Emotes", () =>
        {
            var data = source.Data("EmoteData"); CheckMap(source.Single("EmoteDataMap"), data.Values.Select(n => Value(n, "Id")));
            return data.Select(p => { if (Hash(Guid.Parse(Value(p.Value, "Id")).ToString("D")) != p.Key) throw new InvalidDataException("Emote identity mismatch."); return new EmoteUnlockState.Emote(p.Key, labels.Resolve(Value(p.Value, "Name"), Value(p.Value, "MojoClip"))); }).OrderBy(p => p.Id).ToArray();
        }, EmoteCatalog.Items.ToArray());
        var phrases = Part("Phrases", () =>
        {
            var data = source.Data("QuickChatData"); CheckMap(Prop(source.Single("QuickChatDataMap"), "QuickChatData"), data.Values.Select(n => Value(n, "PhraseId")));
            var knownJournal = ForzaLinkPhraseCatalog.Items.Where(p => p.JournalReward).Select(p => p.Id).ToHashSet();
            return data.Where(p => Value(p.Value, "RequiresUnlock") == "True" && Value(p.Value, "Hidden") == "False" && Value(p.Value, "SpecialId") == "None")
                .Select(p => { if (Hash(Guid.Parse(Value(p.Value, "PhraseId")).ToString("D")) != p.Key) throw new InvalidDataException("Phrase identity mismatch."); return new ForzaLinkPhraseUnlockState.Phrase(p.Key, labels.Resolve(Value(p.Value, "ChatMessage"), $"Phrase {p.Key:X16}"), knownJournal.Contains(p.Key)); }).OrderBy(p => p.Id).ToArray();
        }, ForzaLinkPhraseCatalog.Items.ToArray());
        var journal = Part<CollectionJournalCatalog.Catalog?>("Collection Journal", () => Journal(source, labels), null);
        var badges = Part("Badges", () => Badges(source, labels), BadgeCatalog.Items.ToArray());
        var rewards = Part("Journal reward links", () => Rewards(source, journal ?? CollectionJournalCatalog.Data, horns, phrases), JournalRewardUnlockCatalog.Items.ToArray());
        var protectedIds = rewards.Where(r => r.Kind == JournalRewardKind.Phrase).Select(r => r.Item).ToHashSet();
        phrases = phrases.Select(p => p with { JournalReward = p.JournalReward || protectedIds.Contains(p.Id) }).ToArray();
        if (warnings.Any(w => w.StartsWith("Journal reward links:", StringComparison.Ordinal)))
        {
            // Do not expose newly added rewards as independent inventory grants
            // when their dependency graph could not be validated.
            var knownHorns = CarHornCatalog.Items.Select(h => h.Id).ToHashSet();
            var knownPhrases = ForzaLinkPhraseCatalog.Items.Select(h => h.Id).ToHashSet();
            horns = horns.Where(h => knownHorns.Contains(h.Id)).ToArray();
            phrases = phrases.Where(h => knownPhrases.Contains(h.Id)).ToArray();
        }
        foreach (var group in new[] { clothing.Select(c => c.Id), horns.Select(c => c.Id), emotes.Select(c => c.Id), phrases.Select(c => c.Id) })
        { var ids = group.ToArray(); if (ids.Length is < 1 or > 10000 || ids.Contains(0UL) || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Invalid inventory definition coverage."); }
        var playlists = Part("Festival Playlist", () => PlaylistActivityCatalog.Read(source, labels), Array.Empty<PlaylistActivityCatalog.Series>());
        var dlcCars = Part("DLC Car Collection exclusions", () => CollectionJournalCarPolicy.ReadInstalled(path), CollectionJournalCarPolicy.ReferenceDlcCars);
        return new(path, source.Fingerprint, online, clothing, horns, emotes, phrases, badges, journal, rewards, warnings.ToArray())
            { Playlists = playlists, DlcCarOrdinals = dlcCars };
    }
    static void CheckMap(XElement map, IEnumerable<string> guids)
    {
        var expected = guids.Select(g => Guid.Parse(g).ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = map.Name == "property" ? map.Elements("map_element") : map.Elements("property").SelectMany(p => p.Elements("map_element"));
        var keys = entries.Where(n => n.Element("key")?.Attribute("value") != null)
            .Select(n => Guid.Parse((string)n.Element("key")!.Attribute("value")!).ToString("D")).ToArray();
        if (keys.Length != expected.Count || !expected.SetEquals(keys)) throw new InvalidDataException("Inventory map coverage differs from definitions.");
    }
    static Dictionary<ulong, XElement> Map(ObjectModelSource source, string type, string property) => Prop(source.Single(type), property).Elements("map_element")
        .ToDictionary(n => Hash((string)n.Element("key")!.Attribute("value")!), n => n.Element("value")!);

    static CollectionJournalCatalog.Catalog Journal(ObjectModelSource source, GameLabels labels)
    {
        var items = Map(source, "CollectionItemChallengeDataSet", "Data");
        var challenges = Map(source, "ChallengeDataSet", "Data"); var stats = Map(source, "ChallengeStatsSet", "Stats");
        var threads = source.Data("ProgressionThreads"); var categories = source.Data("CollectionCampaignCategoryData");
        var promoCars = Prop(source.Single("CollectionCampaignPhotographyMapping"), "CarsToItems").Elements("map_element")
            .SelectMany(n => n.Element("value")!.Elements("element").Select(e => (Id: Hash((string)e.Attribute("value")!), Car: int.Parse((string)n.Element("key")!.Attribute("value")!, CultureInfo.InvariantCulture))))
            .ToDictionary(p => p.Id, p => p.Car);
        var categoryRecords = new List<CollectionJournalCatalog.Category>();
        foreach (var group in items.GroupBy(p => Value(p.Value, "ParentCategoryId")))
        {
            ulong id = Hash(group.Key); var category = categories[id];
            var thread = threads[Hash(Value(Prop(Prop(category, "ProgressionThread"), "Id"), "Object"))];
            string bucket = Value(thread, "BucketID");
            if (Value(thread, "CurrencyType") != "CollectionCategoryProgress" || !Regex.IsMatch(bucket, "^cc_[a-z0-9_]{1,100}$")) throw new InvalidDataException("Unsupported Journal category currency.");
            categoryRecords.Add(new(id, group.Key, labels.Resolve(Value(category, "DisplayName")), bucket, Value(category, "CampaignId") == "e366b86a-fbce-3a49-a0a8-2895039655db", group.Count(), group.Sum(p => int.Parse(Value(p.Value, "CampaignProgressReward"), CultureInfo.InvariantCulture))));
        }
        var result = new List<CollectionJournalCatalog.Challenge>();
        foreach (var item in items)
        {
            ulong linked = Hash(Value(Prop(item.Value, "Challenge"), "Key")); var definition = challenges[linked];
            var objectives = new List<CollectionJournalCatalog.Objective>();
            foreach (var objective in Prop(definition, "Objectives").Elements("element"))
            {
                string kind = Value(objective, "ObjectiveType"); uint target = 1; var paths = new List<string>();
                if (kind is "SimpleStatObjective" or "TempStatObjective")
                {
                    var bucket = Prop(objective, "StatsBucket");
                    if (Value(bucket, "ThresholdComparison") is not ("GreaterThanOrEqual" or "Equal")) throw new InvalidDataException("Unsupported Journal comparison.");
                    target = uint.Parse(Value(bucket, "ThresholdValue"), CultureInfo.InvariantCulture);
                    foreach (var stat in Prop(bucket, "Stats").Elements("element")) paths.Add(Value(stats[Hash(Value(stat, "Key"))], "Stat"));
                }
                else if (kind is not ("CarCollectionObjective" or "TakePhotoObjective")) throw new InvalidDataException("New Journal objective kind: " + kind);
                var dependencies = new List<ulong>(); bool global = false;
                foreach (string path in paths)
                {
                    var match = Regex.Match(path, @"^CollectionCampaign/CollectionItemCompleted\.Item\.\*\.Category\.([0-9a-f-]{36}|\*)\.Campaign\.\*\.Track\.\*$");
                    if (match.Success) { if (match.Groups[1].Value == "*") global = true; else dependencies.Add(Hash(match.Groups[1].Value)); }
                }
                int sources = dependencies.Count + (global ? 1 : 0);
                if (sources > 1 || (sources > 0 && (sources != paths.Count || Value(Prop(objective, "StatsBucket"), "ThresholdComparison") != "GreaterThanOrEqual"))) throw new InvalidDataException("Unsupported Journal dependency aggregation.");
                objectives.Add(new(Hash(Value(objective, "Id")), target, kind, dependencies.ToArray(), global, paths.ToArray()));
            }
            if (objectives.Count is < 1 or > 4096 || objectives.Select(o => o.Id).Distinct().Count() != objectives.Count) throw new InvalidDataException("Invalid objective coverage.");
            string name = labels.Resolve(Value(definition, "Name")), description = labels.Resolve(Value(definition, "Description"));
            string? car = Prop(definition, "Objectives").Elements("element").Select(o => Value(o, "CarOrdinal")).FirstOrDefault(v => v.Length > 0);
            if (car != null) { name += " [car " + car + "]"; description += " [car " + car + "]"; }
            int points = int.Parse(Value(item.Value, "CampaignProgressReward"), CultureInfo.InvariantCulture);
            if (points is < 0 or > 1_000_000) throw new InvalidDataException("Invalid Journal points.");
            ulong categoryId = Hash(Value(item.Value, "ParentCategoryId"));
            int promoCar = categoryId == PromoPhotoProgressState.CategoryId ? PromoPhotoProgressState.ReadCar(definition) : 0;
            result.Add(GarageCollectionJournalState.FriendlyLabels(new(item.Key, linked, uint.Parse(Value(definition, "DataVersion"), CultureInfo.InvariantCulture), categoryId, name, description, labels.Resolve(Value(item.Value, "Subtitle")), points, objectives.ToArray())
                { PromoCarOrdinal = promoCar > 0 && promoCars.GetValueOrDefault(item.Key) == promoCar ? promoCar : 0,
                  CollectionCarOrdinal = categoryId == GarageCollectionJournalState.CategoryId ? GarageCollectionJournalState.ReadCar(definition) : 0 }));
        }
        if (result.Count is < 1 or > 100_000 || categoryRecords.Count is < 1 or > 4096 || categoryRecords.Select(c => c.Bucket).Distinct().Count() != categoryRecords.Count) throw new InvalidDataException("Invalid Journal catalog coverage.");
        return new(categoryRecords.ToArray(), result.ToArray());
    }

    static BadgeCatalog.Badge[] Badges(ObjectModelSource source, GameLabels labels)
    {
        var badges = Prop(source.Single("BadgeDefinitionSet"), "Definitions").Elements("map_element").ToDictionary(n => uint.Parse((string)n.Element("key")!.Attribute("value")!, CultureInfo.InvariantCulture), n => n.Element("value")!);
        var categories = Prop(source.Single("BadgeCategorySet"), "Data").Elements("map_element").ToDictionary(n => (string)n.Element("key")!.Attribute("value")!, n => labels.Resolve(Value(n.Element("value")!, "DisplayName")));
        var challenges = Map(source, "ChallengeDataSet", "Data"); var result = new List<BadgeCatalog.Badge>();
        foreach (var item in Map(source, "BadgeChallengeDataSet", "Data"))
        {
            uint id = uint.Parse(Value(item.Value, "BadgeId"), CultureInfo.InvariantCulture); ulong linked = Hash(Value(Prop(item.Value, "Data"), "Key")); var def = challenges[linked];
            if (Value(def, "Repeatable") != "False" || Value(def, "MustCompleteInOrder") != "False" || Value(def, "Comparison") is not ("AND" or "OR")) throw new InvalidDataException("Unsupported badge semantics.");
            var objectives = Prop(def, "Objectives").Elements("element").Select(o =>
            {
                if (Value(o, "ObjectiveType") is not ("SimpleStatObjective" or "TempStatObjective")) throw new InvalidDataException("Unsupported badge objective.");
                var bucket = Prop(o, "StatsBucket"); string comparison = Value(bucket, "ThresholdComparison");
                if (comparison is not ("GreaterThanOrEqual" or "Equal")) throw new InvalidDataException("Unsupported badge comparison.");
                return new BadgeCatalog.Objective(Hash(Value(o, "Id")), uint.Parse(Value(bucket, "ThresholdValue"), CultureInfo.InvariantCulture), comparison == "Equal");
            }).ToArray();
            result.Add(new(id, item.Key, linked, uint.Parse(Value(def, "DataVersion"), CultureInfo.InvariantCulture), labels.Resolve(Value(def, "Name"), "Badge " + id), labels.Resolve(Value(def, "Description")), categories[Value(Prop(badges[id], "Category"), "Key")], objectives));
        }
        if (result.Count is < 1 or > 10000 || result.Select(b => b.Id).Distinct().Count() != result.Count) throw new InvalidDataException("Invalid badge definition coverage.");
        return result.ToArray();
    }

    static JournalRewardUnlockCatalog.Reward[] Rewards(ObjectModelSource source, CollectionJournalCatalog.Catalog journal, CarHornUnlockState.Horn[] horns, ForzaLinkPhraseUnlockState.Phrase[] phrases)
    {
        var categories = journal.Categories.ToDictionary(c => c.Bucket, StringComparer.Ordinal);
        var threads = source.Data("ProgressionThreads").Where(p => Value(p.Value, "CurrencyType") == "CollectionCategoryProgress" && categories.ContainsKey(Value(p.Value, "BucketID"))).ToDictionary(p => p.Key, p => categories[Value(p.Value, "BucketID")]);
        var result = new List<JournalRewardUnlockCatalog.Reward>(); var hornIds = horns.Select(h => h.Id).ToHashSet(); var phraseIds = phrases.Select(p => p.Id).ToHashSet();
        foreach (var objective in source.Data("ProgressionThreadObjectives").Values)
        {
            string reference = Value(Prop(Prop(objective, "ProgressionThread"), "Id"), "Object");
            if (!threads.TryGetValue(Hash(reference), out var category)) continue;
            int target = int.Parse(Value(objective, "CumulativeRequired"), CultureInfo.InvariantCulture);
            if (target is < 0 or > 100_000_000) throw new InvalidDataException("Invalid reward milestone.");
            foreach (var reward in objective.Elements("property").Where(p => ((string?)p.Attribute("id"))?.Contains("Reward", StringComparison.Ordinal) == true))
                foreach (var idNode in reward.Descendants("property").Where(p => (string?)p.Attribute("id") == "Object"))
                {
                    ulong id = Hash((string)idNode.Attribute("value")!);
                    if (hornIds.Contains(id)) result.Add(new(JournalRewardKind.Horn, id, category.Id, target, category.Name));
                    else if (phraseIds.Contains(id)) result.Add(new(JournalRewardKind.Phrase, id, category.Id, target, category.Name));
                }
        }
        if (result.Count > 20000 || result.Select(r => (r.Kind, r.Item)).Distinct().Count() != result.Count) throw new InvalidDataException("Ambiguous reward requirements.");
        return result.ToArray();
    }
}

internal sealed class GameLabels(string root)
{
    readonly Dictionary<string, Dictionary<uint, string>> tables = new(StringComparer.Ordinal);
    internal string Resolve(string reference, string? fallback = null)
    {
        int dot = reference.IndexOf('.'); if (dot < 1) return reference.Length == 0 ? fallback ?? "" : reference;
        string table = reference[..dot];
        if (!Regex.IsMatch(table, "^[A-Za-z0-9_-]{1,100}$")) return fallback ?? reference;
        if (!tables.TryGetValue(table, out var values))
        {
            string path = Path.Combine(root, table + ".str");
            try { values = File.Exists(path) ? Read(path) : new(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OverflowException) { values = new(); }
            tables.Add(table, values);
        }
        uint hash = uint.MaxValue; foreach (char c in reference[(dot + 1)..]) { hash ^= c; hash = (hash << 7) | (hash >> 25); }
        return values.GetValueOrDefault(hash) ?? fallback ?? reference;
    }
    static Dictionary<uint, string> Read(string path)
    {
        if (new FileInfo(path).Length > 64_000_000) throw new InvalidDataException(); byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 136 || bytes[0] != 0 || bytes[1] != 8) throw new InvalidDataException();
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        int tables = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(130)), final = checked(132 + tables * 4); var values = new Dictionary<uint, string>();
        if (tables is < 1 or > 16 || final > bytes.Length) throw new InvalidDataException();
        for (int i = 0; i < tables; i++)
        {
            int at = checked((int)U32(132 + i * 4)); if (at != final || at > bytes.Length - 12) throw new InvalidDataException();
            int tableSize = checked((int)U32(at)), stringSize = checked((int)U32(at + 4)), count = checked((int)U32(at + 8));
            int start = checked(at + 12 + count * 8), end = checked(at + 12 + tableSize);
            if (count > 500000 || end > bytes.Length || start + stringSize != end) throw new InvalidDataException();
            for (int j = 0; j < count; j++)
            {
                uint id = U32(at + 12 + j * 8); int text = checked(start + (int)U32(at + 16 + j * 8)); if (text < start || text >= end) throw new InvalidDataException();
                int zero = Array.IndexOf(bytes, (byte)0, text, end - text); if (zero < 0) throw new InvalidDataException(); string value = new UTF8Encoding(false, true).GetString(bytes, text, zero - text);
                if (i == 0) { if (!values.TryAdd(id, value) && values[id] != value) throw new InvalidDataException(); }
            }
            final = end;
        }
        if (final != bytes.Length) throw new InvalidDataException(); return values;
    }
}
#endif
