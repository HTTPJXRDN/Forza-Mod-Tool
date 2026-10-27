using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Resolve the selected cars' DB dependencies, never whole lookup tables or other cars.</summary>
internal static class CarRelatedDbGraph
{
    sealed record Column(string Name, string Type, int Key);
    static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    static string Value(object value) => value switch
    {
        DBNull => "NULL",
        string text => "'" + text.Replace("'", "''") + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        double real when double.IsFinite(real) => real.ToString("R", CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("Unsupported dependency-key value.")
    };
    static bool Allowed(string table) => !MergeVersionInfo.IsProtectedTable(table) &&
        !table.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) &&
        !table.StartsWith("NewProfile_", StringComparison.OrdinalIgnoreCase) &&
        !table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
    static string Or(IEnumerable<string> conditions)
    {
        string[] items = conditions.ToArray();
        string Join(int start, int count) => count == 1 ? items[start]
            : "(" + Join(start, count / 2) + ") OR (" + Join(start + count / 2, count - count / 2) + ")";
        return items.Length == 0 ? "0" : Join(0, items.Length);
    }

    internal static Dictionary<string, string> Expand(SqliteConnection db, string schema,
        Dictionary<string, string> roots)
    {
        var tables = new Dictionary<string, Column[]>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT name FROM {Q(schema)}.sqlite_master WHERE type='table'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) if (Allowed(reader.GetString(0))) tables.Add(reader.GetString(0), Array.Empty<Column>());
        }
        foreach (string table in tables.Keys.ToArray())
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = $"PRAGMA {Q(schema)}.table_info({Q(table)})";
            using var reader = cmd.ExecuteReader(); var columns = new List<Column>();
            while (reader.Read()) columns.Add(new(reader.GetString(1), reader.GetString(2), reader.GetInt32(5)));
            tables[table] = columns.ToArray();
        }
        var scopes = roots.Where(p => tables.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        // Values, not source rowids, are used in returned predicates. The same
        // ownership predicates must work on both receiving and donor databases.
        var lookups = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var tuples = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // Drop & rebuild also reads the donor as "ov". Older sparse exports
        // can omit lookup tables: do not delete a receiving dependency that a
        // donor still references without supplying its replacement record.
        bool checkingDonor = schema.Equals("ov", StringComparison.OrdinalIgnoreCase);
        var receiving = new Dictionary<string, Column[]>(StringComparer.OrdinalIgnoreCase);
        var checkedTargets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        Column[] ReceivingColumns(string table)
        {
            if (!checkingDonor) return Array.Empty<Column>();
            if (receiving.TryGetValue(table, out var cached)) return cached;
            using var cmd = db.CreateCommand(); cmd.CommandText = $"PRAGMA main.table_info({Q(table)})";
            using var reader = cmd.ExecuteReader(); var columns = new List<Column>();
            while (reader.Read()) columns.Add(new(reader.GetString(1), reader.GetString(2), reader.GetInt32(5)));
            return receiving[table] = columns.ToArray();
        }
        void CheckTarget(string table, string[] columns, IEnumerable<string[]> rows)
        {
            if (!checkingDonor || columns.Any(col => !ReceivingColumns(table).Any(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase)))) return;
            string identity = table + ":" + string.Join(",", columns);
            if (!checkedTargets.TryGetValue(identity, out var seen)) checkedTargets.Add(identity, seen = new(StringComparer.Ordinal));
            var requested = rows.Where(r => r.All(v => v != "NULL") && seen.Add(string.Join("\0", r))).ToArray();
            if (requested.Length == 0) return;
            string wanted = columns.Length == 1 ? "b." + Q(columns[0]) + " IN (" + string.Join(",", requested.Select(r => r[0])) + ")"
                : Or(requested.Select(r => "(" + string.Join(" AND ", columns.Select((col, i) => "b." + Q(col) + " IS " + r[i])) + ")"));
            string supplied = tables.ContainsKey(table) && columns.All(col => Field(table, col) is not null)
                ? $"EXISTS (SELECT 1 FROM {Q(schema)}.{Q(table)} d WHERE " + string.Join(" AND ", columns.Select(col => "d." + Q(col) + " IS b." + Q(col))) + ")" : "0";
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM main.{Q(table)} b WHERE ({wanted}) AND NOT ({supplied}) LIMIT 1";
            if (cmd.ExecuteScalar() is not null)
                throw new InvalidDataException(table + ": donor omits referenced dependency rows. Re-export the selected cars from their full GameDB before using Drop & rebuild.");
        }
        long revision = 0;
        string? Field(string table, string name) => tables.TryGetValue(table, out var columns)
            ? columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name : null;
        string? Key(string table, params string[] choices)
        {
            if (!tables.TryGetValue(table, out var columns)) columns = ReceivingColumns(table);
            foreach (string name in choices) if (Field(table, name) is string found) return found;
            var keys = columns.Where(c => c.Key > 0).ToArray(); return keys.Length == 1 ? keys[0].Name : null;
        }
        void Add(string table, string? column, IEnumerable<string> values)
        {
            if (column is null || !tables.ContainsKey(table) || table.Equals("Data_Car", StringComparison.OrdinalIgnoreCase)) return;
            if (!lookups.TryGetValue(table, out var fields)) lookups.Add(table, fields = new(StringComparer.OrdinalIgnoreCase));
            if (!fields.TryGetValue(column, out var ids)) fields.Add(column, ids = new(StringComparer.Ordinal));
            foreach (string value in values) if (value != "NULL" && ids.Add(value)) revision++;
        }
        string[] Read(string table, string column, string where)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT DISTINCT {Q(column)} FROM {Q(schema)}.{Q(table)} WHERE {where} AND {Q(column)} IS NOT NULL";
            using var reader = cmd.ExecuteReader(); var values = new List<string>();
            while (reader.Read()) values.Add(Value(reader.GetValue(0))); return values.ToArray();
        }
        void Root(string table, string where)
        {
            if (!tables.ContainsKey(table)) return;
            scopes[table] = scopes.TryGetValue(table, out string? previous) ? $"({previous}) OR ({where})" : where;
        }
        if (scopes.TryGetValue("Data_Car", out string? carScope))
        {
            string[] cars = Read("Data_Car", Field("Data_Car", "Id")!, "(" + carScope + ")");
            foreach (string table in new[] { "OnDiscContent", "ContentOffersMapping" })
                if (Field(table, "ContentId") is string content && Field(table, "ContentType") is string type)
                    Root(table, $"{Q(type)}=1 AND {Q(content)} IN ({(cars.Length == 0 ? "NULL" : string.Join(",", cars))})");
        }
        var references = new Dictionary<string, (string Table, string? Key)>(StringComparer.OrdinalIgnoreCase)
        {
            ["CarBodyID"] = ("Data_CarBody", Key("Data_CarBody", "Id")),
            ["EngineID"] = ("Data_Engine", Key("Data_Engine", "EngineID")),
            ["MotorID"] = ("Data_Motor", Key("Data_Motor", "MotorID")),
            ["DrivetrainID"] = ("Data_Drivetrain", Key("Data_Drivetrain", "DrivetrainID")),
            ["TireCompoundID"] = ("List_TireCompound", Key("List_TireCompound", "TireCompoundID")),
            ["AeroPhysicsID"] = ("List_AeroPhysics", Key("List_AeroPhysics", "AeroPhysicsID")),
            ["FrontSpringDamperPhysicsID"] = ("List_SpringDamperPhysics", Key("List_SpringDamperPhysics", "SpringDamperPhysicsID")),
            ["RearSpringDamperPhysicsID"] = ("List_SpringDamperPhysics", Key("List_SpringDamperPhysics", "SpringDamperPhysicsID")),
            ["AntiSwayPhysicsID"] = ("List_AntiSwayPhysics", Key("List_AntiSwayPhysics", "AntiSwayPhysicsID")),
            ["StockWheelID"] = ("List_Wheels", Key("List_Wheels", "Id")),
            ["WheelID"] = ("List_Wheels", Key("List_Wheels", "Id")),
            ["TorqueCurveID"] = ("List_TorqueCurve", Key("List_TorqueCurve", "TorqueCurveID")),
            ["TorqueCurveFullThrottleID"] = ("List_TorqueCurve", Key("List_TorqueCurve", "TorqueCurveID")),
            ["TorqueCurveClosedThrottleID"] = ("List_TorqueCurve", Key("List_TorqueCurve", "TorqueCurveID")),
            ["TireFrictionCurveID"] = ("List_TireFrictionCurve", Key("List_TireFrictionCurve", "FrictionCurveID", "TireFrictionCurveID")),
            ["PartsStringId"] = ("List_PartsStrings", Key("List_PartsStrings", "Id")),
            ["PartManufacturerID"] = ("List_PartManufacturer", Key("List_PartManufacturer", "Id")),
            ["ManufacturerID"] = ("List_PartManufacturer", Key("List_PartManufacturer", "Id")),
            ["MakeID"] = ("List_CarMake", Key("List_CarMake", "Id")),
            ["CarBucket"] = ("CarBuckets", Key("CarBuckets", "Id")),
            ["ParentBucketId"] = ("CarBuckets", Key("CarBuckets", "Id")),
        };
        Dictionary<string, string> Build()
        {
            var result = new Dictionary<string, string>(scopes, StringComparer.OrdinalIgnoreCase);
            foreach (var table in lookups)
            {
                var conditions = table.Value.Where(p => p.Value.Count > 0).Select(p =>
                    Q(p.Key) + (table.Key.Equals("AllIECars", StringComparison.OrdinalIgnoreCase) ? " COLLATE NOCASE" : "") + " IN (" + string.Join(",", p.Value.Order(StringComparer.Ordinal)) + ")").ToArray();
                if (conditions.Length > 0) AddScope(table.Key, Or(conditions));
            }
            foreach (var table in tuples) if (table.Value.Count > 0) AddScope(table.Key, Or(table.Value.Order(StringComparer.Ordinal)));
            return result;
            void AddScope(string table, string condition) => result[table] = result.TryGetValue(table, out string? root) ? $"({root}) OR ({condition})" : condition;
        }
        var foreignKeys = new Dictionary<string, List<(string Target, string[] From, string[] To)>>(StringComparer.OrdinalIgnoreCase);
        foreach (string table in tables.Keys)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = $"PRAGMA {Q(schema)}.foreign_key_list({Q(table)})";
            using var reader = cmd.ExecuteReader(); var records = new List<(int Id, int Sequence, string Target, string From, string? To)>();
            while (reader.Read()) records.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            var links = new List<(string, string[], string[])>();
            foreach (var group in records.GroupBy(r => r.Id))
            {
                var ordered = group.OrderBy(r => r.Sequence).ToArray(); string target = ordered[0].Target;
                if (!Allowed(target) || target.Equals("Data_Car", StringComparison.OrdinalIgnoreCase)) continue;
                if (!tables.TryGetValue(target, out var targetColumns)) targetColumns = ReceivingColumns(target);
                if (targetColumns.Length == 0) continue;
                var keys = targetColumns.Where(c => c.Key > 0).OrderBy(c => c.Key).ToArray();
                string[] to = ordered.Select((r, i) => r.To ?? (keys.Length == ordered.Length ? keys[i].Name : "")).ToArray();
                if (to.Any(string.IsNullOrEmpty)) throw new InvalidDataException(table + ": ambiguous dependency foreign key.");
                links.Add((target, ordered.Select(r => r.From).ToArray(), to));
            }
            foreignKeys.Add(table, links);
        }
        for (int pass = 0; pass < 64; pass++)
        {
            long before = revision;
            foreach (var (table, scope) in Build())
            {
                foreach (var column in tables[table])
                {
                    (string Table, string? Key) target;
                    if (references.TryGetValue(column.Name, out target)) { }
                    else if (column.Name.StartsWith("FrictionMultiCurve", StringComparison.OrdinalIgnoreCase))
                        target = ("List_TireFrictionMultiCurve", Key("List_TireFrictionMultiCurve", "FrictionMultiCurveID"));
                    else if (column.Name.StartsWith("AffectCurve", StringComparison.OrdinalIgnoreCase))
                        target = ("List_TireAffectCurve", Key("List_TireAffectCurve", "AffectCurveID"));
                    else if (column.Name.StartsWith("TireFrictionCurveID", StringComparison.OrdinalIgnoreCase) && column.Name[19..].All(char.IsDigit))
                        target = references["TireFrictionCurveID"];
                    else continue;
                    if (target.Key is not null)
                    {
                        string[] values = Read(table, column.Name, "(" + scope + ")");
                        CheckTarget(target.Table, new[] { target.Key }, values.Select(v => new[] { v }));
                        Add(target.Table, target.Key, values);
                    }
                }
                if (table.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase) && Field(table, "Id") is string partId)
                {
                    var parts = Read(table, partId, "(" + scope + ")");
                    Add("CarPartNames", Key("CarPartNames", "Id"), parts);
                    if (table.Equals("List_UpgradeTireCompound", StringComparison.OrdinalIgnoreCase)) Add("List_UpgradeTireCompoundFictionModOverride", Field("List_UpgradeTireCompoundFictionModOverride", "PartId"), parts);
                }
                if (table is "List_UpgradeCarBodyTrackSpacingFront" or "List_UpgradeCarBodyTrackSpacingRear")
                {
                    string[] levels = Read(table, "Level", "(" + scope + ")");
                    string typeSchema = tables.ContainsKey("UpgradeTypes") ? schema : "main";
                    if (levels.Length > 0 && (tables.ContainsKey("UpgradeTypes") || ReceivingColumns("UpgradeTypes").Length > 0))
                    {
                        string part = table.EndsWith("Front", StringComparison.Ordinal) ? "TrackSpacingFront" : "TrackSpacingRear";
                        using var types = db.CreateCommand();
                        var typeColumns = tables.TryGetValue("UpgradeTypes", out var present) ? present : ReceivingColumns("UpgradeTypes");
                        types.CommandText = $"SELECT Id FROM {Q(typeSchema)}.UpgradeTypes WHERE " + TrackSpacingLabels.TypeCondition(typeColumns.Select(col => col.Name), part) + " AND IsException=0";
                        using var reader = types.ExecuteReader(); var ids = new List<string>();
                        while (reader.Read()) ids.Add(Value(reader.GetValue(0)));
                        CheckTarget("UpgradeTypes", new[] { "Id" }, ids.Select(id => new[] { id }));
                        Add("UpgradeTypes", Field("UpgradeTypes", "Id"), ids);
                        foreach (string id in ids)
                        {
                            CheckTarget("Upgrades", new[] { "TypeId", "Level", "IsException" }, levels.Select(level => new[] { id, level, "0" }));
                            if (Field("Upgrades", "Id") is string key)
                                Add("Upgrades", key, Read("Upgrades", key, $"TypeId={id} AND IsException=0 AND Level IN ({string.Join(",", levels)})"));
                        }
                    }
                }
                if (table is "Data_Car" or "Data_Engine" or "Data_Motor" && Field(table, "MediaName") is string media)
                    Add("AllIECars", Field("AllIECars", "MediaName"), Read(table, media, "(" + scope + ")"));
                foreach (var link in foreignKeys[table])
                {
                    using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT DISTINCT {string.Join(",", link.From.Select(Q))} FROM {Q(schema)}.{Q(table)} WHERE ({scope})";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (Enumerable.Range(0, reader.FieldCount).Any(reader.IsDBNull)) continue;
                        string[] values = Enumerable.Range(0, reader.FieldCount).Select(i => Value(reader.GetValue(i))).ToArray();
                        CheckTarget(link.Target, link.To, new[] { values });
                        if (reader.FieldCount == 1) { Add(link.Target, link.To[0], values); continue; }
                        string condition = "(" + string.Join(" AND ", link.To.Select((col, i) => Q(col) + " IS " + Value(reader.GetValue(i)))) + ")";
                        if (!tuples.TryGetValue(link.Target, out var set)) tuples.Add(link.Target, set = new(StringComparer.Ordinal));
                        if (set.Add(condition)) revision++;
                    }
                }
            }
            foreach (var (field, entity) in new[] { ("CarBodyID", "Data_CarBody"), ("EngineID", "Data_Engine"), ("MotorID", "Data_Motor"), ("DrivetrainID", "Data_Drivetrain") })
            {
                string? key = references[field].Key;
                if (key is null || !lookups.TryGetValue(entity, out var fields) || !fields.TryGetValue(key, out var values)) continue;
                foreach (var table in tables.Where(p => p.Key.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase) && Field(p.Key, "Ordinal") is null))
                    if (Field(table.Key, field) is string column) Add(table.Key, column, values.ToArray());
                if (entity == "Data_CarBody") Add("Data_CarBody_DummyAxle", Field("Data_CarBody_DummyAxle", "Id"), values.ToArray());
            }
            if (revision == before) return Build();
        }
        throw new InvalidDataException("Car dependency graph did not converge; no donor was exported.");
    }
}
