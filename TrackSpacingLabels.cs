using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Spacing stores the total axle increase in metres; labels show mm per side.</summary>
internal static class TrackSpacingLabels
{
    internal static string TypeCondition(IEnumerable<string> columns, string part)
    {
        if (columns.Contains("PartName", StringComparer.OrdinalIgnoreCase)) return "PartName='" + part + "'";
        if (columns.Contains("PartEnum", StringComparer.OrdinalIgnoreCase))
            return "PartEnum=" + (part == "TrackSpacingFront" ? "42" : "43");
        throw new InvalidDataException("The DB has no recognized track-width type mapping.");
    }
    static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    static SqliteCommand Command(SqliteConnection db, string sql, params object[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
        return command;
    }
    static List<Dictionary<string, object>> Rows(SqliteConnection db, string sql, params object[] values)
    {
        using var command = Command(db, sql, values); using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object>>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, reader.GetValue, StringComparer.OrdinalIgnoreCase));
        return rows;
    }
    static int Execute(SqliteConnection db, string sql, params object[] values)
    { using var command = Command(db, sql, values); return command.ExecuteNonQuery(); }
    static long Number(object value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
    static string StoredMillimetres(object metres)
    {
        double value = Convert.ToDouble(metres, CultureInfo.InvariantCulture) * 1000d;
        if (!double.IsFinite(value)) throw new InvalidDataException("Track spacing must be finite before its size can be labelled.");
        return Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture);
    }

    static string PerSideMillimetres(object metres)
    {
        double value = Convert.ToDouble(metres, CultureInfo.InvariantCulture) * 500d;
        if (!double.IsFinite(value)) throw new InvalidDataException("Track spacing must be finite before its size can be labelled.");
        return Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
    }

    internal static Dictionary<string, string> Strings(string part, object metres)
    {
        string mm = PerSideMillimetres(metres), axle = part == "TrackSpacingFront" ? "Front" : "Rear";
        // A separate namespace preserves installed labels from the former
        // total-width convention, while repaired DBs use per-side references.
        string stem = "FMT_TrackWidthPerSide_" + axle + "_" + mm;
        return new() { [stem + "_Name"] = mm + " mm", [stem + "_Description"] = axle + " track width offset: " + mm + " mm per side." };
    }

    // A donor remains DB-only: these strings can be regenerated from its
    // physical spacing when the receiving full DB is exported for installation.
    internal static Dictionary<string, string> RequiredStrings(SqliteConnection db)
    {
        var result = new Dictionary<string, string>();
        var tables = Rows(db, "SELECT name FROM sqlite_schema WHERE type='table'").Select(r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!tables.Contains("UpgradeTypes") || !tables.Contains("Upgrades")) return result;
        var columns = Rows(db, "PRAGMA table_info(UpgradeTypes)").Select(r => (string)r["name"]).ToArray();
        foreach (string part in new[] { "TrackSpacingFront", "TrackSpacingRear" })
        {
            string table = "List_UpgradeCarBody" + part;
            if (!tables.Contains(table)) continue;
            string condition = TypeCondition(columns, part);
            foreach (var row in Rows(db, $"SELECT DISTINCT p.Spacing,u.Name,u.Description FROM {Q(table)} p JOIN Upgrades u ON u.Level=p.Level AND u.IsException=0 " +
                "WHERE p.IsStock=0 AND u.TypeId IN(SELECT Id FROM UpgradeTypes WHERE " + condition + " AND IsException=0)"))
            {
                foreach (var entry in Strings(part, row["Spacing"]))
                    if (Equals(row[entry.Key.EndsWith("_Name", StringComparison.Ordinal) ? "Name" : "Description"], UpgradeStringTable.Reference(entry.Key)))
                        result[entry.Key] = entry.Value;
            }
        }
        return result;
    }

    internal static int Repair(SqliteConnection db, string table, long bodyId)
    {
        string part = table switch
        {
            "List_UpgradeCarBodyTrackSpacingFront" => "TrackSpacingFront",
            "List_UpgradeCarBodyTrackSpacingRear" => "TrackSpacingRear",
            _ => throw new ArgumentException("Not a track-spacing table.", nameof(table))
        };
        var parts = Rows(db, $"SELECT Id,Spacing,Level FROM {Q(table)} WHERE CarBodyId=$p0 AND IsStock=0", bodyId);
        if (parts.Count == 0) return 0;
        var typeColumns = Rows(db, "PRAGMA table_info(UpgradeTypes)").Select(row => (string)row["name"]);
        var types = Rows(db, "SELECT Id FROM UpgradeTypes WHERE " + TypeCondition(typeColumns, part) + " AND IsException=0");
        if (types.Count != 1) throw new InvalidDataException("Track-width display metadata is missing or ambiguous; no changes were kept.");
        long type = Number(types[0]["Id"]);
        var definitions = Rows(db, "SELECT * FROM Upgrades WHERE TypeId=$p0 AND IsException=0", type);
        var template = definitions.OrderBy(row => Number(row["Level"]) is >= 1 and <= 3 ? 0 : Number(row["Level"]) > 0 ? 1 : 2)
            .ThenBy(row => Number(row["Level"])).FirstOrDefault()
            ?? throw new InvalidDataException("The DB has no track-width display template.");
        foreach (string column in new[] { "Id", "TypeId", "IsException", "Name", "Level", "SortOrder", "Description" })
            if (!template.ContainsKey(column)) throw new InvalidDataException("Unsupported track-width display layout: " + column);

        int changes = 0;
        Execute(db, "SAVEPOINT track_spacing_labels");
        try
        {
            foreach (var group in parts.GroupBy(row => StoredMillimetres(row["Spacing"])))
            {
                string mm = group.Key;
                var strings = Strings(part, group.First()["Spacing"]);
                string name = UpgradeStringTable.Reference(strings.Keys.First());
                int sortOrder = (int)Math.Clamp(Math.Round(double.Parse(mm, CultureInfo.InvariantCulture) * 1000d), int.MinValue, int.MaxValue);
                string description = UpgradeStringTable.Reference(strings.Keys.Last());
                bool Compatible(long level, out bool used)
                {
                    var uses = Rows(db, $"SELECT DISTINCT Spacing,IsStock FROM {Q(table)} WHERE Level=$p0", level);
                    used = uses.Count > 0;
                    return uses.All(row => Number(row["IsStock"]) == 0 && StoredMillimetres(row["Spacing"]) == mm);
                }
                // Stable IDs/levels let independently exported cars with the
                // same physical size reuse metadata. Keep the original total-
                // width identity so repairing old labels preserves IDs/levels.
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(part + ":" + mm)));
                long level = 1000L + hash % 1_000_000_000u;
                Dictionary<string, object>? definition = null;
                for (int attempt = 0; attempt < 100_000; attempt++, level++)
                {
                    var matches = definitions.Where(row => Number(row["Level"]) == level).ToArray();
                    if (matches.Length > 1 || !Compatible(level, out bool used)) continue;
                    if (matches.Length == 0) break;
                    var candidate = matches[0];
                    if (!used && !Equals(candidate["Name"], name) && !Equals(candidate["Description"], description) &&
                        !Equals(candidate["Name"], mm + " mm")) continue;
                    definition = candidate; break;
                }
                if (level > int.MaxValue) throw new InvalidDataException("No compatible track-width display level remains.");
                if (definition == null)
                {
                    // A collision loop must not end by creating an ambiguous
                    // duplicate (TypeId, IsException, Level) definition.
                    if (definitions.Any(row => Number(row["Level"]) == level) || !Compatible(level, out _))
                        throw new InvalidDataException("No compatible track-width display level remains.");
                    long id = 1_000_000_000L + hash % 1_000_000_000u;
                    for (int attempt = 0; ; attempt++, id++)
                    {
                        if (attempt == 100_000 || id > int.MaxValue) throw new InvalidDataException("No free track-width display ID remains.");
                        using var query = Command(db, "SELECT 1 FROM Upgrades WHERE Id=$p0", id);
                        if (query.ExecuteScalar() is null) break;
                    }
                    definition = new(template, StringComparer.OrdinalIgnoreCase)
                    {
                        ["Id"] = id, ["Level"] = level, ["Name"] = name, ["Description"] = description,
                        ["SortOrder"] = sortOrder
                    };
                    Execute(db, "INSERT INTO Upgrades (" + string.Join(",", definition.Keys.Select(Q)) + ") VALUES (" +
                        string.Join(",", Enumerable.Range(0, definition.Count).Select(i => "$p" + i)) + ")", definition.Values.ToArray());
                    definitions.Add(definition); changes++;
                }
                else if (!Equals(definition["Name"], name) || !Equals(definition["Description"], description) || Number(definition["SortOrder"]) != sortOrder)
                {
                    Execute(db, "UPDATE Upgrades SET Name=$p0,Description=$p1,SortOrder=$p2 WHERE Id=$p3", name, description, sortOrder, definition["Id"]);
                    definition["Name"] = name; definition["Description"] = description; definition["SortOrder"] = sortOrder; changes++;
                }
                foreach (var row in group)
                    if (Number(row["Level"]) != level)
                        changes += Execute(db, $"UPDATE {Q(table)} SET Level=$p0 WHERE Id=$p1 AND IsStock=0", level, row["Id"]);
            }
            Execute(db, "RELEASE track_spacing_labels"); return changes;
        }
        catch
        {
            Execute(db, "ROLLBACK TO track_spacing_labels"); Execute(db, "RELEASE track_spacing_labels"); throw;
        }
    }
}
