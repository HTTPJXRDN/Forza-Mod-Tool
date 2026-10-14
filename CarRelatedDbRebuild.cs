using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public sealed record CarDbRebuildResult(int RemovedRows, int InsertedRows);

public static partial class Merge
{
    // A car export is sparse. Never pass its tables to the general
    // DROP TABLE replacement: that would delete every other car's records.
    public static CarDbRebuildResult RebuildCar(ModMergePreview preview, long carId,
        string outSqlite, Action<string>? log = null)
        => RebuildCars(preview, new[] { carId }, outSqlite, log);

    public static CarDbRebuildResult RebuildCars(ModMergePreview preview, IEnumerable<long> carIds,
        string outSqlite, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(carIds);
        var selectedCars = carIds.ToHashSet();
        if (selectedCars.Count == 0 || !selectedCars.SetEquals(CarRelatedDbExport.ReadCarDonor(preview.OverlayPath).Select(c => c.Id)))
            throw new InvalidDataException("The donor must contain exactly the selected cars.");
        // One atomic rebuild for the entire bundle: components shared only by
        // selected cars are writable, while all unselected cars stay protected.
        if (preview.Warnings.Count > 0)
            throw new InvalidDataException("The donor schema is incompatible; car rebuild was stopped.");
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
            using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = partial, Pooling = false }.ToString()))
            {
                source.Open(); snapshot.Open(); source.BackupDatabase(snapshot);
            }
            using (var con = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = partial, Pooling = false }.ToString()))
            {
                con.Open();
                string version = MergeVersionInfo.Capture(con);
                var foreignKeysBefore = CarRebuildForeignKeys(con);
                Exec(con, "PRAGMA foreign_keys=OFF;");
                Exec(con, $"ATTACH DATABASE '{SqlPath(preview.OverlayPath)}' AS ov;");
                var current = CarRelatedDbScope.ReadRowScopes(con, "main", selectedCars);
                var donor = CarRelatedDbScope.ReadRowScopes(con, "ov", selectedCars);
                var otherCars = CarRelatedDbScope.ReadRowScopes(con, "main", selectedCars, otherCars: true);
                var baseTables = TableNames(con, "main");
                var donorTables = TableNames(con, "ov");
                if (!donorTables.Contains("Data_CarBody") ||
                    CarRebuildCount(con, "SELECT COUNT(*) FROM ov.List_UpgradeCarBody u " +
                        "WHERE u.CarBodyID IS NULL OR NOT EXISTS (SELECT 1 FROM ov.Data_CarBody b WHERE b.Id=u.CarBodyID)", null) > 0)
                    throw new InvalidDataException("The donor is missing its car-body records. Re-export the car from its full GameDB before rebuilding.");
                var writable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string table in baseTables.Union(donorTables, StringComparer.OrdinalIgnoreCase))
                {
                    current.TryGetValue(table, out string? oldScope);
                    donor.TryGetValue(table, out string? newScope);
                    otherCars.TryGetValue(table, out string? sharedScope);
                    writable[table] = $"(COALESCE(({oldScope ?? "0"}),0) OR COALESCE(({newScope ?? "0"}),0)) " +
                                      $"AND NOT COALESCE(({sharedScope ?? "0"}),0)";
                }
                var preserved = baseTables.ToDictionary(t => t,
                    t => CarRebuildFingerprint(con, t, "NOT (" + writable[t] + ")"), StringComparer.OrdinalIgnoreCase);
                string schemaBefore = CarRebuildSchema(con, baseTables);
                using (var tx = con.BeginTransaction())
                {
                    // Validate the complete donor, including tables with no row
                    // differences (which the ordinary preview can omit).
                    foreach (string table in donorTables)
                    {
                        if (MergeVersionInfo.IsProtectedTable(table)) continue;
                        if (!donor.TryGetValue(table, out string? scope) ||
                            CarRebuildCount(con, $"SELECT COUNT(*) FROM ov.{Q(table)} WHERE NOT COALESCE(({scope}),0)", tx) > 0)
                            throw new InvalidDataException($"{table}: donor contains data outside the selected cars' export scope. Re-export the cars first.");
                        if (!baseTables.Contains(table)) continue;
                        if (!new HashSet<string>(ColumnNames(con, "main", table), StringComparer.OrdinalIgnoreCase)
                                .SetEquals(ColumnNames(con, "ov", table)) ||
                            !PrimaryKeyColumns(con, "main", table).SequenceEqual(PrimaryKeyColumns(con, "ov", table), StringComparer.OrdinalIgnoreCase) ||
                            IsWithoutRowid(con, "main", table) != IsWithoutRowid(con, "ov", table))
                            throw new InvalidDataException($"{table}: incompatible schema; car rebuild was stopped.");
                    }
                    foreach (var (table, scope) in current)
                    {
                        otherCars.TryGetValue(table, out string? shared);
                        using var delete = con.CreateCommand();
                        delete.Transaction = tx;
                        delete.CommandText = $"DELETE FROM main.{Q(table)} WHERE COALESCE(({scope}),0) " +
                                             $"AND NOT COALESCE(({shared ?? "0"}),0)";
                        int count = delete.ExecuteNonQuery();
                        removed += count;
                        if (count > 0) log?.Invoke($"{table}: removed {count:n0} old car-owned row(s)");
                    }
                    foreach (string table in donorTables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                    {
                        if (MergeVersionInfo.IsProtectedTable(table)) continue;
                        bool isNew = !baseTables.Contains(table);
                        if (isNew) Exec(con, ObjectSql(con, "ov", "table", table)
                            ?? throw new InvalidDataException($"Missing donor schema for {table}."), tx);
                        var columns = ColumnNames(con, "ov", table);
                        var key = PrimaryKeyColumns(con, "ov", table);
                        string projection = string.Join(",", columns.Select(Q));
                        string same = string.Join(" AND ", columns.Select(c => $"b.{Q(c)} IS o.{Q(c)}"));
                        string sql;
                        if (key.Count > 0)
                        {
                            if (CarRebuildCount(con, $"SELECT COUNT(*) FROM ov.{Q(table)} WHERE " +
                                string.Join(" OR ", key.Select(c => Q(c) + " IS NULL")), tx) > 0)
                                throw new InvalidDataException($"{table}: donor has NULL primary keys; rebuild was stopped.");
                            string match = string.Join(" AND ", key.Select(c => $"b.{Q(c)} IS o.{Q(c)}"));
                            if (CarRebuildCount(con, $"SELECT COUNT(*) FROM ov.{Q(table)} o JOIN main.{Q(table)} b ON {match} WHERE NOT ({same})", tx) > 0)
                                throw new InvalidDataException($"{table}: a donor ID conflicts with a record used outside the selected cars. " +
                                    "Rebuild stopped without an output; use unique component IDs or the regular conflict-override import after reviewing the shared change.");
                            sql = $"INSERT INTO main.{Q(table)} ({projection}) SELECT {projection} FROM ov.{Q(table)} o " +
                                  $"WHERE NOT EXISTS (SELECT 1 FROM main.{Q(table)} b WHERE {match})";
                        }
                        else
                        {
                            // Exact-content multiplicity, not hidden row IDs. Keep
                            // shared records and insert only missing donor copies.
                            string copyColumn = "__forza_car_rebuild_copy";
                            while (columns.Contains(copyColumn, StringComparer.OrdinalIgnoreCase)) copyColumn += "_";
                            sql = $"INSERT INTO main.{Q(table)} ({projection}) " +
                                  $"SELECT {projection} FROM (SELECT {projection}, ROW_NUMBER() OVER (PARTITION BY {projection}) AS {Q(copyColumn)} FROM ov.{Q(table)}) o " +
                                  $"WHERE o.{Q(copyColumn)} > (SELECT COUNT(*) FROM main.{Q(table)} b WHERE {same})";
                        }
                        using var insert = con.CreateCommand();
                        insert.Transaction = tx; insert.CommandText = sql;
                        int count = insert.ExecuteNonQuery();
                        inserted += count;
                        if (count > 0) log?.Invoke($"{table}: inserted {count:n0} donor row(s)");
                        if (isNew)
                            foreach (string index in ObjectSqls(con, "ov", "index", table)) Exec(con, index, tx);
                    }
                    // Guard against side effects from existing triggers and
                    // accidental over-broad ownership rules, not just FK failures.
                    foreach (var (table, fingerprint) in preserved)
                        if (CarRebuildFingerprint(con, table, "NOT (" + writable[table] + ")", tx) != fingerprint)
                            throw new InvalidDataException($"{table}: rebuilding would alter records outside the selected cars. No output was written.");
                    if (CarRebuildSchema(con, baseTables, tx) != schemaBefore)
                        throw new InvalidDataException("Car rebuild would alter the receiving schema. No output was written.");
                    VerifyRebuiltCarRows(con, tx, writable, donorTables);
                    MergeVersionInfo.VerifyPreserved(con, version, tx);
                    foreach (var (warning, count) in CarRebuildForeignKeys(con, tx))
                        if (count > foreignKeysBefore.GetValueOrDefault(warning))
                            throw new InvalidDataException("Car rebuild introduced a foreign-key warning: " + warning + ". No output was written.");
                    tx.Commit();
                }
                using var check = con.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The rebuilt car DB did not pass quick_check.");
                Exec(con, "DETACH DATABASE ov;");
            }
            VerifyCarRebuildInputs(preview);
            File.Move(partial, output);
            log?.Invoke($"{selectedCars.Count:n0} car(s) rebuilt: {removed:n0} old rows removed, {inserted:n0} donor rows inserted. Other cars and VersionInfo retained.");
            return new CarDbRebuildResult(removed, inserted);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static void VerifyCarRebuildInputs(ModMergePreview preview)
    {
        if (FileHash(preview.BasePath) != preview.BaseHash || FileHash(preview.OverlayPath) != preview.OverlayHash)
            throw new InvalidOperationException("A source database changed after the preview. Reopen the car import.");
    }

    private static long CarRebuildCount(SqliteConnection con, string sql, SqliteTransaction? tx)
    {
        using var cmd = con.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void VerifyRebuiltCarRows(SqliteConnection con, SqliteTransaction tx,
        Dictionary<string, string> writable, HashSet<string> donorTables)
    {
        foreach (var (table, scope) in writable)
        {
            if (MergeVersionInfo.IsProtectedTable(table)) continue;
            if (!donorTables.Contains(table))
            {
                if (CarRebuildCount(con, $"SELECT COUNT(*) FROM main.{Q(table)} WHERE {scope}", tx) > 0)
                    throw new InvalidDataException($"{table}: old car-owned rows remained after rebuilding. No output was written.");
                continue;
            }
            // Both directions and counts: catches duplicate loss and trigger
            // changes to the selected car, not only changes to other cars.
            string cols = string.Join(",", ColumnNames(con, "ov", table).Select(Q));
            string actual = $"SELECT {cols},COUNT(*) FROM main.{Q(table)} WHERE {scope} GROUP BY {cols}";
            string expected = $"SELECT {cols},COUNT(*) FROM ov.{Q(table)} WHERE {scope} GROUP BY {cols}";
            if (CarRebuildCount(con, $"SELECT EXISTS(SELECT * FROM ({actual} EXCEPT {expected})) " +
                $"OR EXISTS(SELECT * FROM ({expected} EXCEPT {actual}))", tx) != 0)
                throw new InvalidDataException($"{table}: the rebuilt car did not exactly match the donor. No output was written.");
        }
    }

    private static string CarRebuildFingerprint(SqliteConnection con, string table, string where, SqliteTransaction? tx = null)
    {
        var columns = ColumnNames(con, "main", table);
        // A shadowed rowid isn't used for matching; the complete row multiset is
        // still captured. Ordinary rowid tables also retain original row IDs.
        if (!IsWithoutRowid(con, "main", table) && !columns.Any(c =>
                c.Equals("rowid", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("oid", StringComparison.OrdinalIgnoreCase) || c.Equals("_rowid_", StringComparison.OrdinalIgnoreCase)))
            columns.Insert(0, "rowid");
        var rows = new List<string>();
        using var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {string.Join(",", columns.Select(Q))} FROM main.{Q(table)} WHERE {where}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    object value = reader.GetValue(i);
                    switch (value)
                    {
                        case DBNull: writer.Write((byte)0); break;
                        case long number: writer.Write((byte)1); writer.Write(number); break;
                        case double number: writer.Write((byte)2); writer.Write(number); break;
                        case string text: writer.Write((byte)3); writer.Write(text); break;
                        case byte[] blob: writer.Write((byte)4); writer.Write(blob.Length); writer.Write(blob); break;
                        default: throw new InvalidDataException($"Unsupported SQLite value in {table}.");
                    }
                }
            rows.Add(Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))));
        }
        rows.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
    }

    private static string CarRebuildSchema(SqliteConnection con, HashSet<string> tables, SqliteTransaction? tx = null)
    {
        var objects = new List<string>();
        using var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT type,name,tbl_name,sql FROM main.sqlite_master ORDER BY type,name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (tables.Contains(reader.GetString(2)))
                objects.Add(System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 4)
                    .Select(i => reader.IsDBNull(i) ? null : reader.GetString(i)).ToArray()));
        return string.Join("\n", objects);
    }

    private static Dictionary<string, int> CarRebuildForeignKeys(SqliteConnection con, SqliteTransaction? tx = null)
    {
        var warnings = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = con.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "PRAGMA main.foreign_key_check";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string warning = $"{reader.GetString(0)} -> {reader.GetString(2)} (constraint {reader.GetInt64(3)})";
            warnings[warning] = warnings.GetValueOrDefault(warning) + 1;
        }
        return warnings;
    }
}
