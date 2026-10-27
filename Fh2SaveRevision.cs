using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using FH6LocalCryptoTool.GarageViewer;

namespace FH6LocalCryptoTool;

/// <summary>Azimuth FH2 revisions only. Never writes a live revision or invokes FH6 save crypto.</summary>
public sealed partial class Fh2SaveRevision : IDisposable
{
    static readonly string[] BlobNames = { "BinarySaveState", "PlayerDatabase", "ProfileData", "SaveState", "VersionFlags" };
    const string Signature = "Azimuth complete save revision v2";
    readonly Dictionary<string, byte[]> _original = new(StringComparer.Ordinal);
    readonly byte[] _commit;
    readonly string _work;
    readonly SqliteConnection _db;
    readonly HashSet<string> _garageColumns;
    readonly HashSet<long> _protected = new();
    PropertyTree _properties;
    string _xml;
    byte[]? _editedBinary;
    bool _databaseChanged, _xmlChanged, _disposed;
    public string SourcePath { get; }
    public string Revision => Path.GetFileName(SourcePath);
    public bool HasChanges => _databaseChanged || _xmlChanged || _properties.Dirty || _editedBinary != null;
    public string GarageRestriction { get; private set; } = "";
    public bool CanEditGarage => GarageRestriction.Length == 0;
    public long CurrentGarageId => ReadUInt("/Main/CareerCar");
    public int PropertyCount => _properties.Walk().Count(p => !p.Node.IsBag);
    public uint Credits => ReadUInt("/Main/Credits");
    public uint Xp => ReadUInt("/Main/XP");
    public uint Level => ReadUInt("/Main/Level");
    public uint LifetimeSkillTokens => ReadUInt("/Main/NumSkillTokens");
    public uint SkillTokens => uint.Parse(XmlProperty("SkillsState", "CurrentSkillTokensTotal").Attribute("Value")!.Value, CultureInfo.InvariantCulture);
    public uint Wheelspins => uint.Parse(XmlProperty("TombolaState", "Spins").Attribute("Value")!.Value, CultureInfo.InvariantCulture);
    public long GarageCount => Number("SELECT COUNT(*) FROM Career_Garage");
    public uint RoadsDiscovered => ReadUInt("/CompareStats/RoadsDiscovered");
    public uint IslandRoadsDiscovered => ReadUInt("/Island/RoadsDiscovered");
    public string RoadRestriction => RoadRestrictionFor(false);
    public string IslandRoadRestriction => RoadRestrictionFor(true);
    string RoadRestrictionFor(bool island)
    {
        try
        {
            if ((island ? IslandRoadsDiscovered : RoadsDiscovered) > (island ? Fh2RoadDiscoveryState.IslandRoadCount : Fh2RoadDiscoveryState.RoadCount)) return "The saved road counter is outside the inspected FH2 map range.";
            return Fh2RoadDiscoveryState.TryRead(_editedBinary ?? _original["BinarySaveState"], out _, island) ? "" :
                island ? "Storm Island's recognized discovery record is not present. Load a save made after visiting the island; unknown layouts are disabled." : "This FH2 binary/map layout is not recognized; road editing is disabled.";
        }
        catch (Exception ex) { return "Unsupported FH2 road layout: " + ex.Message; }
    }
    public bool DiscoverAllRoads(bool island = false)
    {
        string restriction = RoadRestrictionFor(island); if (restriction.Length != 0) throw new InvalidOperationException(restriction);
        byte[] binary = _editedBinary ?? _original["BinarySaveState"];
        Fh2RoadDiscoveryState.TryRead(binary, out var info, island);
        uint total = island ? Fh2RoadDiscoveryState.IslandRoadCount : Fh2RoadDiscoveryState.RoadCount;
        if ((island ? IslandRoadsDiscovered : RoadsDiscovered) == total && info.DiscoveredSamples == Fh2RoadDiscoveryState.BitmapCapacity) return false;
        byte[] edited = Fh2RoadDiscoveryState.DiscoverAll(binary, island);
        var staged = StageProperties(new Dictionary<string, uint> { [island ? "/Island/RoadsDiscovered" : "/CompareStats/RoadsDiscovered"] = total });
        _properties = staged; _editedBinary = edited; return true;
    }

    public static bool IsCandidate(string path) => Directory.Exists(path) ||
        Path.GetFileName(path) is "commit" or "ProfileData" or "PlayerDatabase" or "SaveState" or "BinarySaveState" or "VersionFlags";

    public static Fh2SaveRevision Load(string path) => new(Resolve(path));

    static string Resolve(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(path).Equals("data", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
        if (File.Exists(Path.Combine(path, "commit"))) return path;
        string revisions = Directory.Exists(Path.Combine(path, "revisions")) ? Path.Combine(path, "revisions") : path;
        if (!Directory.Exists(revisions)) throw new DirectoryNotFoundException("Choose an FH2 revision folder, its data folder, or revisions folder.");
        var candidates = Directory.EnumerateDirectories(revisions).Where(p => Regex.IsMatch(Path.GetFileName(p), "^[0-9a-fA-F]{16}$"))
            .OrderByDescending(p => ulong.Parse(Path.GetFileName(p), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        // Do not silently fall back to an older revision if the newest is corrupt.
        return candidates.FirstOrDefault() ?? throw new InvalidDataException("No FH2/Azimuth revision was found.");
    }

    Fh2SaveRevision(string source)
    {
        SourcePath = source; RejectLinks(source); RejectLinks(Path.Combine(source, "data"));
        if (!Directory.EnumerateFileSystemEntries(source).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)
            .SequenceEqual(new[] { "commit", "data" })) throw new InvalidDataException("Unexpected files in this revision root; only commit and data are supported.");
        _commit = ReadBounded(Path.Combine(source, "commit"), 16_384);
        string[] lines = new UTF8Encoding(false, true).GetString(_commit).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length != 7 || lines[0] != Signature || lines[1] != "blobs 5")
            throw new InvalidDataException("Only complete Azimuth v2 FH2 revisions with five blobs are supported.");
        foreach (string line in lines.Skip(2))
        {
            var match = Regex.Match(line, "^([0-9a-fA-F]{64}) ([0-9]+) ([A-Za-z]+)$");
            if (!match.Success || !BlobNames.Contains(match.Groups[3].Value) || _original.ContainsKey(match.Groups[3].Value))
                throw new InvalidDataException("Invalid or duplicate revision manifest entry.");
            string name = match.Groups[3].Value;
            byte[] bytes = ReadBounded(Path.Combine(source, "data", name), name == "PlayerDatabase" ? 64 * 1024 * 1024 : 32 * 1024 * 1024);
            if (!long.TryParse(match.Groups[2].Value, out long size) || size != bytes.Length ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(name + ": revision size/SHA256 mismatch. No edits are allowed.");
            _original.Add(name, bytes);
        }
        var actual = Directory.EnumerateFileSystemEntries(Path.Combine(source, "data")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);
        if (!actual.SequenceEqual(BlobNames.OrderBy(n => n, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected or missing revision data files; this layout is not supported.");
        if (!_original["VersionFlags"].AsSpan().SequenceEqual(new byte[] { 6, 0, 0, 0 }))
            throw new InvalidDataException("This FH2 save version is not supported.");
        byte[] profile = Unwrap(_original["ProfileData"]);
        _properties = PropertyTree.ParseFh2(profile);
        if (!_properties.RoundTripsCleanly(profile)) throw new InvalidDataException("FH2 properties do not rebuild exactly.");
        _xml = new UTF8Encoding(false, true).GetString(_original["SaveState"]);
        if (ParseXml(_xml).Root?.Name != "ObjectDictionary") throw new InvalidDataException("Unsupported FH2 save state.");
        _ = Credits; _ = Xp; _ = Level; _ = SkillTokens; _ = Wheelspins;
        if (Unwrap(_original["PlayerDatabase"]).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) == false)
            throw new InvalidDataException("FH2 PlayerDatabase is not SQLite.");
        Directory.CreateDirectory(TempWorkspace.Root);
        _work = Path.Combine(TempWorkspace.Root, "fh2-save-" + Guid.NewGuid().ToString("N") + ".sqlite");
        using (var output = new FileStream(_work, FileMode.CreateNew)) output.Write(Unwrap(_original["PlayerDatabase"]));
        try { _db = GarageCarCatalog.Open(_work, SqliteOpenMode.ReadWrite); }
        catch { GarageCarCatalog.Cleanup(_work); throw; }
        try
        {
            CheckDatabase();
            var tables = Rows("SELECT name FROM sqlite_master WHERE type='table'").Select(r => (string)r["name"]).ToHashSet(StringComparer.Ordinal);
            if (!tables.Contains("Career_Garage") || !tables.Contains("Career_PurchasedParts")) throw new InvalidDataException("Not an FH2 garage database.");
            _garageColumns = Columns("Career_Garage");
            var parts = Columns("Career_PurchasedParts");
            if (!new[] { "Id", "CarId", "Guid", "Flags", "OriginalOwner", "DistanceDriven", "TopSpeed" }.All(_garageColumns.Contains) ||
                !parts.SetEquals(new[] { "GarageId", "PartEnum", "PartId", "ManufacturerId", "PricePaid" }))
                GarageRestriction = "Unsupported FH2 garage/parts schema; viewing only.";
            if (!tables.Contains("LastUsedGarageId") || !Columns("LastUsedGarageId").SetEquals(new[] { "LastUsedGarageId" }) ||
                Number("SELECT COUNT(*) FROM LastUsedGarageId") != 1)
                GarageRestriction = "Unsupported garage ID allocator; viewing only.";
            var schema = Rows("PRAGMA table_info(Career_Garage)");
            if (schema.Count(r => Convert.ToInt64(r["pk"]) != 0) != 1 ||
                !schema.Any(r => (string)r["name"] == "Id" && (string)r["type"] == "INTEGER" && Convert.ToInt64(r["pk"]) == 1))
                GarageRestriction = "Unsupported garage identity; viewing only.";
            if (!new[] { ("OriginalOwner", "TEXT"), ("DistanceDriven", "INT"), ("TopSpeed", "REAL") }
                .All(f => schema.Any(c => (string)c["name"] == f.Item1 && (string)c["type"] == f.Item2)))
                GarageRestriction = "Unsupported garage detail field types; viewing only.";
            if (Number("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger'") != 0)
                GarageRestriction = "Unrecognized save database triggers; viewing only.";
            if (Number("SELECT COUNT(*) FROM Career_Garage WHERE Id=$p0", CurrentGarageId) != 1)
                GarageRestriction = "Current-car reference is not a garage record; viewing only.";
            _protected.Add(CurrentGarageId);
            foreach (var p in _properties.Walk().Where(p => p.Path == "/Main/CareerCarNext"))
                if (long.TryParse(p.Node.DisplayValue(), out long pending) && pending > 0) _protected.Add(pending);
            foreach (string table in tables.Where(t => t != "Career_PurchasedParts"))
                if (Columns(table).Contains("GarageId")) GarageRestriction = table + ": unrecognized garage-linked data; viewing only.";
            foreach (string table in tables)
                if (Rows("PRAGMA foreign_key_list(" + Q(table) + ")").Any(r => (string)r["table"] is "Career_Garage" or "Career_PurchasedParts"))
                    GarageRestriction = table + ": unsupported garage foreign-key references; viewing only.";
            if (tables.Contains("BarnFinds") && Columns("BarnFinds").Contains("CarOrdinal"))
                foreach (var r in Rows("SELECT g.Id FROM Career_Garage g JOIN BarnFinds b ON b.CarOrdinal=g.CarId")) _protected.Add(Convert.ToInt64(r["Id"]));
            VerifySource();
        }
        catch { _db.Dispose(); GarageCarCatalog.Cleanup(_work); throw; }
    }

    static void RejectLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked/junction save paths are not supported.");
    }
    static byte[] ReadBounded(string path, int max)
    {
        RejectLinks(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length == 0 || f.Length > max) throw new InvalidDataException("Invalid save-file size: " + Path.GetFileName(path));
        byte[] bytes = new byte[checked((int)f.Length)]; f.ReadExactly(bytes); return bytes;
    }
    static byte[] Unwrap(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 4).SequenceEqual("ssmc"u8)) throw new InvalidDataException("Missing FH2 ssmc wrapper.");
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (used < 4 || used > bytes.Length - 8 || bytes.AsSpan(8 + (int)used).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid FH2 wrapper length/padding.");
        return bytes.AsSpan(8, (int)used).ToArray();
    }
    static byte[] Wrap(byte[] data, byte[] template)
    {
        if (data.Length > template.Length - 8) throw new InvalidDataException("Edited data exceeds this save's reserved capacity.");
        byte[] bytes = new byte[template.Length]; template.AsSpan(0, 4).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)data.Length)); data.CopyTo(bytes, 8);
        if (!Unwrap(bytes).AsSpan().SequenceEqual(data)) throw new InvalidDataException("FH2 wrapper verification failed.");
        return bytes;
    }
    static XDocument ParseXml(string text)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    XElement XmlProperty(string type, string name) => ParseXml(_xml).Root!.Elements("Object").Where(o => (string?)o.Attribute("Type") == type)
        .Single().Elements("Property").Single(p => (string?)p.Attribute("Name") == name);
    PropertyTree.PropertyNode Field(string path) => _properties.Walk().Where(p => p.Path == path).Select(p => p.Node).Single();
    uint ReadUInt(string path)
    {
        var n = Field(path);
        if (n.TypeId != 3 || n.RawValue.Length != 4) throw new InvalidDataException(path + ": unsupported FH2 property type.");
        return uint.Parse(n.DisplayValue(), CultureInfo.InvariantCulture);
    }
    public void SetValue(string field, uint value)
    {
        if (field is "Skill tokens" or "Available skill points") { SetSkillTokens(value); return; }
        string path = field switch { "Credits" => "/Main/Credits", _ => throw new InvalidOperationException("Use a supported FH2 control; unrelated save records cannot be edited here.") };
        _ = ReadUInt(path);
        if (!Field(path).TrySetFromText(value.ToString(CultureInfo.InvariantCulture))) throw new InvalidDataException("Value could not be encoded.");
    }
    public void SetSkillTokens(uint value)
    {
        if (value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "Available skill points must be between 0 and 2147483647.");
        _ = SkillTokens;
        string updated = RewriteXml(new() { [("SkillsState", "skills", "CurrentSkillTokensTotal")] = value.ToString(CultureInfo.InvariantCulture) });
        _xmlChanged |= updated != _xml; _xml = updated;
    }
    // Keep original XML formatting and every unrelated byte. Verify the full
    // expected semantic document too; a differently framed layout is refused.
    string RewriteXml(Dictionary<(string Type, string Id, string Property), string> changes)
    {
        var expected = ParseXml(_xml); var completed = new HashSet<(string, string, string)>();
        foreach (var (key, value) in changes)
            expected.Root!.Elements("Object").Single(o => (string?)o.Attribute("Type") == key.Type && (string?)o.Attribute("Id") == key.Id)
                .Elements("Property").Single(p => (string?)p.Attribute("Name") == key.Property).Attribute("Value")!.Value = value;
        string updated = Regex.Replace(_xml, @"<Object\b[^>]*?(?<!/)>.*?</Object>", objectMatch =>
        {
            var obj = XElement.Parse(objectMatch.Value); string type = (string?)obj.Attribute("Type") ?? "", id = (string?)obj.Attribute("Id") ?? "";
            if (!changes.Keys.Any(k => k.Type == type && k.Id == id)) return objectMatch.Value;
            return Regex.Replace(objectMatch.Value, @"<Property\b[^>]*/>", propertyMatch =>
            {
                string name = (string?)XElement.Parse(propertyMatch.Value).Attribute("Name") ?? "";
                var key = (type, id, name); if (!changes.TryGetValue(key, out string? value)) return propertyMatch.Value;
                if (!completed.Add(key)) throw new InvalidDataException("Duplicate FH2 XML property.");
                return Regex.Replace(propertyMatch.Value, @"(\bValue\s*=\s*"")[^""]*("")", m => m.Groups[1].Value + System.Security.SecurityElement.Escape(value) + m.Groups[2].Value);
            });
        }, RegexOptions.Singleline);
        if (completed.Count != changes.Count || !XNode.DeepEquals(expected, ParseXml(updated))) throw new InvalidDataException("Unsupported FH2 XML layout; nothing was changed.");
        return updated;
    }
    string CheckXpLayout()
    {
        var preview = Fh2DriverProgression.Preview(Xp);
        if (ReadUInt("/CompareStats/WristbandLevel") != Xp || (Level != preview.SavedIndex && !Fh2DriverProgression.IsPreviousPreviewIndex(Xp, Level)))
            return "Saved XP/level counters do not match the inspected stock FH2 wristband layout; XP editing is disabled.";
        if (ReadUInt("/Main/XpPendingUpload") != 0) return "Pending XP is present. Let FH2 finish saving before editing XP.";
        return "";
    }
    public string DriverXpRestriction { get { try { return CheckXpLayout(); } catch (Exception ex) { return "Unsupported FH2 XP layout: " + ex.Message; } } }
    public Fh2DriverProgression.Progress DriverProgress => Fh2DriverProgression.Preview(Xp);
    public bool DriverXpNeedsCounterRepair => Level != DriverProgress.SavedIndex && Fh2DriverProgression.IsPreviousPreviewIndex(Xp, Level);
    PropertyTree StageProperties(IReadOnlyDictionary<string, uint> changes)
    {
        foreach (string path in changes.Keys) _ = ReadUInt(path);
        var staged = PropertyTree.ParseFh2(_properties.Serialize());
        foreach (var (path, value) in changes)
            if (!staged.Walk().Single(p => p.Path == path).Node.TrySetFromText(value.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Could not encode FH2 field: " + path);
        // Parsing a serialized working tree clears its existing Edited flags;
        // retain them so a later staged edit cannot lose an earlier change.
        var dirty = _properties.Walk().Where(p => p.Node.Edited).Select(p => p.Path).ToHashSet();
        foreach (var (path, node) in staged.Walk()) if (dirty.Contains(path)) node.Edited = true;
        return staged;
    }
    public void SetDriverXp(uint xp)
    {
        string restriction = DriverXpRestriction; if (restriction.Length != 0) throw new InvalidOperationException(restriction);
        var preview = Fh2DriverProgression.Preview(xp);
        _properties = StageProperties(new Dictionary<string, uint> { ["/Main/XP"] = xp, ["/CompareStats/WristbandLevel"] = xp, ["/Main/Level"] = preview.SavedIndex });
    }
    public void SetDriverLevel(uint level) => SetDriverXp(Fh2DriverProgression.XpForLevel(level));

    public sealed record Board(string Id, string Group, string State);
    static readonly string[] BoardCounters = { "/CompareStats/DiscountBoardsSmashed", "/CompareStats/FastTravelBoardsSmashed", "/CompareStats/SmallValueBoardsSmashed", "/CompareStats/MediumValueBoardsSmashed", "/CompareStats/HighValueBoardsSmashed" };
    public IReadOnlyList<Board> Boards()
    {
        var result = new List<Board>();
        var islandIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in ParseXml(_xml).Root!.Elements("Object").Where(o => (string?)o.Attribute("Type") == "DiscountBoardSaveState"))
        {
            string id = (string?)obj.Attribute("Id") ?? "";
            // Visiting Storm Island adds its separate 25 upgrade boards. They
            // must not enter the mainland totals or disable valid base boards.
            var island = Regex.Match(id, @"^(island_)?discount_board_upgrades_(00[1-9]|01[0-9]|02[0-5])$");
            if (island.Success)
            {
                string islandState = obj.Elements("Property").Single(p => (string?)p.Attribute("Name") == "State").Attribute("Value")!.Value;
                if (!islandIds.Add(id) || islandState is not ("Undiscovered" or "Discovered" or "Collected")) throw new InvalidDataException("Unsupported Storm Island board state.");
                continue;
            }
            var match = Regex.Match(id, @"^discount_board_(travel|xp_a|xp_b|xp_c)_([0-9]{3})$");
            if (!match.Success) throw new InvalidDataException("Unknown FH2 board ID; completion is disabled.");
            string group = match.Groups[1].Value, state = obj.Elements("Property").Single(p => (string?)p.Attribute("Name") == "State").Attribute("Value")!.Value;
            if (state is not ("Undiscovered" or "Discovered" or "Collected")) throw new InvalidDataException("Unknown FH2 board state.");
            result.Add(new(id, group, state));
        }
        var groups = new[] { ("travel", 50), ("xp_a", 50), ("xp_b", 35), ("xp_c", 15) };
        foreach (string prefix in new[] { "island_discount_board_upgrades_", "discount_board_upgrades_" })
            if (islandIds.Count(id => id.StartsWith(prefix, StringComparison.Ordinal)) is not (0 or 25))
                throw new InvalidDataException("Incomplete Storm Island board definitions; base-game completion is disabled.");
        var expected = groups.SelectMany(g => Enumerable.Range(1, g.Item2).Select(i => "discount_board_" + g.Item1 + "_" + i.ToString("000", CultureInfo.InvariantCulture))).ToHashSet(StringComparer.Ordinal);
        if (result.Count != expected.Count || !expected.SetEquals(result.Select(b => b.Id))) throw new InvalidDataException("Incomplete or unsupported FH2 base-game board list.");
        return result;
    }
    public string BoardRestriction
    {
        get
        {
            try
            {
                var boards = Boards(); var island = IslandBoards();
                int upgrades = island.Count(b => b.State == "Collected");
                if (ReadUInt("/MiscStats/UpgradesBoardsSmashed") != upgrades) return "Storm Island upgrade-board states do not match their saved counter; completion is disabled.";
                var counts = new[] { boards.Count(b => b.State == "Collected") + upgrades, boards.Count(b => b.Group == "travel" && b.State == "Collected"), boards.Count(b => b.Group == "xp_a" && b.State == "Collected"), boards.Count(b => b.Group == "xp_b" && b.State == "Collected"), boards.Count(b => b.Group == "xp_c" && b.State == "Collected") };
                return BoardCounters.Where((path, i) => ReadUInt(path) != counts[i]).Any() ? "Board counters do not match the saved board states; completion is disabled to preserve existing progress." : "";
            }
            catch (Exception ex) { return "Unsupported FH2 board layout: " + ex.Message; }
        }
    }
    public IReadOnlyList<Board> IslandBoards()
    {
        _ = Boards(); // Also validates the known optional island namespaces/states.
        return ParseXml(_xml).Root!.Elements("Object").Where(o => (string?)o.Attribute("Type") == "DiscountBoardSaveState" &&
            ((string?)o.Attribute("Id"))?.StartsWith("island_discount_board_upgrades_", StringComparison.Ordinal) == true)
            .Select(o => new Board(o.Attribute("Id")!.Value, "island", o.Elements("Property").Single(p => (string?)p.Attribute("Name") == "State").Attribute("Value")!.Value)).ToArray();
    }
    public int CompleteBoards(string group)
    {
        if (group is not ("all" or "travel" or "xp_a" or "xp_b" or "xp_c" or "island")) throw new ArgumentException("Choose a supported FH2 board group.");
        string restriction = BoardRestriction; if (restriction.Length != 0) throw new InvalidOperationException(restriction);
        var boards = Boards(); var island = IslandBoards();
        if (group == "island" && island.Count != 25) throw new InvalidOperationException("Visit Storm Island and load its saved 25-board definitions first.");
        var selected = (group == "island" ? island : boards.Where(b => group == "all" || b.Group == group)).Where(b => b.State != "Collected").ToArray();
        if (selected.Length == 0) return 0;
        var ids = selected.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        string updated = RewriteXml(selected.ToDictionary(b => ("DiscountBoardSaveState", b.Id, "State"), _ => "Collected"));
        bool Collected(Board b) => b.State == "Collected" || ids.Contains(b.Id);
        var counts = new[] { boards.Count(Collected) + island.Count(Collected), boards.Count(b => b.Group == "travel" && Collected(b)), boards.Count(b => b.Group == "xp_a" && Collected(b)), boards.Count(b => b.Group == "xp_b" && Collected(b)), boards.Count(b => b.Group == "xp_c" && Collected(b)) };
        var fields = BoardCounters.Select((path, i) => (path, value: (uint)counts[i])).ToDictionary(p => p.path, p => p.value);
        if (group == "island") fields["/MiscStats/UpgradesBoardsSmashed"] = (uint)island.Count(Collected);
        var staged = StageProperties(fields);
        _properties = staged; _xmlChanged |= updated != _xml; _xml = updated; return selected.Length;
    }
    public void SetWheelspins(uint value)
    {
        _ = Wheelspins;
        string updated = Regex.Replace(_xml, @"<Object\b[^>]*\bType=""TombolaState""[^>]*>.*?</Object>", match =>
            Regex.Replace(match.Value, @"(<Property\s+Name=""Spins""\s+Value="")[0-9]+(""\s*/>)", m => m.Groups[1].Value + value.ToString(CultureInfo.InvariantCulture) + m.Groups[2].Value), RegexOptions.Singleline);
        var previous = ParseXml(_xml); var check = ParseXml(updated);
        var oldField = previous.Root!.Elements("Object").Single(o => (string?)o.Attribute("Type") == "TombolaState").Elements("Property").Single(p => (string?)p.Attribute("Name") == "Spins");
        oldField.SetAttributeValue("Value", value.ToString(CultureInfo.InvariantCulture));
        if (!XNode.DeepEquals(previous, check)) throw new InvalidDataException("Wheelspin XML edit changed unexpected data.");
        _xmlChanged |= updated != _xml; _xml = updated;
    }

    static string Q(string name) => GarageCarCatalog.Q(name);
    SqliteCommand Command(string sql, params object[] values)
    {
        var c = _db.CreateCommand(); c.CommandText = sql;
        for (int i = 0; i < values.Length; i++) c.Parameters.AddWithValue("$p" + i, values[i]); return c;
    }
    long Number(string sql, params object[] values) { using var c = Command(sql, values); return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture); }
    void Execute(string sql, params object[] values) { using var c = Command(sql, values); c.ExecuteNonQuery(); }
    List<Dictionary<string, object>> Rows(string sql, params object[] values)
    {
        using var c = Command(sql, values); using var r = c.ExecuteReader(); var rows = new List<Dictionary<string, object>>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, r.GetValue, StringComparer.OrdinalIgnoreCase)); return rows;
    }
    HashSet<string> Columns(string table) => Rows("PRAGMA table_info(" + Q(table) + ")").Select(r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    void CheckDatabase()
    {
        using var c = Command("PRAGMA integrity_check");
        if (!string.Equals(c.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("FH2 database integrity check failed.");
    }
    void Atomic(Action edit)
    {
        if (!CanEditGarage) throw new InvalidOperationException(GarageRestriction);
        using var tx = _db.BeginTransaction();
        try { edit(); CheckDatabase(); tx.Commit(); _databaseChanged = true; } catch { tx.Rollback(); throw; }
    }
    public IReadOnlyList<GarageEntry> Cars() => Rows("SELECT Id,CarId,Guid,PerformanceIndex,ClassID FROM Career_Garage ORDER BY Id")
        .Select(r => new GarageEntry(Convert.ToInt64(r["Id"]), Convert.ToInt64(r["CarId"]), Convert.ToString(r["Guid"])!, false,
            Convert.ToDouble(r["PerformanceIndex"]), Convert.ToInt64(r["ClassID"]), _protected.Contains(Convert.ToInt64(r["Id"])) ? (Convert.ToInt64(r["Id"]) == CurrentGarageId ? "Current car" : "Pending/barn-find car") : "")).ToArray();
    Dictionary<string, object> Car(long id) => Rows("SELECT * FROM Career_Garage WHERE Id=$p0", id).Single();
    public IReadOnlyDictionary<string, object> Details(long id) => Car(id);
    long Insert(Dictionary<string, object> row)
    {
        long next = Math.Max(Math.Max(Number("SELECT COALESCE(MAX(Id),0) FROM Career_Garage"), Number("SELECT COALESCE(MAX(GarageId),0) FROM Career_PurchasedParts")),
            Number("SELECT LastUsedGarageId FROM LastUsedGarageId")) + 1;
        if (next > int.MaxValue) throw new InvalidDataException("Garage IDs exhausted.");
        row["Id"] = next; row["Guid"] = Guid.NewGuid().ToString();
        var fields = row.Keys.Where(_garageColumns.Contains).ToArray();
        Execute("INSERT INTO Career_Garage (" + string.Join(",", fields.Select(Q)) + ") VALUES (" + string.Join(",", fields.Select((_, i) => "$p" + i)) + ")", fields.Select(f => row[f]).ToArray());
        Execute("UPDATE LastUsedGarageId SET LastUsedGarageId=$p0", next); return next;
    }
    public long Duplicate(long id)
    {
        var row = Car(id); long added = 0;
        foreach (string field in new[] { "DistanceDriven", "TimeDriven", "TotalWinnings", "TotalRepairs", "NumVictories", "NumPodiums", "NumRaces", "NumTimesSold", "TimeDrivenInRoadTrips", "CurOwnerNumRaces", "CurOwnerWinnings", "NumSkillPointsEarned", "SharedID" }) row[field] = 0L;
        Atomic(() => { added = Insert(row); Execute("INSERT INTO Career_PurchasedParts SELECT $p0,PartEnum,PartId,ManufacturerId,PricePaid FROM Career_PurchasedParts WHERE GarageId=$p1", added, id); }); return added;
    }
    public void Remove(IReadOnlyCollection<long> ids, IReadOnlySet<long> lockedModels)
    {
        var selected = ids.Distinct().ToArray();
        if (selected.Length == 0 || selected.Length >= GarageCount) throw new InvalidOperationException("Select cars and keep at least one garage car.");
        foreach (long id in selected)
            if (_protected.Contains(id) || lockedModels.Contains(Convert.ToInt64(Car(id)["CarId"]))) throw new InvalidOperationException("Current, pending, barn-find and non-removable cars are protected.");
        Atomic(() => { foreach (long id in selected) { Execute("DELETE FROM Career_PurchasedParts WHERE GarageId=$p0", id); Execute("DELETE FROM Career_Garage WHERE Id=$p0", id); } });
    }
    public void UpdateDetails(long id, string owner, long distance, double speed)
    {
        _ = Car(id);
        if (owner.Length > 256 || owner.Any(char.IsControl) || distance < 0 || distance > int.MaxValue || !double.IsFinite(speed) || speed < 0 || speed > float.MaxValue)
            throw new InvalidDataException("Use a single-line owner label, non-negative distance up to 2147483647, and a finite non-negative speed.");
        Atomic(() => Execute("UPDATE Career_Garage SET OriginalOwner=$p0,DistanceDriven=$p1,TopSpeed=$p2 WHERE Id=$p3", owner, distance, speed, id));
    }
    /// <summary>Replace only the display label, never per-car statistics or account identity.</summary>
    public int UpdateOriginalOwners(IReadOnlyCollection<long> ids, string owner)
    {
        var selected = ids.Distinct().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select at least one garage car.");
        if (owner.Length > 256 || owner.Any(char.IsControl))
            throw new InvalidDataException("Original Owner must be single-line text, at most 256 characters. It does not change account identity.");
        Atomic(() =>
        {
            foreach (long id in selected)
            {
                using var command = Command("UPDATE Career_Garage SET OriginalOwner=$p0 WHERE Id=$p1", owner, id);
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("A selected garage car no longer exists; no owner labels were changed.");
            }
        });
        return selected.Length;
    }
    public IReadOnlyList<long> AddStocks(IReadOnlyCollection<long> carIds, GarageCarCatalog catalog)
    {
        if (catalog.Game != ForzaGame.Horizon2) throw new InvalidDataException("Use the embedded FH2 catalog, never an FH6 catalog.");
        var schema = Rows("PRAGMA table_info(Career_Garage)");
        catalog.ValidateFh2GarageSchema(schema);
        var rows = carIds.Distinct().Select(id =>
        {
            var definition = catalog.Cars.Single(c => c.Id == id);
            if (!definition.CanAddToGarage) throw new InvalidDataException("This model is not addable.");
            var config = catalog.StockConfiguration(id); var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in schema)
            {
                string name = (string)col["name"];
                if (name == "Id") continue;
                if (name.StartsWith("Tuning_", StringComparison.Ordinal)) row[name] = -1d;
                else if (name == "TireBrand") row[name] = -1L;
                else if (config.TryGetValue(name, out var value)) row[name] = value;
                else if (name == "NumOwners") row[name] = 1L;
                else if (name == "Flags") row[name] = 8L;
                else if (name is "OriginalOwner" or "LiveryFileName" or "TuneFileName" or "Thumbnail") row[name] = "";
                else if ((string)col["type"] == "TEXT") row[name] = "";
                else if (Convert.ToInt64(col["notnull"]) != 0 && col["dflt_value"] is DBNull) throw new InvalidDataException("Unknown required garage field: " + name);
                else row[name] = 0L;
            }
            return row;
        }).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Select stock cars first.");
        var added = new List<long>(); Atomic(() => { foreach (var row in rows) added.Add(Insert(row)); }); return added;
    }

    public void VerifySource()
    {
        if (!ReadBounded(Path.Combine(SourcePath, "commit"), 16_384).AsSpan().SequenceEqual(_commit)) throw new IOException("Source revision changed; reload before exporting.");
        foreach (var (name, bytes) in _original)
            if (!ReadBounded(Path.Combine(SourcePath, "data", name), 64 * 1024 * 1024).AsSpan().SequenceEqual(bytes)) throw new IOException(name + ": source changed; reload before exporting.");
    }
    static bool Within(string path, string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Expected picker validation, without throwing an exception on the background task.</summary>
    public bool TryValidateExportDestination(string destination, out string reason)
    {
        reason = "";
        if (_disposed) { reason = "This save has been closed. Load it again before exporting."; return false; }
        try
        {
            destination = Path.GetFullPath(destination);
            if (Directory.Exists(destination) || File.Exists(destination))
            { reason = "Choose a new output folder; existing files are never overwritten."; return false; }
            string? parent = Path.GetDirectoryName(destination);
            if (parent == null || !Directory.Exists(parent))
            { reason = "Choose an existing output parent folder."; return false; }

            // Protect the actual loaded revision and, when applicable, its User
            // save tree. An unrelated sibling named 'revisions' is not a save.
            string protectedRoot = SourcePath;
            string? revisions = Path.GetDirectoryName(SourcePath);
            if (string.Equals(Path.GetFileName(revisions), "revisions", StringComparison.OrdinalIgnoreCase))
            {
                string? user = Path.GetDirectoryName(revisions!);
                protectedRoot = user != null && Path.GetFileName(user).StartsWith("User_", StringComparison.OrdinalIgnoreCase) ? user : revisions!;
            }
            if (Within(destination, protectedRoot))
            { reason = "That folder is inside the loaded save/revisions area. Choose an output folder such as Desktop or Documents. Nothing was written."; return false; }

            for (string? p = parent; p != null; p = Path.GetDirectoryName(p))
            {
                bool revision = File.Exists(Path.Combine(p, "commit")) && Directory.Exists(Path.Combine(p, "data"));
                string? user = Path.GetDirectoryName(p);
                string? player = user == null ? null : Path.GetDirectoryName(user);
                bool azimuthRevisions = string.Equals(Path.GetFileName(p), "revisions", StringComparison.OrdinalIgnoreCase) &&
                    user != null && Path.GetFileName(user).StartsWith("User_", StringComparison.OrdinalIgnoreCase) &&
                    player != null && Path.GetFileName(player).StartsWith("player-", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(player)), "saves", StringComparison.OrdinalIgnoreCase);
                string? userParent = Path.GetDirectoryName(p);
                bool azimuthUser = Path.GetFileName(p).StartsWith("User_", StringComparison.OrdinalIgnoreCase) &&
                    userParent != null && Path.GetFileName(userParent).StartsWith("player-", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(userParent)), "saves", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(p, "revisions"));
                if (revision || azimuthRevisions || azimuthUser)
                { reason = "Export outside live save/revision folders. Choose Desktop or Documents; keep commit and data together. Nothing was written."; return false; }
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { reason = "The selected output path cannot be used: " + ex.Message; return false; }
    }

    public void Export(string destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryValidateExportDestination(destination, out string reason)) throw new IOException(reason);
        destination = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(destination)!; RejectLinks(parent); VerifySource(); CheckDatabase();
        var blobs = _original.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (_properties.Dirty) blobs["ProfileData"] = Wrap(_properties.Serialize(), _original["ProfileData"]);
        if (_xmlChanged) blobs["SaveState"] = new UTF8Encoding(false, true).GetBytes(_xml);
        if (_editedBinary != null) blobs["BinarySaveState"] = _editedBinary;
        string snapshot = _work + ".export." + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, ".fh2-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (_databaseChanged)
            {
                using (var output = GarageCarCatalog.Open(snapshot, SqliteOpenMode.ReadWriteCreate)) _db.BackupDatabase(output);
                blobs["PlayerDatabase"] = Wrap(File.ReadAllBytes(snapshot), _original["PlayerDatabase"]);
            }
            bool same = BlobNames.All(n => blobs[n].AsSpan().SequenceEqual(_original[n]));
            byte[] manifest = same ? _commit : Encoding.UTF8.GetBytes(Signature + "\nblobs 5\n" + string.Concat(BlobNames.Select(n => Convert.ToHexString(SHA256.HashData(blobs[n])).ToLowerInvariant() + " " + blobs[n].Length.ToString(CultureInfo.InvariantCulture) + " " + n + "\n")));
            Directory.CreateDirectory(Path.Combine(staging, "data"));
            foreach (string n in BlobNames) using (var output = new FileStream(Path.Combine(staging, "data", n), FileMode.CreateNew)) output.Write(blobs[n]);
            using (var output = new FileStream(Path.Combine(staging, "commit"), FileMode.CreateNew)) output.Write(manifest);
            using (var verified = Load(staging))
            {
                if (verified.Credits != Credits || verified.SkillTokens != SkillTokens || verified.Wheelspins != Wheelspins || verified.GarageCount != GarageCount || verified.Xp != Xp || verified.Level != Level)
                    throw new InvalidDataException("Exported FH2 revision failed verification.");
                foreach (string n in BlobNames) if (!verified._original[n].AsSpan().SequenceEqual(blobs[n])) throw new InvalidDataException("Exported blob differs: " + n);
            }
            VerifySource(); RejectLinks(parent); Directory.Move(staging, destination);
        }
        finally
        {
            GarageCarCatalog.Cleanup(snapshot);
            // Only our own known files, never recursive deletion of a computed directory.
            if (Directory.Exists(staging))
            {
                foreach (string n in BlobNames) { string f = Path.Combine(staging, "data", n); if (File.Exists(f)) File.Delete(f); }
                if (File.Exists(Path.Combine(staging, "commit"))) File.Delete(Path.Combine(staging, "commit"));
                if (Directory.Exists(Path.Combine(staging, "data"))) Directory.Delete(Path.Combine(staging, "data"));
                Directory.Delete(staging);
            }
        }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _db.Dispose(); GarageCarCatalog.Cleanup(_work); }
}
