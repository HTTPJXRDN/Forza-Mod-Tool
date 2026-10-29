#if PROFILE_UNLOCK_TESTING
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

// An editor policy, not a change to the game's catalog or saved achievements.
// Keep every definition available to the serializer and historical point totals.
internal static class CollectionJournalCarPolicy
{
    static readonly Lazy<HashSet<int>> Reference = new(ReadReference);
    internal static IReadOnlySet<int> ReferenceDlcCars => Reference.Value;
    internal static bool Excluded(CollectionJournalCatalog.Challenge challenge)
        => challenge.Category == GarageCollectionJournalState.CategoryId && challenge.CollectionCarOrdinal > 0
            && (ProfileGameData.Current?.DlcCarOrdinals ?? ReferenceDlcCars).Contains(challenge.CollectionCarOrdinal);

    internal static string GameDbPath(string objectModelPath)
        => Path.Combine(Path.GetDirectoryName(objectModelPath)!, "stripped", "gamedbRC.slt");

    internal static IReadOnlySet<int> ReadInstalled(string objectModelPath)
    {
        // Use the clean database alongside these definitions, never MediaPC's
        // modded autoshow/ownership flags or a database from a different install.
        using var source = GameDbSqliteBridge.Materialize(GameDbPath(objectModelPath));
        var cars = ReadSqlite(source.SqlitePath);
        source.VerifySourceUnchanged();
        return cars;
    }

    static HashSet<int> ReadReference()
    {
        string path = Path.Combine(TempWorkspace.Root, $"forza_journal_dlc_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("FH6LocalCryptoTool.gamedbRC.stock.661353983.sqlite.gz")
                ?? throw new InvalidDataException("The reference FH6 DLC mappings are missing."))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new FileStream(path, FileMode.CreateNew)) gzip.CopyTo(output);
            return ReadSqlite(path);
        }
        finally { GarageViewer.GarageCarCatalog.Cleanup(path); }
    }

    static HashSet<int> ReadSqlite(string path)
    {
        try
        {
            using var db = GarageViewer.GarageCarCatalog.Open(path, SqliteOpenMode.ReadOnly);
            // ContentType 1 identifies a car ordinal. Data_Car.ContentId is a
            // content-group number (including ordinary base-game cars), not DLC.
            using var command = db.CreateCommand();
            command.CommandText = "SELECT m.ContentId FROM ContentOffersMapping m "
                + "JOIN ContentOffers o ON o.Id=m.OfferId WHERE m.ContentType=1 AND m.OfferId>0";
            using var reader = command.ExecuteReader();
            var cars = new HashSet<int>();
            int rows = 0;
            while (reader.Read())
            {
                if (++rows > 100_000 || !int.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int car) || car <= 0)
                    throw new InvalidDataException("The FH6 DLC car mappings are not recognized.");
                cars.Add(car);
            }
            return cars;
        }
        catch (SqliteException error) { throw new InvalidDataException("The FH6 DLC offer mappings could not be read.", error); }
    }
}
#endif
