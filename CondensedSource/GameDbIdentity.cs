using System.IO;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public enum ForzaGame { Unknown, Horizon2, Horizon5, Horizon6, Motorsport8 }

/// <summary>Game identity from independent schema signatures. VersionInfo is
/// used separately to select an exact clean reference, never as a game guess.</summary>
public static class GameDbIdentity
{
    public static string DisplayName(ForzaGame game) => game switch
    {
        ForzaGame.Horizon2 => "FH2", ForzaGame.Horizon5 => "FH5",
        ForzaGame.Horizon6 => "FH6", ForzaGame.Motorsport8 => "FM8", _ => "Unknown Forza DB"
    };

    public static ForzaGame Detect(string sqlitePath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return Detect(db);
    }

    public static ForzaGame Detect(SqliteConnection db)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT name FROM main.sqlite_schema WHERE type='table'";
            using var r = command.ExecuteReader(); while (r.Read()) tables.Add(r.GetString(0));
        }
        HashSet<string> Columns(string table)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!tables.Contains(table)) return columns;
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM pragma_table_info($t)"; cmd.Parameters.AddWithValue("$t", table);
            using var r = cmd.ExecuteReader(); while (r.Read()) columns.Add(r.GetString(0)); return columns;
        }
        var car = Columns("Data_Car"); var engine = Columns("Data_Engine"); var spring = Columns("List_UpgradeSpringDamper");
        // Sparse EV car donors can legitimately omit Data_Engine. New exports
        // retain their game in the existing SQLite header, not a game table.
        ForzaGame declared = ForzaGame.Unknown;
        using (var tag = db.CreateCommand())
        {
            tag.CommandText = "PRAGMA application_id";
            long donorTag = Convert.ToInt64(tag.ExecuteScalar());
            if (donorTag == CarRelatedDbExport.ApplicationId || donorTag == ConvertedCarReplacementDb.ApplicationId)
            {
                tag.CommandText = "PRAGMA user_version"; int value = Convert.ToInt32(tag.ExecuteScalar());
                if ((value & unchecked((int)0xFFFFFF00)) == 0x46010000 && Enum.IsDefined(typeof(ForzaGame), value & 255))
                    declared = (ForzaGame)(value & 255);
            }
        }
        ForzaGame Result(ForzaGame actual)
        {
            if (actual != ForzaGame.Unknown && declared != ForzaGame.Unknown && actual != declared)
                throw new InvalidDataException("The car donor's game tag disagrees with its schema. Re-export it from the full GameDB.");
            return actual == ForzaGame.Unknown ? declared : actual;
        }
        if (!car.Contains("Id") || !engine.Contains("EngineID")) return Result(ForzaGame.Unknown);
        if (car.Contains("DryWeight") && car.Contains("FuelTankPlacementID") && spring.Contains("CarPoint") && engine.Contains("FuelDensity-kgm3"))
            return Result(ForzaGame.Motorsport8);
        if (car.Contains("NotAvailableInAutoshow"))
        {
            if (engine.Contains("BackfireIntensityScalar") && engine.Contains("BackfireSootToFireRatioScalar")) return Result(ForzaGame.Horizon6);
            if (engine.Contains("EngineGraphingAspirationID") && spring.Contains("SteeringSettingsProfileID")) return Result(ForzaGame.Horizon5);
        }
        if (car.Contains("IsArcade") && tables.Contains("AnthemEventRaces") && tables.Contains("Campaigns") &&
            !spring.Contains("SteerMaxAngle")) return Result(ForzaGame.Horizon2);
        return Result(ForzaGame.Unknown);
    }

    internal static void RequireSameGame(string sqlitePath, string templateSqlite)
    {
        var source = Detect(sqlitePath); var template = Detect(templateSqlite);
        if (source != ForzaGame.Unknown && template != ForzaGame.Unknown && source != template)
            throw new InvalidDataException($"The edited DB is {DisplayName(source)}, but the template is {DisplayName(template)}. Choose the original SLT from the same game/build.");
    }
}
