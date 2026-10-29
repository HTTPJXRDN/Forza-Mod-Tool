using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public sealed record ConvertedCarReplacement(long CarId, string MediaName);

/// <summary>A typed donor signature; no SQL or executable instructions in the file.</summary>
public static class ConvertedCarReplacementDb
{
    public const int ApplicationId = 0x46485250; // FHRP, distinct from normal FH6C car exports.

    public static ConvertedCarReplacement? Read(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA application_id";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != ApplicationId) return null;
        cmd.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != (0x46010000 | (int)ForzaGame.Horizon6))
            throw new InvalidDataException("Unsupported converted-car replacement format.");
        cmd.CommandText = "SELECT Id,MediaName FROM Data_Car";
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1))
            throw new InvalidDataException("Replacement donor is missing its target car identity.");
        var car = new ConvertedCarReplacement(r.GetInt64(0), r.GetString(1));
        if (car.CarId <= 0 || string.IsNullOrWhiteSpace(car.MediaName) || r.Read())
            throw new InvalidDataException("A converted replacement donor must contain exactly one target car.");
        return car;
    }
}

public static partial class Merge
{
    private static readonly HashSet<string> ReplacementKeepNative = new(StringComparer.OrdinalIgnoreCase)
        { "OnDiscContent", "ContentOffersMapping", "AllIECars", "CarRarities",
          "List_AeroPhysics", "List_SpringDamperPhysics", "List_AntiSwayPhysics" };

    private static string? ReplacementCleanup(string table, List<string> columns, long carId)
    {
        if (MergeVersionInfo.IsProtectedTable(table) || ReplacementKeepNative.Contains(table) ||
            table.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) ||
            table.StartsWith("NewProfile_", StringComparison.OrdinalIgnoreCase)) return null;
        string? Find(string n) => columns.FirstOrDefault(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (table.Equals("Data_Car", StringComparison.OrdinalIgnoreCase) || table.Equals("CarTrackOffsets", StringComparison.OrdinalIgnoreCase))
            return Find("Id") is string id ? Q(id) + "=" + carId : null;
        foreach (string field in new[] { "Ordinal", "CarId" })
            if (Find(field) is string column) return Q(column) + "=" + carId;
        return null;
    }

    private static HashSet<long> ReplacementReferences(SqliteConnection con, string field,
        Func<string, bool> tablesAllowed)
    {
        var values = new HashSet<long>();
        foreach (string table in TableNames(con, "ov").Where(tablesAllowed))
        {
            string? column = ColumnNames(con, "ov", table).FirstOrDefault(c => c.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (column is null) continue;
            using var cmd = con.CreateCommand(); cmd.CommandText = $"SELECT {Q(column)} FROM ov.{Q(table)} WHERE {Q(column)} IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) values.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        }
        return values;
    }

    public static CarDbRebuildResult RunConvertedCarReplacement(ModMergePreview preview,
        string outSqlite, Action<string>? log = null)
    {
        var replacement = ConvertedCarReplacementDb.Read(preview.OverlayPath)
            ?? throw new InvalidDataException("Choose an explicitly tagged converted-car replacement donor.");
        var cars = CarRelatedDbExport.ReadCarDonor(preview.OverlayPath);
        if (cars.Count != 1 || cars[0].Id != replacement.CarId || preview.Warnings.Count != 0)
            throw new InvalidDataException("The converted replacement donor is incomplete or incompatible.");
        string output = Path.GetFullPath(outSqlite);
        if (File.Exists(output) || string.Equals(output, preview.BasePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(output, preview.OverlayPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new output path; source databases are never overwritten.");
        VerifyCarRebuildInputs(preview);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string partial = output + ".partial." + Guid.NewGuid().ToString("N");
        int removed = 0, inserted = 0;
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = preview.BasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = partial, Pooling = false }.ToString()))
            { source.Open(); target.Open(); source.BackupDatabase(target); }
            using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
            {
                con.Open(); Exec(con, "PRAGMA foreign_keys=OFF");
                Exec(con, $"ATTACH DATABASE '{SqlPath(preview.OverlayPath)}' AS ov");
                Exec(con, $"ATTACH DATABASE '{SqlPath(preview.BasePath)}' AS original");
                using (var identity = con.CreateCommand())
                {
                    identity.CommandText = "SELECT MediaName FROM main.Data_Car WHERE Id=$id";
                    identity.Parameters.AddWithValue("$id", replacement.CarId);
                    if (!string.Equals(identity.ExecuteScalar() as string, replacement.MediaName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The replacement target car ID/media name does not match this receiving DB.");
                }
                var baseTables = TableNames(con, "main"); var donorTables = TableNames(con, "ov");
                var scopes = CarRelatedDbScope.ReadRowScopes(con, "ov", replacement.CarId);
                bool PartTable(string t) => t.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase) || t.Equals("List_Wheels", StringComparison.OrdinalIgnoreCase);
                var brands = ReplacementReferences(con, "ManufacturerID", PartTable);
                brands.UnionWith(ReplacementReferences(con, "PartManufacturerID", PartTable));
                var torque = new HashSet<long>();
                foreach (string field in new[] { "TorqueCurveID", "TorqueCurveFullThrottleID", "TorqueCurveClosedThrottleID" })
                    torque.UnionWith(ReplacementReferences(con, field, t => PartTable(t) || t.StartsWith("Data_", StringComparison.OrdinalIgnoreCase)));
                string In(HashSet<long> ids) => ids.Count == 0 ? "NULL" : string.Join(",", ids.Order());
                scopes["List_PartManufacturer"] = "Id IN (" + In(brands) + ")";
                scopes["List_TorqueCurve"] = "TorqueCurveID IN (" + In(torque) + ")";
                foreach (string table in donorTables)
                {
                    if (MergeVersionInfo.IsProtectedTable(table) || table.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) ||
                        table.StartsWith("NewProfile_", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Replacement donors cannot contain profile or build metadata tables.");
                    if (!baseTables.Contains(table) ||
                        !ColumnNames(con, "main", table).SequenceEqual(ColumnNames(con, "ov", table), StringComparer.OrdinalIgnoreCase) ||
                        !PrimaryKeyColumns(con, "main", table).SequenceEqual(PrimaryKeyColumns(con, "ov", table), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException(table + ": incompatible replacement schema.");
                    if (!scopes.TryGetValue(table, out string? scope))
                    {
                        if (CarRebuildCount(con, $"SELECT COUNT(*) FROM ov.{Q(table)}", null) != 0)
                            throw new InvalidDataException(table + ": replacement data is outside the selected car's dependency graph.");
                    }
                    else if (CarRebuildCount(con, $"SELECT COUNT(*) FROM ov.{Q(table)} WHERE NOT COALESCE(({scope}),0)", null) != 0)
                        throw new InvalidDataException(table + ": replacement contains unrelated car/dependency rows.");
                }
                string version = MergeVersionInfo.Capture(con);
                string schemaBefore = CarRebuildSchema(con, baseTables);
                var fkBefore = CarRebuildForeignKeys(con);
                using (var tx = con.BeginTransaction())
                {
                    // Existing shared definitions must match. Only target-owned
                    // links may be removed; private donor dependencies are added.
                    foreach (string table in donorTables)
                    {
                        var columns = ColumnNames(con, "ov", table); var keys = PrimaryKeyColumns(con, "ov", table);
                        if (keys.Count == 0 || ReplacementCleanup(table, columns, replacement.CarId) is not null) continue;
                        string match = string.Join(" AND ", keys.Select(k => $"b.{Q(k)} IS o.{Q(k)}"));
                        string same = string.Join(" AND ", columns.Select(c => $"b.{Q(c)} IS o.{Q(c)}"));
                        if (CarRebuildCount(con, $"SELECT COUNT(*) FROM ov.{Q(table)} o JOIN main.{Q(table)} b ON {match} WHERE NOT ({same})", tx) != 0)
                            throw new InvalidDataException(table + ": a replacement component conflicts with existing shared data.");
                    }
                    foreach (string table in baseTables)
                    {
                        string? cleanup = ReplacementCleanup(table, ColumnNames(con, "main", table), replacement.CarId);
                        if (cleanup is null) continue;
                        using var cmd = con.CreateCommand(); cmd.Transaction = tx;
                        cmd.CommandText = $"DELETE FROM main.{Q(table)} WHERE {cleanup}"; removed += cmd.ExecuteNonQuery();
                    }
                    foreach (string table in donorTables)
                    {
                        var columns = ColumnNames(con, "ov", table); string projection = string.Join(",", columns.Select(Q));
                        string same = string.Join(" AND ", columns.Select(c => $"b.{Q(c)} IS o.{Q(c)}"));
                        string copy = "__converted_replacement_copy"; while (columns.Contains(copy, StringComparer.OrdinalIgnoreCase)) copy += "_";
                        using var cmd = con.CreateCommand(); cmd.Transaction = tx;
                        cmd.CommandText = $"INSERT INTO main.{Q(table)} ({projection}) SELECT {projection} FROM " +
                            $"(SELECT {projection},ROW_NUMBER() OVER(PARTITION BY {projection}) AS {Q(copy)} FROM ov.{Q(table)}) o " +
                            $"WHERE o.{Q(copy)}>(SELECT COUNT(*) FROM main.{Q(table)} b WHERE {same})";
                        inserted += cmd.ExecuteNonQuery();
                    }
                    // Independently compare the complete DB to the expected row
                    // multisets: preserved original rows plus this donor's rows.
                    foreach (string table in baseTables)
                    {
                        var columns = ColumnNames(con, "main", table); string cols = string.Join(",", columns.Select(Q));
                        string? cleanup = ReplacementCleanup(table, columns, replacement.CarId);
                        string expected = $"SELECT {cols},COUNT(*) AS n FROM original.{Q(table)}" +
                            (cleanup is null ? "" : $" WHERE NOT COALESCE(({cleanup}),0)") + $" GROUP BY {cols}";
                        if (donorTables.Contains(table))
                            expected = $"SELECT {cols},MAX(n) FROM ({expected} UNION ALL SELECT {cols},COUNT(*) AS n FROM ov.{Q(table)} GROUP BY {cols}) GROUP BY {cols}";
                        string actual = $"SELECT {cols},COUNT(*) FROM main.{Q(table)} GROUP BY {cols}";
                        if (CarRebuildCount(con, $"SELECT EXISTS(SELECT * FROM ({actual} EXCEPT {expected})) OR EXISTS(SELECT * FROM ({expected} EXCEPT {actual}))", tx) != 0)
                            throw new InvalidDataException(table + ": replacement altered data outside its permitted rows.");
                    }
                    MergeVersionInfo.VerifyPreserved(con, version, tx);
                    if (CarRebuildSchema(con, baseTables, tx) != schemaBefore)
                        throw new InvalidDataException("Replacement changed the receiving schema.");
                    foreach (var (key, count) in CarRebuildForeignKeys(con, tx))
                        if (count > fkBefore.GetValueOrDefault(key)) throw new InvalidDataException("Replacement introduced a foreign-key warning: " + key);
                    tx.Commit();
                }
                using var check = con.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Replacement DB integrity check failed.");
            }
            VerifyCarRebuildInputs(preview); File.Move(partial, output);
            log?.Invoke($"Converted replacement car {replacement.CarId}: {removed:n0} old links removed, {inserted:n0} donor rows inserted; other cars and VersionInfo retained.");
            return new CarDbRebuildResult(removed, inserted);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
