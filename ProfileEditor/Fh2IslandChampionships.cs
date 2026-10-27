using System.Globalization;
using System.IO;

namespace FH6LocalCryptoTool;

public sealed partial class Fh2SaveRevision
{
    public sealed record IslandChampionshipProgress(int Won, int Completed, int Total, int RacesWon, int TotalRaces);
    public sealed record IslandChampionshipEdit(int Championships, int Races);
    static readonly string[] IslandChampionshipCounterPaths = {
        "/Island/CarChampionshipsWon", "/Island/CarChampionshipsCompleted", "/Island/CarChampionshipEventsWon",
        "/Island/CarChampionshipEventsRaced", "/Island/CarChampionshipEventPodiums",
        "/Main/NumRaces", "/Main/NumVictories", "/Main/NumPodiums", "/MiscStats/NumRacesSinglePlayer",
    };
    void CheckIslandChampionshipLayout(Fh2ChampionshipDefinitions reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var touched = Fh2ChampionshipDefinitions.IslandTables.Concat(new[] { "Career_RaceResults", "Career_EventResults" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string table in touched)
            if (!Rows("PRAGMA table_info(" + Q(table) + ")").Select(r => Fh2ChampionshipDefinitions.Shape(r)).SequenceEqual(reference.Schemas[table]))
                throw new InvalidDataException("Unsupported island championship schema: " + table);
        if (Number("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger'") != 0)
            throw new InvalidDataException("Unrecognized database triggers.");
        foreach (string table in Rows("SELECT name FROM sqlite_master WHERE type='table'").Select(r => (string)r["name"]))
        {
            if (Rows("PRAGMA foreign_key_list(" + Q(table) + ")").Any(r => touched.Contains((string)r["table"])))
                throw new InvalidDataException("Unsupported island championship foreign-key references.");
            if (!touched.Contains(table) && table.StartsWith("IslandCareer_", StringComparison.OrdinalIgnoreCase) && Columns(table).Contains("CampaignId"))
                throw new InvalidDataException("Unrecognized island championship-linked table: " + table);
        }
        if (XmlProperty("IslandIESaveState", "IEState").Attribute("Value")!.Value != "Complete")
            throw new InvalidDataException("Finish Storm Island's introduction before bulk completion. The editor does not bypass introduction/reward-car flags.");
        if (CareerValue("PendingIslandFinalEvent") != "false" || CareerValue("PendingIslandFinalSlamDown") != "false")
            throw new InvalidDataException("Finish the pending island finale first; its story/reward flags are not edited.");
        if (!Fh2RoadDiscoveryState.TryRead(_editedBinary ?? _original["BinarySaveState"], out _, true))
            throw new InvalidDataException("Visit Storm Island and load its recognized map record first.");
        // Both captured island finishes leave this table empty, unlike mainland.
        if (Number("SELECT COUNT(*) FROM IslandCareer_ChampionshipHistory") != 0)
            throw new InvalidDataException("This island history layout has not been mapped; it will not be rewritten.");
        _ = ChampionshipRoster(true);
        var known = reference.IslandChampionships.ToDictionary(c => c.Id);
        var completed = Rows("SELECT * FROM IslandCareer_ChampionshipsCompleted");
        if (completed.Any(r => !known.ContainsKey(Integer(r, "CampaignId")) || Integer(r, "Won") is < 0 or > 1 ||
            Integer(r, "BestFinishPosition") is < 0 or > 11 || (Integer(r, "Won") == 1 && Integer(r, "BestFinishPosition") != 0)))
            throw new InvalidDataException("Unsupported island completion values.");
        var wins = Rows("SELECT * FROM IslandCareer_ChampionshipsWon").Select(r => Integer(r, "CampaignId")).ToHashSet();
        if (!wins.SetEquals(completed.Where(r => Integer(r, "Won") == 1).Select(r => Integer(r, "CampaignId"))))
            throw new InvalidDataException("Island win/completion records disagree.");
        long played = CareerInteger("IslandChampionshipsPlayedCount");
        if (played < completed.Count || played > 100000) throw new InvalidDataException("Island played/completion counters disagree.");
        long current = CareerInteger("CurrentIslandCampaign"); var active = Rows("SELECT * FROM IslandCareer_ChampionshipEventsCompleted");
        if (active.Count > 0 && (!known.TryGetValue(current, out var campaign) || active.Any(r => !campaign.Races.Any(a => a.EventId == Integer(r, "EventId")))))
            throw new InvalidDataException("Active island events do not match the native island campaign.");
        foreach (var (table, key) in new[] { ("Career_RaceResults", "RaceId"), ("Career_EventResults", "EventId") })
        {
            var ids = reference.IslandChampionships.SelectMany(c => c.Races).Select(r => key == "RaceId" ? r.RaceId : r.EventId).ToHashSet();
            if (Rows("SELECT * FROM " + Q(table)).Where(r => ids.Contains(Integer(r, key))).Any(r => Integer(r, "Place") is < 1 or > 12 ||
                Integer(r, "TargetAchieved") != 100 || Integer(r, "TimesCompleted") is < 1 or >= int.MaxValue))
                throw new InvalidDataException("Unsupported island race result values.");
        }
        foreach (string path in IslandChampionshipCounterPaths) _ = ReadUInt(path);
        if (ReadUInt("/Island/CarChampionshipsWon") < wins.Count || ReadUInt("/Island/CarChampionshipsCompleted") < played)
            throw new InvalidDataException("Island summary counters are below their native records.");
    }
    public string IslandChampionshipRestriction
    {
        get { try { CheckIslandChampionshipLayout(Fh2ChampionshipDefinitions.Reference.Value); return ""; } catch (Exception ex) { return "Storm Island championship editing disabled: " + ex.Message; } }
    }
    public IslandChampionshipProgress IslandChampionships()
    {
        var targets = Fh2ChampionshipDefinitions.Reference.Value.IslandChampionships.Where(c => !c.Finale).ToArray();
        var completed = Rows("SELECT * FROM IslandCareer_ChampionshipsCompleted").ToDictionary(r => Integer(r, "CampaignId"));
        var races = Rows("SELECT * FROM Career_RaceResults").ToDictionary(r => Integer(r, "RaceId"));
        return new(targets.Count(c => completed.TryGetValue(c.Id, out var row) && Integer(row, "Won") == 1), targets.Count(c => completed.ContainsKey(c.Id)),
            targets.Length, targets.SelectMany(c => c.Races).Count(r => races.TryGetValue(r.RaceId, out var row) && FirstPlace(row)), targets.Sum(c => c.Races.Length));
    }
    public IslandChampionshipEdit CompleteIslandChampionships()
    {
        var reference = Fh2ChampionshipDefinitions.Reference.Value; CheckIslandChampionshipLayout(reference);
        var selected = reference.IslandChampionships.Where(c => !c.Finale).ToArray();
        var wins = Rows("SELECT * FROM IslandCareer_ChampionshipsWon").Select(r => Integer(r, "CampaignId")).ToHashSet();
        var newWins = selected.Where(c => !wins.Contains(c.Id)).ToArray();
        var races = Rows("SELECT * FROM Career_RaceResults").ToDictionary(r => Integer(r, "RaceId"));
        var events = Rows("SELECT * FROM Career_EventResults").ToDictionary(r => Integer(r, "EventId"));
        var raceChanges = selected.SelectMany(c => c.Races).Where(r => !races.TryGetValue(r.RaceId, out var row) || !FirstPlace(row)).ToArray();
        var eventChanges = selected.SelectMany(c => c.Races).Where(r => !events.TryGetValue(r.EventId, out var row) || !FirstPlace(row)).ToArray();
        if (newWins.Length == 0 && raceChanges.Length == 0 && eventChanges.Length == 0) return new(0, 0);
        if (raceChanges.Length != eventChanges.Length || !raceChanges.Select(r => r.EventId).ToHashSet().SetEquals(eventChanges.Select(r => r.EventId)))
            throw new InvalidDataException("Island race/event records disagree; nothing was changed.");
        var changes = IslandChampionshipCounterPaths.ToDictionary(p => p, p => checked(ReadUInt(p) + (uint)(p is "/Island/CarChampionshipsWon" or "/Island/CarChampionshipsCompleted" ? newWins.Length : raceChanges.Length)));
        var staged = StageProperties(changes);
        string xml = newWins.Length == 0 ? _xml : RewriteXml(new() { [("CareerState", "CareerState", "IslandChampionshipsPlayedCount")] = checked(CareerInteger("IslandChampionshipsPlayedCount") + newWins.Length).ToString(CultureInfo.InvariantCulture) });
        var roster = ChampionshipRoster(true); long current = CareerInteger("CurrentIslandCampaign");
        var oldProperties = _properties; string oldXml = _xml;
        using var tx = _db.BeginTransaction();
        try
        {
            foreach (var race in raceChanges)
            {
                Execute("INSERT INTO Career_RaceResults (RaceId,Place,TargetAchieved,TimesCompleted) VALUES ($p0,1,100,1) ON CONFLICT(RaceId) DO UPDATE SET Place=1,TargetAchieved=100,TimesCompleted=TimesCompleted+1", race.RaceId);
                Execute("INSERT INTO Career_EventResults (EventId,Place,TargetAchieved,TimesCompleted) VALUES ($p0,1,100,1) ON CONFLICT(EventId) DO UPDATE SET Place=1,TargetAchieved=100,TimesCompleted=TimesCompleted+1", race.EventId);
            }
            foreach (var campaign in newWins)
            {
                Execute("INSERT INTO IslandCareer_ChampionshipsCompleted (CampaignId,Won,BestFinishPosition) VALUES ($p0,1,0) ON CONFLICT(CampaignId) DO UPDATE SET Won=1,BestFinishPosition=0", campaign.Id);
                Execute("INSERT INTO IslandCareer_ChampionshipsWon (CampaignId) VALUES ($p0)", campaign.Id);
                if (campaign.Id == current)
                {
                    foreach (var race in campaign.Races) Execute("INSERT INTO IslandCareer_ChampionshipEventsCompleted (EventId) VALUES ($p0) ON CONFLICT(EventId) DO NOTHING", race.EventId);
                    for (int i = 0; i < roster.Count; i++) Execute("UPDATE IslandCareer_ChampionshipStandings SET Points=$p0 WHERE PlayerXUID=$p1", ChampionshipPoints[i] * campaign.Races.Length, roster[i]["PlayerXUID"]);
                }
            }
            _properties = staged; _xml = xml;
            CheckDatabase(); CheckIslandChampionshipLayout(reference);
            tx.Commit(); _databaseChanged = true; _xmlChanged |= xml != oldXml;
        }
        catch { _properties = oldProperties; _xml = oldXml; tx.Rollback(); throw; }
        return new(newWins.Length, raceChanges.Length);
    }
}
