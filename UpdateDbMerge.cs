using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public sealed record UpdateDbMergePreview(string UpdatedPath, string ModdedPath,
    string UpdatedHash, string ModdedHash, int NewRows, int ChangedRows,
    IReadOnlyList<string> KeylessTables, IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, int> AddedByTable)
{
    public string BaselinePath { get; init; } = "";
    public string BaselineHash { get; init; } = "";
    public string BaselineVersion { get; init; } = "";
    public string UpdatedVersion { get; init; } = "";
    public int DeletedRows { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<DeltaTable> Delta { get; init; } = Array.Empty<DeltaTable>();
}

public static class UpdateDbMerge
{
    public static UpdateDbMergePreview Preview(string updated, string modded, string? baseline = null)
    {
        updated = Path.GetFullPath(updated); modded = Path.GetFullPath(modded);
        if (updated.Equals(modded, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The updated and modded databases must be different files.");
        GameDbIdentity.RequireSameGame(updated, modded);
        string updatedVersion = StockDatabaseCatalog.Version(updated), version = StockDatabaseCatalog.Version(modded);
        baseline ??= StockDatabaseCatalog.Find(modded) ?? throw new InvalidOperationException(
            $"No embedded clean baseline matches stamp {version}. Select the unmodified GameDB from the SAME game version as your modded DB.");
        baseline = Path.GetFullPath(baseline);
        GameDbIdentity.RequireSameGame(modded, baseline);
        if (baseline.Equals(modded, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The clean baseline must not be your modded database.");
        if (StockDatabaseCatalog.Version(baseline) != version)
            throw new InvalidDataException("The clean baseline version stamp does not match your old modded DB. Deletions cannot be inferred safely.");
        string updatedHash = Hash(updated), moddedHash = Hash(modded), baselineHash = Hash(baseline);
        using var db = GameDbDelta.Open(updated, modded, baseline);
        using (var check = db.CreateCommand())
        {
            check.CommandText = "PRAGMA ov.application_id";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0x46483643)
                throw new InvalidDataException("This is a sparse car export, not a complete modded GameDB. Use Import car DB instead.");
        }
        var warnings = new List<string>(); var notes = new List<string>();
        var tables = GameDbDelta.Inspect(db, warnings, notes);
        if (Hash(updated) != updatedHash || Hash(modded) != moddedHash || Hash(baseline) != baselineHash)
            throw new InvalidOperationException("An input changed during comparison. Start the merge again.");
        return new(updated, modded, updatedHash, moddedHash, tables.Sum(t => t.Added), tables.Sum(t => t.Changed),
            tables.Where(t => t.Keys.Length == 0 && t.Added + t.Deleted > 0).Select(t => t.Name).ToArray(), warnings,
            tables.ToDictionary(t => t.Name, t => t.Added, StringComparer.OrdinalIgnoreCase))
        {
            BaselinePath = baseline, BaselineHash = baselineHash, BaselineVersion = version,
            UpdatedVersion = updatedVersion, DeletedRows = tables.Sum(t => t.Deleted), Notes = notes, Delta = tables
        };
    }

    public static void Run(UpdateDbMergePreview preview, string outputPath, Action<string>? log = null)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || new[] { preview.UpdatedPath, preview.ModdedPath, preview.BaselinePath }
                .Any(p => output.Equals(p, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a new output path. All three inputs stay untouched.");
        Verify(preview);
        if (preview.Warnings.Count > 0)
            throw new InvalidDataException("Incompatible schemas; no partial merge will be written: " + string.Join("; ", preview.Warnings.Take(3)));
        string partial = output + ".partial." + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = Read(preview.UpdatedPath))
            using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
            { snapshot.Open(); source.BackupDatabase(snapshot); }
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
            {
                db.Open(); GameDbDelta.Exec(db, "PRAGMA foreign_keys=OFF");
                GameDbDelta.Attach(db, "ov", preview.ModdedPath); GameDbDelta.Attach(db, "old", preview.BaselinePath);
                log?.Invoke($"Three-way merge: baseline {preview.BaselineVersion} -> update {preview.UpdatedVersion}. Applying {preview.DeletedRows:n0} deliberate baseline deletions.");
                foreach (string note in preview.Notes) log?.Invoke(note);
                GameDbDelta.Apply(db, preview.Delta, log);
                using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The merged DB failed SQLite quick_check.");
                var result = ForeignKeyCounts(db);
                using var updated = Read(preview.UpdatedPath); using var modded = Read(preview.ModdedPath);
                var original = ForeignKeyCounts(updated); var donor = ForeignKeyCounts(modded);
                // Donor-added rows may use a looser schema than the official clean DB.
                var excess = result.Where(r => r.Value > original.GetValueOrDefault(r.Key) + donor.GetValueOrDefault(r.Key) +
                    preview.AddedByTable.GetValueOrDefault(r.Key.ChildTable)).ToArray();
                if (excess.Length > 0)
                    throw new InvalidDataException("New foreign-key warnings after applying changes/deletions: " +
                        string.Join(", ", excess.Take(5).Select(r => $"{r.Key.ChildTable}:{r.Key.ParentTable} {r.Key.Columns}")));
                GameDbDelta.Exec(db, "DETACH DATABASE ov"); GameDbDelta.Exec(db, "DETACH DATABASE old");
            }
            Verify(preview);
            if (StockDatabaseCatalog.Version(partial) != preview.UpdatedVersion)
                throw new InvalidDataException("The clean update's version stamp was not preserved.");
            File.Move(partial, output);
        }
        finally { try { File.Delete(partial); } catch { } SqliteConnection.ClearAllPools(); }
    }
    private static void Verify(UpdateDbMergePreview p)
    {
        if (Hash(p.UpdatedPath) != p.UpdatedHash || Hash(p.ModdedPath) != p.ModdedHash || Hash(p.BaselinePath) != p.BaselineHash)
            throw new InvalidOperationException("An input or clean baseline changed after preview. Start the merge again.");
    }
    private static SqliteConnection Read(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path); string result = Convert.ToHexString(SHA256.HashData(stream));
        if (File.Exists(path + "-wal")) { using var wal = File.OpenRead(path + "-wal"); result += ":" + Convert.ToHexString(SHA256.HashData(wal)); }
        return result;
    }
    private sealed record ForeignKeyIdentity(string ChildTable, string ParentTable, string Columns);

    private static Dictionary<ForeignKeyIdentity, int> ForeignKeyCounts(SqliteConnection db)
    {
        // SQLite's fkid is only an index into this database's FK declarations.
        // Reordering identical declarations changes it; compare the actual
        // child/parent column mapping instead, including composite constraints.
        var counts = new Dictionary<(string Table, int Id), int>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA main.foreign_key_check";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetInt32(3));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        var result = new Dictionary<ForeignKeyIdentity, int>();
        foreach (var table in counts.Keys.Select(k => k.Table).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var definitions = new Dictionary<int, (string Parent, List<string[]> Columns)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA main.foreign_key_list(\"{table.Replace("\"", "\"\"")}\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    if (!definitions.TryGetValue(id, out var definition))
                        definition = (reader.GetString(2), new List<string[]>());
                    definition.Columns.Add(new[]
                    {
                        reader.GetString(3).ToUpperInvariant(),
                        reader.IsDBNull(4) ? "<PARENT_PRIMARY_KEY>" : reader.GetString(4).ToUpperInvariant()
                    });
                    definitions[id] = definition;
                }
            }
            foreach (var warning in counts.Where(k => k.Key.Table.Equals(table, StringComparison.OrdinalIgnoreCase)))
            {
                if (!definitions.TryGetValue(warning.Key.Id, out var definition))
                    throw new InvalidDataException($"Cannot identify a foreign-key constraint in {table}; merge validation stopped.");
                var key = new ForeignKeyIdentity(table.ToUpperInvariant(), definition.Parent.ToUpperInvariant(),
                    JsonSerializer.Serialize(definition.Columns.OrderBy(p => p[0], StringComparer.Ordinal)
                        .ThenBy(p => p[1], StringComparer.Ordinal)));
                result[key] = result.GetValueOrDefault(key) + warning.Value;
            }
        }
        return result;
    }
}
