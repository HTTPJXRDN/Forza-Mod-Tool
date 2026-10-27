using System.Globalization;
using System.IO;

namespace FH6LocalCryptoTool;

public sealed partial class Fh2SaveRevision
{
    sealed record MainlandStoryPlan(bool Changed, long[] Destinations, long[] Events, int FinaleRuns, long FinaleTimes);

    public bool MainlandPostStoryComplete
    {
        get
        {
            try { return CareerValue("DestinationSelectMode") == "UserSelected" && CareerInteger("RoadTripPlaythroughId") == 5 &&
                CareerInteger("NumScriptedPlaythroughsCompleted") == 4 && CareerValue("RoadTripCreatorUnlocked") == "true" &&
                CareerValue("WonFirstFinal") == "true" && CareerValue("WonSecondFinal") == "true" &&
                CareerValue("PendingFinalEvent") == "false" && CareerValue("PendingFinalSlamDown") == "false"; }
            catch { return false; }
        }
    }

    MainlandStoryPlan PlanMainlandPostStory(Fh2ChampionshipDefinitions reference, Dictionary<string, uint> properties,
        Dictionary<(string Type, string Id, string Property), string> xml, long played)
    {
        // Four separately supplied progressed profiles agree on these native
        // enums/counters. No donor account, roster, car or whole XML is copied.
        string mode = CareerValue("DestinationSelectMode");
        if (mode is not ("Scripted" or "UserSelected") || CareerInteger("RoadTripPlaythroughId") is < 1 or > 5 ||
            CareerInteger("NumScriptedPlaythroughsCompleted") is < 0 or > 4 || CareerInteger("NumLocationsVisitedThisRoadTrip") is < 0 or > 6 ||
            CareerValue("ActiveCareerLocation") is not ("Mainland" or "Island"))
            throw new InvalidDataException("Unknown mainland road-trip state; post-story repair is disabled.");
        foreach (string name in new[] { "RoadTripCreatorUnlocked", "RoadTripStarted", "PendingFinalEvent", "PendingFinalSlamDown", "WonFirstFinal", "WonSecondFinal" })
            if (CareerValue(name) is not ("true" or "false")) throw new InvalidDataException("Unknown career boolean: " + name);
        long location = CareerInteger("RoadTripLocation"), next = CareerInteger("UserSelectedNextLocation"), blocking = CareerInteger("BlockingEventId");
        bool KnownLocation(long id) => id == 0 || Fh2ChampionshipDefinitions.Destinations.Any(d => d.Id == id);
        if (!KnownLocation(location) || !KnownLocation(next) || (blocking != -1 && blocking != reference.MainlandFinale.EventId))
            throw new InvalidDataException("Finish the blocking non-finale event or use a recognized road-trip location before post-story repair.");
        var playthroughs = Rows("SELECT * FROM Career_Playthrough ORDER BY PlaythroughOrder");
        if (playthroughs.Count != 5 || playthroughs.Where((row, i) => Integer(row, "Id") != i + 1 || Integer(row, "PlaythroughOrder") != i).Any())
            throw new InvalidDataException("Unsupported native mainland playthrough list.");
        var unlocked = Rows("SELECT * FROM Career_DestinationUnlock").Select(r => Integer(r, "UnlockId")).ToHashSet();
        if (unlocked.Any(id => !Fh2ChampionshipDefinitions.Destinations.Any(d => d.Id == id)))
            throw new InvalidDataException("Unknown mainland destination unlocks.");
        long[] destinations = Fh2ChampionshipDefinitions.Destinations.Select(d => (long)d.Id).Except(unlocked).ToArray();
        var eventUnlocks = Rows("SELECT * FROM Career_EventUnlock").Select(r => Integer(r, "UnlockId")).ToHashSet();
        if (eventUnlocks.Any(id => id < 0)) throw new InvalidDataException("Unknown event-unlock values.");
        long[] events = reference.Championships.SelectMany(c => c.Races).Select(r => r.EventId).Append(reference.MainlandFinale.EventId).Except(eventUnlocks).ToArray();

        var finals = Rows("SELECT * FROM Career_EventResults WHERE EventId=$p0", reference.MainlandFinale.EventId);
        var races = Rows("SELECT * FROM Career_RaceResults WHERE RaceId=$p0", reference.MainlandFinale.RaceId);
        if (finals.Count != races.Count) throw new InvalidDataException("Mainland finale race/event records disagree.");
        long times = 0; bool won = false;
        if (finals.Count == 1)
        {
            foreach (var row in finals.Concat(races))
                if (Integer(row, "Place") is < 1 or > 12 || Integer(row, "TargetAchieved") != 100 || Integer(row, "TimesCompleted") is < 1 or >= int.MaxValue)
                    throw new InvalidDataException("Unsupported mainland finale result.");
            if (Integer(finals[0], "Place") != Integer(races[0], "Place") || Integer(finals[0], "TimesCompleted") != Integer(races[0], "TimesCompleted"))
                throw new InvalidDataException("Mainland finale race/event values disagree.");
            times = Integer(finals[0], "TimesCompleted"); won = FirstPlace(finals[0]);
        }
        int missingFinals = (CareerValue("WonFirstFinal") == "true" ? 0 : 1) + (CareerValue("WonSecondFinal") == "true" ? 0 : 1);
        int runs = Math.Max(missingFinals, checked((int)Math.Max(0, 2 - times)));
        if (!won && runs == 0) runs = 1;
        long desiredTimes = checked(times + runs);
        if (desiredTimes >= int.MaxValue) throw new InvalidDataException("Finale result count would overflow.");
        foreach (string path in new[] { "/Main/NumRaces", "/Main/NumVictories", "/Main/NumPodiums", "/MiscStats/NumRacesSinglePlayer" })
            properties[path] = checked(properties[path] + (uint)runs);
        properties["/CompareStats/HorizonFinalProgress"] = 100;
        // Native second-final progress can legitimately exceed 100 (the DJS
        // reference has 1021). Establish completion, never cap existing totals.
        properties["/CompareStats/HorizonFinalProgress2"] = Math.Max(ReadUInt("/CompareStats/HorizonFinalProgress2"), 100u);
        properties["/CompareStats/DestinationsVisited"] = Math.Max(ReadUInt("/CompareStats/DestinationsVisited"), 6u);
        properties["/CompareStats/RoadTripEventsCompleted"] = Math.Max(ReadUInt("/CompareStats/RoadTripEventsCompleted"), checked((uint)played));
        void Set(string name, string value) => xml[("CareerState", "CareerState", name)] = value;
        Set("RoadTripPlaythroughId", "5"); Set("NumScriptedPlaythroughsCompleted", "4");
        Set("DestinationSelectMode", "UserSelected"); Set("RoadTripCreatorUnlocked", "true");
        Set("WonFirstFinal", "true"); Set("WonSecondFinal", "true"); Set("PendingFinalEvent", "false"); Set("PendingFinalSlamDown", "false");
        if (blocking == reference.MainlandFinale.EventId) Set("BlockingEventId", "-1");
        if (mode == "Scripted") Set("UserSelectedNextLocation", location.ToString(CultureInfo.InvariantCulture));
        foreach (string feature in new[] { "IntroducedSisteron", "IntroducedNice", "IntroducedSaintMartin", "IntroducedCastelletto", "IntroducedMontellino", "IntroducedSanGimignano" })
        {
            var field = XmlProperty("IESaveState", feature);
            if (field.Attribute("Value")!.Value is not ("true" or "false")) throw new InvalidDataException("Unknown destination-introduction state.");
            xml[("IESaveState", "iestate", feature)] = "true";
        }
        bool changed = runs > 0 || destinations.Length > 0 || events.Length > 0 || properties.Any(p => ReadUInt(p.Key) != p.Value) ||
            xml.Any(p => XmlProperty(p.Key.Type, p.Key.Property).Attribute("Value")!.Value != p.Value);
        return new(changed, destinations, events, runs, desiredTimes);
    }

    void ApplyMainlandPostStory(MainlandStoryPlan plan, Fh2ChampionshipDefinitions reference)
    {
        foreach (long id in plan.Destinations) Execute("INSERT INTO Career_DestinationUnlock (UnlockId) VALUES ($p0)", id);
        foreach (long id in plan.Events) Execute("INSERT INTO Career_EventUnlock (UnlockId) VALUES ($p0)", id);
        if (plan.FinaleRuns == 0) return;
        Execute("INSERT INTO Career_RaceResults (RaceId,Place,TargetAchieved,TimesCompleted) VALUES ($p0,1,100,$p1) ON CONFLICT(RaceId) DO UPDATE SET Place=1,TargetAchieved=100,TimesCompleted=$p1",
            reference.MainlandFinale.RaceId, plan.FinaleTimes);
        Execute("INSERT INTO Career_EventResults (EventId,Place,TargetAchieved,TimesCompleted) VALUES ($p0,1,100,$p1) ON CONFLICT(EventId) DO UPDATE SET Place=1,TargetAchieved=100,TimesCompleted=$p1",
            reference.MainlandFinale.EventId, plan.FinaleTimes);
    }
}
