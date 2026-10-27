using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

internal static class CarRelatedDbScope
{
    // Shared export/rebuild ownership mapping. Import/merge conflict policies
    // are unchanged; unselected cars' complete dependency graphs protect rows.
    internal static Dictionary<string, string> ReadRowScopes(SqliteConnection db, string schema,
        long carId, bool otherCars = false)
        => ReadRowScopes(db, schema, new[] { carId }, otherCars);

    internal static Dictionary<string, string> ReadRowScopes(SqliteConnection db, string schema,
        IEnumerable<long> carIds, bool otherCars = false)
    {
        var selectedCars = carIds.ToHashSet();
        if (selectedCars.Count == 0) throw new ArgumentException("Select at least one car.", nameof(carIds));
        var tables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0), new List<string>());
        }
        foreach (string table in tables.Keys.ToArray()) tables[table] = Columns(db, schema, table);
        string comparison = (otherCars ? " NOT IN (" : " IN (") + In(selectedCars) + ")";
        HashSet<long> Read(string table, string column, string where)
            => tables.TryGetValue(table, out var cols) && cols.Contains(column, StringComparer.OrdinalIgnoreCase)
                ? Ids(db, $"SELECT {Q(column)} FROM {schema}.{Q(table)} WHERE {where}") : new HashSet<long>();
        var bodies = Read("List_UpgradeCarBody", "CarBodyID", "Ordinal" + comparison);
        var engines = Read("List_UpgradeEngine", "EngineID", "Ordinal" + comparison);
        var motors = Read("List_UpgradeMotor", "MotorID", "Ordinal" + comparison);
        var drivetrains = Read("List_UpgradeDrivetrain", "DrivetrainID", "Ordinal" + comparison);
        var compounds = Read("List_UpgradeTireCompound", "TireCompoundID", "Ordinal" + comparison);
        var tireParts = Read("List_UpgradeTireCompound", "Id", "Ordinal" + comparison);
        var aero = Read("List_UpgradeRearWing", "AeroPhysicsID", "Ordinal" + comparison);
        aero.UnionWith(Read("List_UpgradeCarBodyFrontBumper", "AeroPhysicsID", $"CarBodyID IN ({In(bodies)})"));
        var springs = Read("List_UpgradeSpringDamper", "FrontSpringDamperPhysicsID", "Ordinal" + comparison);
        springs.UnionWith(Read("List_UpgradeSpringDamper", "RearSpringDamperPhysicsID", "Ordinal" + comparison));
        var antiSway = Read("List_UpgradeAntiSwayFront", "AntiSwayPhysicsID", "Ordinal" + comparison);
        antiSway.UnionWith(Read("List_UpgradeAntiSwayRear", "AntiSwayPhysicsID", "Ordinal" + comparison));
        var wheels = Read("Data_Car", "StockWheelID", "Id" + comparison);
        var parts = new HashSet<long>();
        var strings = new HashSet<long>();
        string? For(string table, List<string> cols) => Scope(table, cols, comparison, bodies, engines,
            motors, drivetrains, compounds, tireParts, aero, springs, antiSway, wheels, parts, strings);
        foreach (var (table, cols) in tables)
        {
            if (!table.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase)) continue;
            string? scope = For(table, cols);
            if (scope is null) continue;
            parts.UnionWith(Read(table, "Id", scope));
            strings.UnionWith(Read(table, "PartsStringId", scope));
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, cols) in tables)
        {
            if (table.StartsWith("NewProfile_", StringComparison.OrdinalIgnoreCase)) continue;
            string? scope = For(table, cols);
            if (scope is not null) result.Add(table, scope);
        }
        return CarRelatedDbGraph.Expand(db, schema, result);
    }

    private static string? Scope(string table, List<string> columns, string comparison,
        HashSet<long> bodies, HashSet<long> engines, HashSet<long> motors, HashSet<long> drivetrains,
        HashSet<long> compounds, HashSet<long> tireParts, HashSet<long> aero,
        HashSet<long> springs, HashSet<long> antiSway, HashSet<long> wheels, HashSet<long> parts,
        HashSet<long> partStrings)
    {
        bool Has(string column) => columns.Contains(column, StringComparer.OrdinalIgnoreCase);
        if (MergeVersionInfo.IsProtectedTable(table)) return null;
        if (table.Equals("Data_Car", StringComparison.OrdinalIgnoreCase)) return "Id" + comparison;
        if (table.Equals("Data_CarBody", StringComparison.OrdinalIgnoreCase) ||
            table.Equals("Data_CarBody_DummyAxle", StringComparison.OrdinalIgnoreCase))
            return Has("Id") ? $"Id IN ({In(bodies)})" : null;
        if (table.Equals("CarTrackOffsets", StringComparison.OrdinalIgnoreCase)) return Has("Id") ? "Id" + comparison : null;
        if (table.Equals("CarPartNames", StringComparison.OrdinalIgnoreCase)) return Has("Id") ? $"Id IN ({In(parts)})" : null;
        if (table.Equals("List_PartsStrings", StringComparison.OrdinalIgnoreCase)) return Has("Id") ? $"Id IN ({In(partStrings)})" : null;
        if (table.Equals("Data_Engine", StringComparison.OrdinalIgnoreCase)) return Has("EngineID") ? $"EngineID IN ({In(engines)})" : null;
        if (table.Equals("Data_Motor", StringComparison.OrdinalIgnoreCase)) return Has("MotorID") ? $"MotorID IN ({In(motors)})" : null;
        if (table.Equals("Data_Drivetrain", StringComparison.OrdinalIgnoreCase)) return Has("DrivetrainID") ? $"DrivetrainID IN ({In(drivetrains)})" : null;
        if (table.Equals("List_TireCompound", StringComparison.OrdinalIgnoreCase)) return Has("TireCompoundID") ? $"TireCompoundID IN ({In(compounds)})" : null;
        if (table.Equals("List_Wheels", StringComparison.OrdinalIgnoreCase)) return Has("Id") ? $"Id IN ({In(wheels)})" : null;
        if (table.Equals("List_UpgradeTireCompoundFictionModOverride", StringComparison.OrdinalIgnoreCase))
            return Has("PartId") ? $"PartId IN ({In(tireParts)})" : null;
        if (table.Equals("List_AeroPhysics", StringComparison.OrdinalIgnoreCase)) return Has("Ordinal")
            ? $"Ordinal{comparison} OR AeroPhysicsID IN ({In(aero)})" : $"AeroPhysicsID IN ({In(aero)})";
        if (table.Equals("List_SpringDamperPhysics", StringComparison.OrdinalIgnoreCase)) return Has("Ordinal")
            ? $"Ordinal{comparison} OR SpringDamperPhysicsID IN ({In(springs)})" : $"SpringDamperPhysicsID IN ({In(springs)})";
        if (table.Equals("List_AntiSwayPhysics", StringComparison.OrdinalIgnoreCase)) return Has("Ordinal")
            ? $"Ordinal{comparison} OR AntiSwayPhysicsID IN ({In(antiSway)})" : $"AntiSwayPhysicsID IN ({In(antiSway)})";
        if (table.StartsWith("List_UpgradeEngine", StringComparison.OrdinalIgnoreCase) && Has("EngineID") && !Has("Ordinal"))
            return $"EngineID IN ({In(engines)})";
        if (table.StartsWith("List_UpgradeMotor", StringComparison.OrdinalIgnoreCase) && Has("MotorID") && !Has("Ordinal"))
            return $"MotorID IN ({In(motors)})";
        if (table.StartsWith("List_UpgradeDrivetrain", StringComparison.OrdinalIgnoreCase) && Has("DrivetrainID") && !Has("Ordinal"))
            return $"DrivetrainID IN ({In(drivetrains)})";
        if (table.StartsWith("List_UpgradeCarBody", StringComparison.OrdinalIgnoreCase) && Has("CarBodyID") && !Has("Ordinal"))
            return $"CarBodyID IN ({In(bodies)})";
        if (Has("Ordinal")) return "Ordinal" + comparison;
        if (Has("CarId")) return Q(columns.First(c => c.Equals("CarId", StringComparison.OrdinalIgnoreCase))) + comparison;
        return null;
    }

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string In(HashSet<long> ids) => ids.Count == 0 ? "NULL" : string.Join(",", ids.Order());
    private static HashSet<long> Ids(SqliteConnection db, string sql)
    {
        var values = new HashSet<long>();
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) if (!reader.IsDBNull(0)) values.Add(Convert.ToInt64(reader.GetValue(0)));
        return values;
    }
    private static List<string> Columns(SqliteConnection db, string schema, string table)
    {
        var columns = new List<string>();
        using var cmd = db.CreateCommand(); cmd.CommandText = $"PRAGMA {schema}.table_info({Q(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }
}
