using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

// The stock quad-turbo table is shorter than the single- and twin-turbo tables.
// A mod can add the corresponding optional fields or recreate the table without its key.
internal static class QuadTurboMerge
{
    internal const string Table = "List_UpgradeEngineTurboQuad";
    static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    static readonly Dictionary<string, string> OptionalFields = new(Names)
    {
        ["MassDiff"] = "REAL",
        ["WeightDistDiff"] = "REAL",
        ["OffThrottleMomentInertia"] = "REAL",
        ["releaseOrder"] = "INTEGER",
    };

    internal sealed record Column(string Name, string Type, string? Default, bool Required, int Key, int Hidden)
    {
        internal string Affinity => Type.Equals("INT", StringComparison.OrdinalIgnoreCase) ? "INTEGER" : Type.ToUpperInvariant();
    }

    internal sealed record Alignment(string[] Columns, string[] Keys, Column[] Additions,
        Dictionary<string, string> BaselineDefaults, Dictionary<string, string> UpdatedDefaults, bool MissingKey, string Note);

    internal static Alignment? Inspect(SqliteConnection db, string table)
    {
        if (!Names.Equals(table, Table)) return null;
        var baseline = ReadColumns(db, "old", table);
        var updated = ReadColumns(db, "main", table);
        var donor = ReadColumns(db, "ov", table);
        if (!HasIdKey(baseline) || !HasIdKey(updated) ||
            !donor.TryGetValue("Id", out var donorId) || donorId.Affinity != "INTEGER" ||
            new[] { baseline, updated, donor }.Any(columns => columns.Values.Any(c => c.Hidden != 0)))
            return null;

        bool missingKey = !HasIdKey(donor);
        if (missingKey && donor.Values.Any(c => c.Key != 0)) return null;
        if (missingKey)
        {
            using var check = db.CreateCommand();
            check.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM ov.List_UpgradeEngineTurboQuad
                    GROUP BY Id HAVING count(*) > 1 OR typeof(Id) <> 'integer'
                )
                """;
            if (Convert.ToInt64(check.ExecuteScalar()) != 0) return null;
        }

        var all = new[] { baseline, updated, donor };
        var defaults = new Dictionary<string, string>(Names);
        foreach (string name in all.SelectMany(s => s.Keys).Distinct(Names))
        {
            var columns = all.Where(s => s.ContainsKey(name)).Select(s => s[name]).ToArray();
            if (columns.Select(c => c.Affinity).Distinct().Count() != 1) return null;
            if (columns.Length == all.Length) continue;
            if (!OptionalFields.TryGetValue(name, out string? type) || columns.Any(c => c.Affinity != type || c.Key != 0))
                return null;
            foreach (var column in columns)
                if (!HasSupportedDefault(column)) return null;
            if (donor.ContainsKey(name) && !updated.ContainsKey(name) && baseline.ContainsKey(name))
                return null; // The update removed a stock field that the mod still uses.
            if (!baseline.ContainsKey(name) && donor.TryGetValue(name, out var added))
                defaults.Add(name, added.Default ?? "NULL");
        }

        var additions = donor.Values.Where(c => !updated.ContainsKey(c.Name)).ToArray();
        // Use a fresh default only for mod-added fields. Keep update-only
        // columns out of the INSERT list so SQLite supplies the update's default.
        var updatedDefaults = additions.ToDictionary(c => c.Name, c => c.Default ?? "NULL", Names);
        var notes = new List<string>();
        if (additions.Length > 0) notes.Add("carrying over " + string.Join(", ", additions.Select(c => c.Name)));
        if (updated.Keys.Except(donor.Keys, Names).Any()) notes.Add("keeping update-only fields");
        if (missingKey) notes.Add("matching rows by the donor's unique integer Id");
        if (notes.Count == 0) notes.Add("matching the supported optional fields");
        return new(donor.Keys.ToArray(), new[] { donorId.Name }, additions, defaults, updatedDefaults, missingKey, string.Join("; ", notes));
    }

    static bool HasIdKey(Dictionary<string, Column> columns) =>
        columns.TryGetValue("Id", out var id) && id.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) &&
        id.Key == 1 && columns.Values.Count(c => c.Key != 0) == 1;

    static bool HasSupportedDefault(Column column) =>
        (!column.Required && (column.Default == null || column.Default.Equals("NULL", StringComparison.OrdinalIgnoreCase))) ||
        (double.TryParse(column.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number));

    static Dictionary<string, Column> ReadColumns(SqliteConnection db, string schema, string table)
    {
        using var query = db.CreateCommand();
        query.CommandText = "SELECT name,type,dflt_value,[notnull],pk,hidden FROM pragma_table_xinfo($table,$schema) ORDER BY cid";
        query.Parameters.AddWithValue("$table", table);
        query.Parameters.AddWithValue("$schema", schema);
        using var reader = query.ExecuteReader();
        var columns = new Dictionary<string, Column>(Names);
        while (reader.Read())
        {
            var column = new Column(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3) != 0, reader.GetInt32(4), reader.GetInt32(5));
            columns.Add(column.Name, column);
        }
        return columns;
    }
}
