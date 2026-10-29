using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

// A self-contained donor for one or more cars, not a game-ready GameDB.
public static class CarRelatedDbExport
{
    public const int ApplicationId = 0x46483643; // FH6C: car-related export
    private static readonly HashSet<string> RequiredImportTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data_Car", "Data_CarBody", "List_UpgradeCarBody", "List_UpgradeRearWing",
        "List_UpgradeCarBodyChassisStiffness", "List_UpgradeCarBodyFrontBumper",
        "List_UpgradeCarBodyHood", "List_UpgradeCarBodyRearBumper",
        "List_UpgradeCarBodySideSkirt", "List_UpgradeCarBodyTireAspectRatioFront",
        "List_UpgradeCarBodyTireAspectRatioRear", "List_UpgradeCarBodyTireWidthFront",
        "List_UpgradeCarBodyTireWidthRear", "List_UpgradeCarBodyTrackSpacingFront",
        "List_UpgradeCarBodyTrackSpacingRear", "List_UpgradeCarBodyWeight",
        "UpgradePresetPackages"
    };
    public sealed record Car(long Id, string Name, int Year)
    {
        public override string ToString() => $"{Name} ({Year})  ·  ID {Id}";
    }
    public sealed record Result(string Path, int Tables, long Rows, IReadOnlyList<string> Warnings);

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static IReadOnlyList<Car> ListCars(string sourcePath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, COALESCE(MediaName, DisplayName, 'Car'), COALESCE(Year, 0) FROM Data_Car ORDER BY MediaName, Year, Id";
        using var reader = cmd.ExecuteReader();
        var cars = new List<Car>();
        while (reader.Read())
            cars.Add(new Car(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return cars;
    }

    public static Car ReadSingleCarDonor(string path)
    {
        var cars = ReadCarDonor(path);
        if (cars.Count != 1)
            throw new InvalidDataException("This operation requires exactly one car in the donor file.");
        return cars[0];
    }

    public static IReadOnlyList<Car> ReadCarDonor(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        if (Scalar(db, "PRAGMA application_id") != ApplicationId &&
            Scalar(db, "PRAGMA application_id") != ConvertedCarReplacementDb.ApplicationId)
            throw new InvalidDataException("Import car DB needs a file made by the updated Export Car Related DB. Re-export the car from its full GameDB first.");
        if (Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Data_Car','List_UpgradeCarBody')") != 2)
            throw new InvalidDataException("The car export is missing required car/body tables. Re-export it from the full GameDB.");
        var cars = ListCars(path);
        if (cars.Count == 0 || cars.Select(c => c.Id).Distinct().Count() != cars.Count)
            throw new InvalidDataException("The donor must contain at least one car, with unique Car IDs.");
        foreach (var car in cars)
            if (Scalar(db, "SELECT COUNT(*) FROM List_UpgradeCarBody WHERE Ordinal=" + car.Id + " AND IsStock=1") != 1)
                throw new InvalidDataException($"The donor is missing a unique stock body for car {car.Id}. Re-export it from the full GameDB.");
        return cars;
    }

    public static Result Export(string sourcePath, long carId, string outputPath)
        => Export(sourcePath, new[] { carId }, outputPath);

    public static Result Export(string sourcePath, IEnumerable<long> carIds, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(carIds);
        var selectedCars = carIds.ToHashSet();
        if (selectedCars.Count == 0) throw new ArgumentException("Select at least one car to export.", nameof(carIds));
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The car-related donor cannot overwrite its source database.");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source database was not found.", sourcePath);
        var sourceGame = GameDbIdentity.Detect(sourcePath);
        if (File.Exists(outputPath)) throw new IOException("The output file already exists. Choose a new name.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Result result;
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString()))
            {
                db.Open();
                Execute(db, "ATTACH DATABASE " + Literal(sourcePath) + " AS src");
                foreach (long carId in selectedCars)
                    if (Scalar(db, "SELECT COUNT(*) FROM src.Data_Car WHERE Id=" + carId) != 1)
                        throw new InvalidOperationException($"Car {carId} is missing or ambiguous in the staged database.");
                Execute(db, "PRAGMA foreign_keys=OFF");
                Execute(db, $"PRAGMA application_id={ApplicationId}");
                Execute(db, $"PRAGMA user_version={(sourceGame == ForzaGame.Unknown ? 1 : 0x46010000 | (int)sourceGame)}");

                // One consistent source snapshot for dependency resolution and
                // copying. Shared rows are copied once, never whole tables.
                Execute(db, "BEGIN");
                var scopes = CarRelatedDbScope.ReadRowScopes(db, "src", selectedCars);
                var tableSql = new List<(string Name, string Sql)>();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT name,sql FROM src.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND sql IS NOT NULL ORDER BY name";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) tableSql.Add((reader.GetString(0), reader.GetString(1)));
                }
                var warnings = new List<string>();
                int tableCount = 0;
                long rowCount = 0;
                foreach (var (table, createSql) in tableSql)
                {
                    var columns = Columns(db, "src", table);
                    if (!scopes.TryGetValue(table, out string? scope)) continue;
                    string select = SelectScoped(table, columns, scope);
                    long count = Scalar(db, "SELECT COUNT(*) FROM (" + select + ")");
                    // Insert each scoped source row once, even when several cars
                    // share a component; retain genuine keyless duplicates.
                    // Preserve the focused importer's required empty schemas.
                    if (count == 0 && !RequiredImportTables.Contains(table)) continue;
                    Execute(db, createSql);
                    Execute(db, $"INSERT INTO {Q(table)} ({string.Join(",", columns.Select(Q))}) {select}");
                    tableCount++;
                    rowCount += count;
                }
                Execute(db, "COMMIT");
                if (rowCount == 0)
                    throw new InvalidOperationException("No car-related rows were found in the source database.");
                using var check = db.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The exported donor database failed SQLite quick_check.");
                result = new Result(outputPath, tableCount, rowCount, warnings);
            }
            if (!ReadCarDonor(tempPath).Select(c => c.Id).ToHashSet().SetEquals(selectedCars))
                throw new InvalidDataException("Exported donor car identities did not match the selection.");
            File.Move(tempPath, outputPath);
            return result;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string SelectScoped(string table, List<string> columns, string scope)
    {
        string projection = string.Join(",", columns.Select(Q));
        return $"SELECT {projection} FROM src.{Q(table)} WHERE {scope}";
    }
    private static List<string> Columns(SqliteConnection db, string schema, string table)
    {
        var result = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({Literal(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }
    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
