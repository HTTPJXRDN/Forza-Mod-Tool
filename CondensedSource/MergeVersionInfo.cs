using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Ordinary merges/imports preserve the destination's version metadata exactly.</summary>
internal static class MergeVersionInfo
{
    internal static bool IsProtectedTable(string name) =>
        name.Equals("VersionInfo", StringComparison.OrdinalIgnoreCase);

    // Preserve schema, SQLite storage types, NULLs and duplicate counts. Absence is
    // also meaningful: a sparse destination must not acquire a donor's build stamp.
    internal static string Capture(SqliteConnection db, SqliteTransaction? tx = null)
    {
        var objects = new List<string>();
        string? table = null;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT type,name,tbl_name,sql FROM main.sqlite_master " +
                "WHERE name='VersionInfo' COLLATE NOCASE OR tbl_name='VersionInfo' COLLATE NOCASE " +
                "ORDER BY type,name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                objects.Add(Row(reader));
                if (reader.GetString(0) is "table" or "view") table = reader.GetString(1);
            }
        }
        var rows = new List<string>();
        if (table is not null)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT * FROM main.\"" + table.Replace("\"", "\"\"") + "\"";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(Row(reader));
        }
        rows.Sort(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new { Objects = objects, Rows = rows });
    }

    internal static void VerifyPreserved(SqliteConnection db, string expected, SqliteTransaction? tx = null)
    {
        if (Capture(db, tx) != expected)
            throw new InvalidDataException("Merge attempted to change VersionInfo. Only Merge Updated DB " +
                "may advance the database version. No merged changes were committed.");
    }

    private static string Row(SqliteDataReader reader) => JsonSerializer.Serialize(
        Enumerable.Range(0, reader.FieldCount).Select(i =>
        {
            object value = reader.GetValue(i);
            return value switch
            {
                DBNull => new[] { "null", "" },
                byte[] blob => new[] { "blob", Convert.ToBase64String(blob) },
                double number => new[] { "real", number.ToString("R", CultureInfo.InvariantCulture) },
                long number => new[] { "integer", number.ToString(CultureInfo.InvariantCulture) },
                _ => new[] { "text", Convert.ToString(value, CultureInfo.InvariantCulture)! }
            };
        }));
}
