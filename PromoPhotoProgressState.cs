#if PROFILE_UNLOCK_TESTING
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using static FH6LocalCryptoTool.ObjectModelSource;

namespace FH6LocalCryptoTool;

/// <summary>Horizon Promo's native per-car photographed set, not photo images or uploaded UGC.</summary>
internal static class PromoPhotoProgressState
{
    internal const ulong CategoryId = 0x075135FB3A14D21EUL;
    internal sealed record Change(byte[]? Database, int[] Cars)
    {
        internal bool Changed => Cars.Length > 0;
    }

    // The reference catalog's generated [car N] suffix is machine metadata, not
    // user-entered/localized text. All 663 pairs were checked against the game's
    // CarsToItems map and unrestricted IsPromo/PhotoType=Car objectives.
    internal static CollectionJournalCatalog.Challenge Reference(CollectionJournalCatalog.Challenge challenge)
    {
        if (challenge.Category != CategoryId) return challenge;
        var match = Regex.Match(challenge.Name, @" \[car ([1-9][0-9]{0,8})\]$");
        if (challenge.Objectives.Length != 1 || challenge.Objectives[0].Kind != "TakePhotoObjective" ||
            !match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int car))
            throw new InvalidDataException("Reference Promo mapping is missing.");
        return challenge with { PromoCarOrdinal = car };
    }

    internal static int ReadCar(XElement definition)
    {
        var objectives = Prop(definition, "Objectives").Elements("element").ToArray();
        if (objectives.Length != 1) return 0;
        var objective = objectives[0];
        if (Value(objective, "ObjectiveType") != "TakePhotoObjective" || Value(objective, "IsPromo") != "True" ||
            Value(objective, "PhotoType") != "Car" || Value(objective, "CarManufacturer") != "-1" || Value(objective, "CarBucket") != "-1" ||
            new[] { "LocalPlayer", "AirborneRequired", "DriftingRequired", "BurnoutRequired", "HasValidMinSpeedThreshold" }.Any(key => Value(objective, key) != "False") ||
            Value(objective, "SubjectName") != "" || !int.TryParse(Value(objective, "CarOrdinal"), NumberStyles.None, CultureInfo.InvariantCulture, out int car) || car <= 0)
            return 0;
        return car;
    }

    internal static bool TrySync(ProfileContainer container, IEnumerable<CollectionJournalCatalog.Challenge> selected,
        out Change change, out string reason)
    {
        change = new(null, Array.Empty<int>()); reason = "";
        var promo = selected.Where(c => c.Category == CategoryId).ToArray();
        if (promo.Length == 0) return true;
        if (promo.Any(c => c.PromoCarOrdinal <= 0))
        { reason = "A selected Promo entry has no verified car mapping; nothing was changed."; return false; }
        string path = Path.Combine(TempWorkspace.Root, "promo-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            // Only a private scratch DB is opened for writing. No input save or
            // installed game file is opened here. Only missing rows are inserted.
            File.WriteAllBytes(path, container.Database);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                db.Open();
                if (!Recognized(db))
                { reason = "Horizon Promo's photographed-car table is not safely recognized; nothing was changed."; return false; }
                var before = Cars(db);
                int[] missing = promo.Select(c => c.PromoCarOrdinal).Distinct().Except(before).Order().ToArray();
                if (missing.Length == 0) return true; // Preserve the original DB bytes on a no-op.
                using (var transaction = db.BeginTransaction())
                {
                    foreach (int car in missing)
                    {
                        using var insert = db.CreateCommand(); insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO PhotoCaptures (CarOrdinal) VALUES ($car)";
                        insert.Parameters.AddWithValue("$car", car);
                        if (insert.ExecuteNonQuery() != 1) throw new InvalidDataException("Promo insert was not applied exactly once.");
                    }
                    if (!Cars(db, transaction).SetEquals(before.Concat(missing))) throw new InvalidDataException("Promo set verification failed.");
                    transaction.Commit();
                }
                using var check = db.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.Ordinal)) throw new InvalidDataException("Promo database integrity check failed.");
                change = new(null, missing);
            }
            byte[] bytes = File.ReadAllBytes(path);
            container.Database = bytes;
            change = change with { Database = bytes };
            return true;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        { change = new(null, Array.Empty<int>()); reason = "Horizon Promo could not be safely updated: " + error.Message; return false; }
        finally
        {
            foreach (string file in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static bool Recognized(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='PhotoCaptures' AND type='table'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 1) return false;
        cmd.CommandText = "PRAGMA table_xinfo('PhotoCaptures')";
        using (var reader = cmd.ExecuteReader())
            if (!reader.Read() || reader.GetString(1) != "CarOrdinal" || !reader.GetString(2).Equals("INTEGER", StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(5) != 1 || reader.GetInt64(6) != 0 || reader.Read()) return false;
        // A user-modified DB must not turn an otherwise scoped insert into writes
        // to other tables through a trigger (including a differently-cased name).
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND tbl_name='PhotoCaptures' COLLATE NOCASE";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
    }

    static HashSet<int> Cars(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "SELECT CarOrdinal FROM PhotoCaptures";
        using var reader = cmd.ExecuteReader(); var cars = new HashSet<int>();
        while (reader.Read())
        {
            long value = reader.GetInt64(0);
            if (value <= 0 || value > int.MaxValue || !cars.Add((int)value) || cars.Count > 100_000) throw new InvalidDataException("Invalid photographed-car set.");
        }
        return cars;
    }
}
#endif
