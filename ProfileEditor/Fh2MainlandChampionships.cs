using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Data.Sqlite;
using FH6LocalCryptoTool.GarageViewer;

namespace FH6LocalCryptoTool;

/// <summary>Native mainland Anthem IDs, not the unrelated legacy Events/Races tables.</summary>
internal sealed class Fh2ChampionshipDefinitions
{
    internal sealed record Race(long EventId, long RaceId);
    internal sealed record Championship(long Id, int Destination, Race[] Races, bool Finale = false);
    internal static readonly Lazy<Fh2ChampionshipDefinitions> Reference = new(Load);
    internal static readonly (int Id, string Name)[] Destinations = {
        (3, "Sisteron"), (4, "Nice Masséna"), (5, "Saint-Martin"),
        (6, "Castelletto"), (7, "Montellino"), (8, "San Giovanni"),
    };
    internal static readonly string[] Tables = {
        "Career_RaceResults", "Career_EventResults", "Career_ChampionshipsCompleted",
        "Career_ChampionshipsWon", "Career_ChampionshipHistory", "Career_ChampionshipStandings",
        "Career_ChampionshipEventsCompleted",
    };
    internal static readonly string[] IslandTables = {
        "IslandCareer_ChampionshipsCompleted", "IslandCareer_ChampionshipsWon", "IslandCareer_ChampionshipHistory",
        "IslandCareer_ChampionshipStandings", "IslandCareer_ChampionshipEventsCompleted",
    };
    internal static readonly string[] MainlandStoryTables = { "Career_DestinationUnlock", "Career_EventUnlock", "Career_Playthrough" };
    internal static readonly string[] ExtraUnlockTables = { "PerkUnlock", "PhotoCaptures" };
    internal required Championship[] Championships { get; init; }
    internal required Championship[] IslandChampionships { get; init; }
    internal required Race MainlandFinale { get; init; }
    internal required Dictionary<string, string[]> Schemas { get; init; }
    internal required Dictionary<long, string> Perks { get; init; }
    internal required HashSet<long> PhotoCars { get; init; }
    internal static string Shape(IReadOnlyDictionary<string, object> row) => string.Join("|",
        new[] { "name", "type", "notnull", "pk", "dflt_value" }.Select(k => Convert.ToString(row[k], CultureInfo.InvariantCulture)));
    static Fh2ChampionshipDefinitions Load()
    {
        Directory.CreateDirectory(TempWorkspace.Root);
        string path = Path.Combine(TempWorkspace.Root, "fh2-career-reference-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("FH6LocalCryptoTool.gamedbRC.stock.fh2-2067577858.sqlite.gz")
                ?? throw new InvalidDataException("The embedded FH2 reference is missing."))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new FileStream(path, FileMode.CreateNew)) gzip.CopyTo(output);
            using var db = GarageCarCatalog.Open(path, SqliteOpenMode.ReadOnly);
            if (GameDbIdentity.Detect(db) != ForzaGame.Horizon2 || StockDatabaseCatalog.Version(path) != "-2067577858")
                throw new InvalidDataException("The mainland reference is not the inspected FH2 build.");
            List<Dictionary<string, object>> Read(string sql)
            {
                using var cmd = db.CreateCommand(); cmd.CommandText = sql; using var reader = cmd.ExecuteReader();
                var rows = new List<Dictionary<string, object>>();
                while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, reader.GetValue));
                return rows;
            }
            var rows = Read("SELECT c.Id,c.RoadTripLocationId,e.AnthemEventId,r.AnthemEventRaceId FROM Campaigns c " +
                "JOIN AnthemEvents e ON e.CampaignId=c.Id JOIN AnthemEventRaces r ON r.AnthemEventId=e.AnthemEventId " +
                "WHERE c.CareerLocationId=0 AND e.EventType=0 ORDER BY c.Id,e.AnthemEventId,r.AnthemEventRaceId");
            var championships = rows.GroupBy(r => Convert.ToInt64(r["Id"])).Select(g => new Championship(g.Key,
                checked((int)Convert.ToInt64(g.First()["RoadTripLocationId"])),
                g.Select(r => new Race(Convert.ToInt64(r["AnthemEventId"]), Convert.ToInt64(r["AnthemEventRaceId"]))).ToArray())).ToArray();
            if (championships.Length != 168 || Destinations.Any(d => championships.Count(c => c.Destination == d.Id) != 28) ||
                championships.Any(c => c.Races.Length != 4) || rows.Select(r => r["AnthemEventId"]).Distinct().Count() != 672 ||
                rows.Select(r => r["AnthemEventRaceId"]).Distinct().Count() != 672)
                throw new InvalidDataException("Unexpected FH2 mainland championship definitions.");
            var islandRows = Read("SELECT c.Id,c.RoadTripLocationId,i.TileId,e.AnthemEventId,r.AnthemEventRaceId FROM Campaigns c " +
                "JOIN IslandCampaigns i ON i.CampaignId=c.Id JOIN AnthemEvents e ON e.CampaignId=c.Id JOIN AnthemEventRaces r ON r.AnthemEventId=e.AnthemEventId " +
                "WHERE c.CareerLocationId=1 AND e.EventType=0 ORDER BY c.Id,e.AnthemEventId,r.AnthemEventRaceId");
            var island = islandRows.GroupBy(r => Convert.ToInt64(r["Id"])).Select(g => new Championship(g.Key, 9,
                g.Select(r => new Race(Convert.ToInt64(r["AnthemEventId"]), Convert.ToInt64(r["AnthemEventRaceId"]))).ToArray(), Convert.ToInt64(g.First()["TileId"]) < 0)).ToArray();
            if (island.Length != 30 || island.Count(c => c.Finale) != 1 || island.Sum(c => c.Races.Length) != 89 ||
                island.Any(c => c.Races.Length is < 1 or > 5) || islandRows.Select(r => r["AnthemEventId"]).Distinct().Count() != 89 ||
                islandRows.Select(r => r["AnthemEventRaceId"]).Distinct().Count() != 89 ||
                island.SelectMany(c => c.Races).Any(r => championships.SelectMany(c => c.Races).Any(b => b.RaceId == r.RaceId || b.EventId == r.EventId)))
                throw new InvalidDataException("Unexpected FH2 Storm Island championship definitions.");
            var finale = Read("SELECT e.AnthemEventId,r.AnthemEventRaceId FROM AnthemEvents e JOIN AnthemEventRaces r ON r.AnthemEventId=e.AnthemEventId WHERE e.EventType=3 AND r.RaceModeId=1");
            if (finale.Count != 1 || Convert.ToInt64(finale[0]["AnthemEventId"]) != 201 || Convert.ToInt64(finale[0]["AnthemEventRaceId"]) != 201)
                throw new InvalidDataException("Unexpected FH2 mainland finale mapping.");
            var perks = Read("SELECT Id,Name FROM Perks ORDER BY Id").ToDictionary(r => Convert.ToInt64(r["Id"]), r => (string)r["Name"]);
            if (!perks.Keys.SequenceEqual(Enumerable.Range(0, 25).Select(i => (long)i)) || perks.Values.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Unexpected FH2 perk definitions.");
            // IsDrivable is 1 even on the PG bus/truck and null placeholder.
            // The full photographed-car reference contains the other 351 models.
            var photoCars = Read("SELECT Id FROM Data_Car WHERE MediaName NOT IN ('PG_BUS_14','PG_BoxTruck_14','NUL_Car_00')").Select(r => Convert.ToInt64(r["Id"])).ToHashSet();
            if (photoCars.Count != 351) throw new InvalidDataException("Unexpected FH2 photographed-car definitions.");
            return new() { Championships = championships, IslandChampionships = island, MainlandFinale = new(201, 201), Perks = perks,
                PhotoCars = photoCars,
                Schemas = Tables.Concat(IslandTables).Concat(MainlandStoryTables).Concat(ExtraUnlockTables).ToDictionary(t => t,
                t => Read("PRAGMA table_info(" + GarageCarCatalog.Q("NewProfile_" + t) + ")").Select(r => Shape(r)).ToArray()) };
        }
        finally { GarageCarCatalog.Cleanup(path); }
    }
}

public sealed partial class Fh2SaveRevision
{
    public sealed record MainlandChampionshipProgress(int DestinationId, string Name, int Won, int Completed, int Total, int RacesWon, int TotalRaces);
    public sealed record MainlandChampionshipEdit(int Championships, int Races)
    {
        public bool CareerRepaired { get; init; }
        public int FinaleCompletions { get; init; }
    }
    static readonly int[] ChampionshipPoints = { 20, 16, 14, 12, 10, 8, 6, 5, 4, 3, 2, 1 };
    static long Integer(IReadOnlyDictionary<string, object> row, string name)
    {
        if (row[name] is not long value) throw new InvalidDataException("Unsupported FH2 integer: " + name);
        return value;
    }
    string CareerValue(string name) => XmlProperty("CareerState", name).Attribute("Value")!.Value;
    long CareerInteger(string name) => long.Parse(CareerValue(name), NumberStyles.Integer, CultureInfo.InvariantCulture);
    static bool FirstPlace(IReadOnlyDictionary<string, object> row) => Integer(row, "Place") == 1 && Integer(row, "TargetAchieved") == 100;
    // Supplied progressed saves also contain paired first-place championship
    // results with TargetAchieved=0. Preserve these wins, rather than inventing
    // another race finish just to normalize their target field to 100.
    static bool MainlandRaceWon(IReadOnlyDictionary<string, object> row) => Integer(row, "Place") == 1 && Integer(row, "TargetAchieved") is 0 or 100;
    List<Dictionary<string, object>> ChampionshipRoster(bool island = false)
    {
        // Reuse native participant identities, never an invented account/XUID.
        var roster = Rows("SELECT * FROM " + Q(island ? "IslandCareer_ChampionshipStandings" : "Career_ChampionshipStandings") + " ORDER BY Points DESC,rowid");
        if (roster.Count != 12 || roster.Count(r => Integer(r, "PlayerType") == 1) != 1 || roster.Count(r => Integer(r, "PlayerType") == 1 && Integer(r, "OfflineDrivatar") == 0 && Integer(r, "Friend") == 0) != 1 ||
            roster.Any(r => r["PlayerXUID"] is not string x || x.Length == 0) || roster.Select(r => r["PlayerXUID"]).Distinct().Count() != 12 ||
            roster.Any(r => Integer(r, "Points") < 0 || Integer(r, "Points") > (island ? 100 : 80) || Integer(r, "Friend") != 0 ||
                (Integer(r, "PlayerType") != 1 && (Integer(r, "PlayerType") != 0 || Integer(r, "OfflineDrivatar") != 1))))
            throw new InvalidDataException("A recognized offline 12-driver championship roster is required; online/unknown rosters are not rewritten.");
        return roster.OrderByDescending(r => Integer(r, "PlayerType") == 1).ToList();
    }
    void CheckChampionshipLayout(Fh2ChampionshipDefinitions reference, bool includeStory = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (string table in Fh2ChampionshipDefinitions.Tables.Concat(includeStory ? Fh2ChampionshipDefinitions.MainlandStoryTables : Array.Empty<string>()))
        {
            var expected = reference.Schemas[table];
            if (expected.Length == 0 || !Rows("PRAGMA table_info(" + Q(table) + ")").Select(r => Fh2ChampionshipDefinitions.Shape(r)).SequenceEqual(expected))
                throw new InvalidDataException("Unsupported championship schema: " + table);
        }
        if (Number("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger'") != 0)
            throw new InvalidDataException("Unknown database triggers; championship editing is disabled.");
        var touched = Fh2ChampionshipDefinitions.Tables.Concat(includeStory ? Fh2ChampionshipDefinitions.MainlandStoryTables : Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in Rows("SELECT name FROM sqlite_master WHERE type='table'").Select(r => (string)r["name"]))
        {
            if (Rows("PRAGMA foreign_key_list(" + Q(table) + ")").Any(r => touched.Contains((string)r["table"])))
                throw new InvalidDataException("Unsupported championship foreign-key references: " + table);
            if (!touched.Contains(table) && table.StartsWith("Career_", StringComparison.OrdinalIgnoreCase) && Columns(table).Contains("CampaignId"))
                throw new InvalidDataException("Unrecognized championship-linked table: " + table);
        }
        var known = reference.Championships.ToDictionary(c => c.Id);
        var completed = Rows("SELECT * FROM Career_ChampionshipsCompleted");
        if (completed.Any(r => !known.ContainsKey(Integer(r, "CampaignId")) || Integer(r, "Won") is < 0 or > 1 || Integer(r, "BestFinishPosition") is < 0 or > 11 ||
            (Integer(r, "Won") == 1 && Integer(r, "BestFinishPosition") != 0)))
            throw new InvalidDataException("Unsupported mainland completion records.");
        var won = Rows("SELECT * FROM Career_ChampionshipsWon").Select(r => Integer(r, "CampaignId")).ToHashSet();
        if (!won.SetEquals(completed.Where(r => Integer(r, "Won") == 1).Select(r => Integer(r, "CampaignId"))))
            throw new InvalidDataException("Mainland championship win/completion records disagree.");
        var roster = ChampionshipRoster(); string player = (string)roster.Single(r => Integer(r, "PlayerType") == 1)["PlayerXUID"];
        long played = CareerInteger("ChampionshipsPlayedCount");
        if (played is < 1 or > 100000 || (!includeStory && (CareerValue("PendingFinalEvent") != "false" || CareerValue("PendingFinalSlamDown") != "false")) ||
            XmlProperty("IESaveState", "IEState").Attribute("Value")!.Value != "Complete")
            throw new InvalidDataException("Finish the introduction and any pending mainland finale before editing championship records.");
        var history = Rows("SELECT * FROM Career_ChampionshipHistory");
        var groups = history.GroupBy(r => Integer(r, "OrderIndex")).OrderBy(g => g.Key).ToArray();
        // Native post-story saves retain 0, 12 or 24 rows even after hundreds
        // of championships. History is not a required lifetime win ledger.
        if (groups.Length > played || groups.Where((g, i) => g.Key != i).Any())
            throw new InvalidDataException("Championship history/order counters do not match.");
        foreach (var group in groups)
        {
            if (group.Count() != 12 || group.Select(r => Integer(r, "CampaignId")).Distinct().Count() != 1 ||
                !completed.Any(r => Integer(r, "CampaignId") == Integer(group.First(), "CampaignId")) ||
                !group.Select(r => Integer(r, "Position")).Order().SequenceEqual(Enumerable.Range(0, 12).Select(i => (long)i)) ||
                group.Select(r => r["PlayerXUID"]).Distinct().Count() != 12 ||
                group.Count(r => Integer(r, "PlayerType") == 1) != 1 || group.Count(r => Integer(r, "PlayerType") == 1 && Equals(r["PlayerXUID"], player) && Integer(r, "OfflineDrivatar") == 0 && Integer(r, "Friend") == 0) != 1 ||
                group.Any(r => r["PlayerXUID"] is not string x || x.Length == 0 || Integer(r, "Friend") != 0 ||
                    (Integer(r, "PlayerType") != 1 && (Integer(r, "PlayerType") != 0 || Integer(r, "OfflineDrivatar") != 1))))
                throw new InvalidDataException("Unsupported offline championship history.");
        }
        foreach (var (table, key) in new[] { ("Career_RaceResults", "RaceId"), ("Career_EventResults", "EventId") })
        {
            var ids = reference.Championships.SelectMany(c => c.Races).Select(r => key == "RaceId" ? r.RaceId : r.EventId).ToHashSet();
            if (Rows("SELECT * FROM " + Q(table)).Where(r => ids.Contains(Integer(r, key))).Any(r => Integer(r, "Place") is < 1 or > 12 ||
                Integer(r, "TargetAchieved") is not (0 or 100) || Integer(r, "TimesCompleted") is < 1 or >= int.MaxValue))
                throw new InvalidDataException("Unsupported mainland race result values.");
        }
        long current = CareerInteger("CurrentCampaign"); var active = Rows("SELECT * FROM Career_ChampionshipEventsCompleted");
        if (active.Count > 0 && (!known.TryGetValue(current, out var campaign) || active.Any(r => !campaign.Races.Any(a => a.EventId == Integer(r, "EventId")))))
            throw new InvalidDataException("Active championship events do not match the native mainland campaign.");
        foreach (string path in ChampionshipCounterPaths) _ = ReadUInt(path);
        if (ReadUInt("/CompareStats/CarChampionshipsWon") < won.Count || ReadUInt("/MiscStats/CarChampionshipsCompleted") < played)
            throw new InvalidDataException("Championship summary counters are below their native records.");
    }
    static readonly string[] ChampionshipCounterPaths = {
        "/CompareStats/CarChampionshipsWon", "/MiscStats/CarChampionshipsCompleted", "/CompareStats/CarChampionshipEventsWon",
        "/MiscStats/CarChampionshipEventsRaced", "/MiscStats/CarChampionshipEventPodiums",
        "/Main/NumRaces", "/Main/NumVictories", "/Main/NumPodiums", "/MiscStats/NumRacesSinglePlayer",
    };
    public string ChampionshipRestriction
    {
        get { try { CheckChampionshipLayout(Fh2ChampionshipDefinitions.Reference.Value, true); return ""; } catch (Exception ex) { return "Mainland championship editing disabled: " + ex.Message; } }
    }
    public IReadOnlyList<MainlandChampionshipProgress> MainlandChampionships()
    {
        var reference = Fh2ChampionshipDefinitions.Reference.Value;
        var completed = Rows("SELECT * FROM Career_ChampionshipsCompleted").ToDictionary(r => Integer(r, "CampaignId"));
        var races = Rows("SELECT * FROM Career_RaceResults").ToDictionary(r => Integer(r, "RaceId"));
        return Fh2ChampionshipDefinitions.Destinations.Select(d => {
            var entries = reference.Championships.Where(c => c.Destination == d.Id).ToArray();
            return new MainlandChampionshipProgress(d.Id, d.Name, entries.Count(c => completed.TryGetValue(c.Id, out var r) && Integer(r, "Won") == 1),
                entries.Count(c => completed.ContainsKey(c.Id)), entries.Length,
                entries.SelectMany(c => c.Races).Count(r => races.TryGetValue(r.RaceId, out var row) && MainlandRaceWon(row)), entries.Sum(c => c.Races.Length));
        }).ToArray();
    }
    public MainlandChampionshipEdit CompleteMainlandChampionships(int destinationId = 0)
    {
        if (destinationId != 0 && !Fh2ChampionshipDefinitions.Destinations.Any(d => d.Id == destinationId))
            throw new ArgumentOutOfRangeException(nameof(destinationId), "Choose all mainland championships or a mainland destination; Storm Island is separate.");
        bool includeStory = destinationId == 0;
        var reference = Fh2ChampionshipDefinitions.Reference.Value; CheckChampionshipLayout(reference, includeStory);
        var selected = reference.Championships.Where(c => destinationId == 0 || c.Destination == destinationId).ToArray();
        var wins = Rows("SELECT * FROM Career_ChampionshipsWon").Select(r => Integer(r, "CampaignId")).ToHashSet();
        var newWins = selected.Where(c => !wins.Contains(c.Id)).ToArray();
        var raceResults = Rows("SELECT * FROM Career_RaceResults").ToDictionary(r => Integer(r, "RaceId"));
        var eventResults = Rows("SELECT * FROM Career_EventResults").ToDictionary(r => Integer(r, "EventId"));
        var raceChanges = selected.SelectMany(c => c.Races).Where(r => !raceResults.TryGetValue(r.RaceId, out var row) || !MainlandRaceWon(row)).ToArray();
        var eventChanges = selected.SelectMany(c => c.Races).Where(r => !eventResults.TryGetValue(r.EventId, out var row) || !MainlandRaceWon(row)).ToArray();
        if (raceChanges.Length != eventChanges.Length || !raceChanges.Select(r => r.EventId).ToHashSet().SetEquals(eventChanges.Select(r => r.EventId)))
            throw new InvalidDataException("Race/event records disagree; nothing was changed.");
        uint Add(uint value, int count) => checked(value + (uint)count);
        var changes = ChampionshipCounterPaths.ToDictionary(p => p, p => Add(ReadUInt(p),
            p is "/CompareStats/CarChampionshipsWon" or "/MiscStats/CarChampionshipsCompleted" ? newWins.Length : raceChanges.Length));
        long played = CareerInteger("ChampionshipsPlayedCount");
        var xmlChanges = new Dictionary<(string Type, string Id, string Property), string>();
        if (newWins.Length > 0) xmlChanges[("CareerState", "CareerState", "ChampionshipsPlayedCount")] = checked(played + newWins.Length).ToString(CultureInfo.InvariantCulture);
        var story = includeStory ? PlanMainlandPostStory(reference, changes, xmlChanges, checked(played + newWins.Length)) : null;
        var staged = StageProperties(changes);
        string xml = xmlChanges.Count == 0 ? _xml : RewriteXml(xmlChanges);
        var roster = ChampionshipRoster(); long current = CareerInteger("CurrentCampaign");
        var active = selected.SingleOrDefault(c => c.Id == current);
        bool activeRepair = active != null &&
            (!Rows("SELECT * FROM Career_ChampionshipEventsCompleted").Select(r => Integer(r, "EventId")).ToHashSet().SetEquals(active.Races.Select(r => r.EventId)) ||
             roster.Where((r, i) => Integer(r, "Points") != ChampionshipPoints[i] * 4).Any());
        bool storyChanged = story?.Changed == true;
        if (newWins.Length == 0 && raceChanges.Length == 0 && eventChanges.Length == 0 && !activeRepair && !storyChanged) return new(0, 0);
        long historyOrder = Number("SELECT COUNT(DISTINCT OrderIndex) FROM Career_ChampionshipHistory");
        var oldProperties = _properties; string oldXml = _xml;
        using var tx = _db.BeginTransaction();
        try
        {
            foreach (var race in raceChanges)
            {
                Execute("INSERT INTO Career_RaceResults (RaceId,Place,TargetAchieved,TimesCompleted) VALUES ($p0,1,100,1) " +
                    "ON CONFLICT(RaceId) DO UPDATE SET Place=1,TargetAchieved=100,TimesCompleted=TimesCompleted+1", race.RaceId);
                Execute("INSERT INTO Career_EventResults (EventId,Place,TargetAchieved,TimesCompleted) VALUES ($p0,1,100,1) " +
                    "ON CONFLICT(EventId) DO UPDATE SET Place=1,TargetAchieved=100,TimesCompleted=TimesCompleted+1", race.EventId);
            }
            foreach (var championship in newWins)
            {
                Execute("INSERT INTO Career_ChampionshipsCompleted (CampaignId,Won,BestFinishPosition) VALUES ($p0,1,0) " +
                    "ON CONFLICT(CampaignId) DO UPDATE SET Won=1,BestFinishPosition=0", championship.Id);
                Execute("INSERT INTO Career_ChampionshipsWon (CampaignId) VALUES ($p0)", championship.Id);
                for (int i = 0; i < roster.Count; i++)
                {
                    var driver = roster[i];
                    Execute("INSERT INTO Career_ChampionshipHistory (CampaignId,OrderIndex,PlayerXUID,Position,PlayerType,OfflineDrivatar,Friend) " +
                        "VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6)", championship.Id, historyOrder, driver["PlayerXUID"], i, driver["PlayerType"], driver["OfflineDrivatar"], driver["Friend"]);
                }
                historyOrder++;
            }
            if (activeRepair && active != null)
            {
                foreach (var race in active.Races) Execute("INSERT INTO Career_ChampionshipEventsCompleted (EventId) VALUES ($p0) ON CONFLICT(EventId) DO NOTHING", race.EventId);
                for (int i = 0; i < roster.Count; i++) Execute("UPDATE Career_ChampionshipStandings SET Points=$p0 WHERE PlayerXUID=$p1", ChampionshipPoints[i] * 4, roster[i]["PlayerXUID"]);
            }
            if (story != null) ApplyMainlandPostStory(story, reference);
            _properties = staged; _xml = xml;
            CheckDatabase(); CheckChampionshipLayout(reference, includeStory);
            tx.Commit(); _databaseChanged = true; _xmlChanged |= xml != oldXml;
        }
        catch { _properties = oldProperties; _xml = oldXml; tx.Rollback(); throw; }
        return new(newWins.Length, raceChanges.Length) { CareerRepaired = storyChanged || activeRepair, FinaleCompletions = story?.FinaleRuns ?? 0 };
    }
}
