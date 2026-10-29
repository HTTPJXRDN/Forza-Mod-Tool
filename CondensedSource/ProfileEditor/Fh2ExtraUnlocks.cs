using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace FH6LocalCryptoTool;

/// <summary>Mapped local unlocks only: no donor identity, online scores or reward claims.</summary>
public sealed partial class Fh2SaveRevision
{
    public sealed record PhotoProgress(int Photographed, int SupportedOwned, int PendingOwned, int UnknownOwned);
    public sealed record CameraProgress(int Discovered, int Total);
    static string Restriction(Action check) { try { check(); return ""; } catch (Exception ex) { return ex.Message; } }

    HashSet<long> CheckMappedUnlocks(string table, string key, string counter, HashSet<long> known, bool requireMatchingCounter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var reference = Fh2ChampionshipDefinitions.Reference.Value;
        if (!Rows("PRAGMA table_info(" + Q(table) + ")").Select(Fh2ChampionshipDefinitions.Shape).SequenceEqual(reference.Schemas[table]))
            throw new InvalidDataException("Unsupported FH2 unlock schema: " + table);
        if (Number("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger'") != 0)
            throw new InvalidDataException("Unknown save database triggers; unlock editing is disabled.");
        foreach (string other in Rows("SELECT name FROM sqlite_master WHERE type='table'").Select(r => (string)r["name"]))
            if (Rows("PRAGMA foreign_key_list(" + Q(other) + ")").Any(r => string.Equals((string)r["table"], table, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Unsupported unlock-linked table: " + other);
        if (Rows("PRAGMA foreign_key_list(" + Q(table) + ")").Count != 0)
            throw new InvalidDataException("Unsupported unlock foreign keys: " + table);
        var ids = Rows("SELECT * FROM " + Q(table)).Select(r => Integer(r, key)).ToHashSet();
        if (!ids.IsSubsetOf(known) || ReadUInt(counter) > known.Count || (requireMatchingCounter && ReadUInt(counter) != ids.Count))
            throw new InvalidDataException("Unknown or inconsistent FH2 unlock IDs/counters: " + table);
        return ids;
    }
    int ApplyMappedUnlocks(string table, string key, long[] added, string counter, uint total)
    {
        var staged = StageProperties(new Dictionary<string, uint> { [counter] = total });
        if (added.Length == 0 && ReadUInt(counter) == total) return 0;
        var previous = _properties;
        using var tx = _db.BeginTransaction();
        try
        {
            foreach (long id in added) Execute("INSERT INTO " + Q(table) + " (" + Q(key) + ") VALUES ($p0)", id);
            CheckDatabase();
            if (Number("SELECT COUNT(*) FROM " + Q(table)) != total) throw new InvalidDataException("Unlock totals failed verification.");
            _properties = staged; tx.Commit(); _databaseChanged = true;
        }
        catch { _properties = previous; tx.Rollback(); throw; }
        return added.Length;
    }

    HashSet<long> CheckPerks() => CheckMappedUnlocks("PerkUnlock", "UnlockId", "/CompareStats/PerksUnlocked",
        Fh2ChampionshipDefinitions.Reference.Value.Perks.Keys.ToHashSet(), false);
    public string PerkRestriction => Restriction(() => CheckPerks());
    public int UnlockedPerks => CheckPerks().Count;
    public int UnlockAllPerks()
    {
        var unlocked = CheckPerks(); var perks = Fh2ChampionshipDefinitions.Reference.Value.Perks;
        return ApplyMappedUnlocks("PerkUnlock", "UnlockId", perks.Keys.Except(unlocked).ToArray(), "/CompareStats/PerksUnlocked", (uint)perks.Count);
    }

    HashSet<long> CheckPhotos() => CheckMappedUnlocks("PhotoCaptures", "CarOrdinal", "/CompareStats/PhotoChallengesCompleted",
        Fh2ChampionshipDefinitions.Reference.Value.PhotoCars, true);
    long[] SupportedOwnedPhotos(out int unknown)
    {
        var known = Fh2ChampionshipDefinitions.Reference.Value.PhotoCars;
        var owned = Rows("SELECT DISTINCT CarId FROM Career_Garage").Select(r => Integer(r, "CarId")).ToArray();
        unknown = owned.Count(id => !known.Contains(id)); return owned.Where(known.Contains).ToArray();
    }
    public string PhotoRestriction => Restriction(() => { CheckPhotos(); _ = SupportedOwnedPhotos(out _); });
    public PhotoProgress OwnedPhotoProgress()
    {
        var photographed = CheckPhotos(); var owned = SupportedOwnedPhotos(out int unknown);
        return new(photographed.Count, owned.Length, owned.Count(id => !photographed.Contains(id)), unknown);
    }
    public int PhotographOwnedCars()
    {
        var photographed = CheckPhotos(); var added = SupportedOwnedPhotos(out _).Except(photographed).ToArray();
        return ApplyMappedUnlocks("PhotoCaptures", "CarOrdinal", added, "/CompareStats/PhotoChallengesCompleted", checked((uint)(photographed.Count + added.Length)));
    }

    XElement[] CheckCameras(bool island)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var objects = ParseXml(_xml).Root!.Elements("Object").Where(o => (string?)o.Attribute("Type") == "CameraState").ToArray();
        string[] Names(bool extra) => new[] { (extra ? "pdlc_speed_trap_" : "speed_trap_", extra ? 5 : 30), (extra ? "pdlc_speed_zone_" : "speed_zone_", extra ? 5 : 15) }
            .SelectMany(group => Enumerable.Range(1, group.Item2).Select(i => group.Item1 + i.ToString("00", CultureInfo.InvariantCulture))).ToArray();
        var mainland = Names(false); var islandIds = Names(true); var known = mainland.Concat(islandIds).ToHashSet(StringComparer.Ordinal);
        var ids = objects.Select(o => (string?)o.Attribute("Id") ?? "").ToArray();
        if (ids.Length != ids.Distinct(StringComparer.Ordinal).Count() || ids.Any(id => !known.Contains(id)) || mainland.Any(id => !ids.Contains(id)) ||
            (ids.Count(islandIds.Contains) is not (0 or 10))) throw new InvalidDataException("Unsupported FH2 speed-camera definitions.");
        if (island && !islandIds.All(ids.Contains)) throw new InvalidDataException("Visit Storm Island and save before revealing its cameras.");
        foreach (var obj in objects)
        {
            var fields = obj.Elements().ToArray();
            if (fields.Length != 3 || !fields.Select(p => (string?)p.Attribute("Name")).Order().SequenceEqual(new[] { "Completed", "Discovered", "PersonalBest" }))
                throw new InvalidDataException("Unsupported speed-camera fields.");
            string Value(string name) => fields.Single(p => (string?)p.Attribute("Name") == name).Attribute("Value")?.Value ?? "";
            if (Value("Discovered") is not ("true" or "false") || Value("Completed") is not ("true" or "false") ||
                !double.TryParse(Value("PersonalBest"), NumberStyles.Float, CultureInfo.InvariantCulture, out double best) || !double.IsFinite(best) || best < 0 ||
                (Value("Completed") == "true" && (Value("Discovered") != "true" || best <= 0)))
                throw new InvalidDataException("Unknown speed-camera state; existing results are not rewritten.");
        }
        var selected = (island ? islandIds : mainland).ToHashSet(StringComparer.Ordinal);
        return objects.Where(o => selected.Contains((string)o.Attribute("Id")!)).ToArray();
    }
    public string CameraRestriction(bool island = false) => Restriction(() => CheckCameras(island));
    public CameraProgress SpeedCameraProgress(bool island = false)
    {
        var objects = CheckCameras(island);
        return new(objects.Count(o => o.Elements("Property").Single(p => (string?)p.Attribute("Name") == "Discovered").Attribute("Value")!.Value == "true"), objects.Length);
    }
    public int RevealSpeedCameras(bool island = false)
    {
        var pending = CheckCameras(island).Where(o => o.Elements("Property").Single(p => (string?)p.Attribute("Name") == "Discovered").Attribute("Value")!.Value == "false").ToArray();
        if (pending.Length == 0) return 0;
        string updated = RewriteXml(pending.ToDictionary(o => ("CameraState", (string)o.Attribute("Id")!, "Discovered"), _ => "true"));
        _xmlChanged |= updated != _xml; _xml = updated; return pending.Length;
    }
}
