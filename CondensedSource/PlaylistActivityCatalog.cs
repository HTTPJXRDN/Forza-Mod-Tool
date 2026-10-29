#if PROFILE_UNLOCK_TESTING
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using static FH6LocalCryptoTool.ObjectModelSource;

namespace FH6LocalCryptoTool;

/// <summary>Definition-backed, scoring Festival Playlist activities. No reward grants.</summary>
internal static class PlaylistActivityCatalog
{
    internal sealed record Activity(uint Type, ulong Id, int Stages, int Points);
    internal sealed record Season(uint Index, Activity[] Activities)
    {
        internal int Maximum => Activities.Sum(a => a.Points);
    }
    internal sealed record Series(uint Id, string Name, Season[] Seasons)
    {
        internal int Maximum => Seasons.Sum(s => s.Maximum);
    }

    internal static Series[] Read(ObjectModelSource source, GameLabels labels)
    {
        var locals = source.Data("LocalSeasonInstances");
        var schedules = source.Data("FestivalPassSchedule");
        var dailies = source.Data("ForzathonDailyChallengeSchedule");
        var weeklies = source.Data("ForzathonWeeklyChallengeSchedule");
        var info = source.Data("FestivalPassEventInfo").Values.ToArray();
        var points = Prop(source.Single("FestivalPassPointsSettings"), "PointsData").Elements("map_element")
            .Single(n => (string?)n.Element("key")?.Attribute("value") == "-1").Element("value")!
            .Elements("map_element").ToDictionary(n => (string)n.Element("key")!.Attribute("value")!,
                n => int.Parse(Value(n.Element("value")!, "Points"), CultureInfo.InvariantCulture));
        int Points(string kind) => points.TryGetValue(kind, out int n) && n is > 0 and <= 1000
            ? n : throw new InvalidDataException("Unknown Playlist point award: " + kind);
        string Ref(XElement value, string field) => Value(Prop(Prop(value, field), "Id"), "Object");
        string[] Values(XElement value, string field) => value.Elements("property").SingleOrDefault(p => (string?)p.Attribute("id") == field)?
            .Elements("element").Select(n => (string)n.Attribute("value")!).ToArray() ?? Array.Empty<string>();
        var orders = Prop(source.Single("SeasonScheduleOrderMap"), "SeasonScheduleOrderData").Elements("map_element")
            .Select(n => n.Element("value")!).Where(n => Value(n, "Series") != "0")
            .GroupBy(n => uint.Parse(Value(n, "Series"), CultureInfo.InvariantCulture));
        var result = new List<Series>();
        foreach (var group in orders)
        {
            var seasons = new List<Season>(); string name = "Series " + group.Key;
            var monthly = new HashSet<ulong>();
            foreach (var order in group)
            {
                string localId = Ref(order, "SeasonInstance"); var local = locals[Hash(localId)];
                uint index = Value(local, "TargetSeasonBrio") switch { "Summer" => 0, "Autumn" => 1, "Winter" => 2, "Spring" => 3, _ => throw new InvalidDataException("Unknown Playlist season.") };
                var schedule = schedules[Hash(Ref(order, "FestivalPassSchedule"))];
                name = labels.Resolve(Value(schedule, "SeriesName"), "Series " + group.Key);
                var activities = new List<Activity>();
                void Add(uint type, ulong id, int stages, string kind, int multiple = 1)
                {
                    if (id == 0 || stages is < 1 or > 64 || activities.Any(a => a.Type == type && a.Id == id)) throw new InvalidDataException("Ambiguous Playlist activity.");
                    activities.Add(new(type, id, stages, checked(Points(kind) * multiple)));
                }
                void Guids(string field, uint type, string kind)
                { foreach (string id in Values(local, field)) Add(type, Hash(Guid.Parse(id).ToString("D")), 1, kind); }
                var events = info.Where(n => Ref(n, "LocalSeasonInstance").Equals(localId, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string id in Values(local, "CareerEventCollections"))
                {
                    string[] kinds = events.Where(n => Value(n, "EventId") == id).Select(n => Value(n, "EventType")).Distinct().ToArray();
                    if (kinds.Length != 1 || kinds[0] is not ("SeasonalEvent" or "Trial")) throw new InvalidDataException("Unmapped seasonal championship.");
                    Add(kinds[0] == "Trial" ? 1U : 0U, ulong.Parse(id, CultureInfo.InvariantCulture), 1, kinds[0]);
                }
                foreach (string field in new[] { "DangerSignObjectives", "SpeedTrapObjectives", "SpeedZoneObjectives", "DriftZoneObjectives", "TrailBlazerObjectives" }) Guids(field, 2, "PRStunt");
                string dailyId = Ref(local, "ForzathonDailySchedule"), weeklyId = Ref(local, "ForzathonWeeklySchedule");
                var daily = dailies[Hash(dailyId)];
                if (Enumerable.Range(1, 7).Any(day => !Guid.TryParse(Value(Prop(daily, "Day" + day), "Key"), out _))) throw new InvalidDataException("Unknown daily challenge schedule.");
                Add(4, Hash(dailyId), 7, "ForzathonDaily", 7);
                // The existing serializer stores four chapter progress values
                // for one weekly award, not four independent point awards.
                _ = weeklies[Hash(weeklyId)]; Add(5, Hash(weeklyId), 4, "ForzathonWeekly");
                Guids("PhotoChallenges", 9, "PhotoChallenge");
                foreach (string id in Values(local, "TreasureChests")) Add(10, ulong.Parse(id, CultureInfo.InvariantCulture), 1, "TreasureChest");
                Guids("HorizonOpenChallenges", 12, "HorizonOpen");
                Guids("EliminatorObjectives", 13, "Eliminator"); Guids("Collectibles", 14, "Collectibles");
                switch (Value(local, "StuntPartyTheme"))
                {
                    case "Any": Add(15, 6, 1, "StuntParty"); break;
                    case "Count": break;
                    default: throw new InvalidDataException("New Stunt Party theme needs a verified save mapping.");
                }
                Guids("HideSeekObjectives", 17, "HideSeek"); Guids("Jobs", 18, "SeasonalJob");
                foreach (string field in new[] { "TimeAttackObjectives", "DriftAttackObjectives", "DragMeetObjectives" }) Guids(field, 22, "HorizonLifeEvent");
                foreach (string id in events.Where(n => Value(n, "EventType") == "MonthlyRival").Select(n => Value(n, "EventId"))) monthly.Add(Hash(Guid.Parse(id).ToString("D")));
                seasons.Add(new(index, activities.ToArray()));
            }
            if (seasons.Count != 4 || seasons.Select(s => s.Index).Distinct().Count() != 4 || monthly.Count != 1)
                throw new InvalidDataException("Incomplete or ambiguous Playlist schedule.");
            // Monthly points contribute to every season. Older saves may store
            // this shared result only beneath the season first visited.
            seasons = seasons.Select(s => s with { Activities = s.Activities.Concat(monthly.Select(id => new Activity(6, id, 1, Points("MonthlyRival")))).ToArray() }).ToList();
            result.Add(new(group.Key, name, seasons.OrderBy(s => s.Index).ToArray()));
        }
        if (result.Count is < 1 or > 4096) throw new InvalidDataException("Invalid Playlist definition coverage.");
        return result.OrderBy(s => s.Id).ToArray();
    }
}
#endif
