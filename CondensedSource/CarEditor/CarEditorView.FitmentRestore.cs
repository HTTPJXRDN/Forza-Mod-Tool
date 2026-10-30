#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FH6CarEditor;

public partial class CarEditorView
{
    static readonly (string Table, string Owner)[] FitmentCatalogTables =
    {
        ("List_UpgradeRimSizeFront", "Ordinal"), ("List_UpgradeRimSizeRear", "Ordinal"),
        ("List_UpgradeCarBodyTireWidthFront", "CarBodyId"), ("List_UpgradeCarBodyTireWidthRear", "CarBodyId"),
        ("List_UpgradeCarBodyTireAspectRatioFront", "CarBodyId"), ("List_UpgradeCarBodyTireAspectRatioRear", "CarBodyId"),
        ("List_UpgradeCarBodyTrackSpacingFront", "CarBodyId"), ("List_UpgradeCarBodyTrackSpacingRear", "CarBodyId")
    };
    readonly Dictionary<string, ILookup<long, Dictionary<string, object>>> _loadedFitmentCatalog = new(StringComparer.OrdinalIgnoreCase);
    HashSet<long> _loadedFitmentCars = new(), _loadedFitmentBodies = new();
    sealed record FitmentRestoreGroup(string Table, string Owner, long Id, List<Dictionary<string, object>> Rows, bool Embedded);

    void CaptureLoadedFitmentCatalog()
    {
        _loadedFitmentCatalog.Clear();
        _loadedFitmentCars = Query("SELECT Id FROM main.Data_Car").Select(r => Convert.ToInt64(r["Id"])).ToHashSet();
        _loadedFitmentBodies = _schema.ContainsKey("Data_CarBody")
            ? Query("SELECT Id FROM main.Data_CarBody").Select(r => Convert.ToInt64(r["Id"])).ToHashSet() : new();
        foreach (var (table, owner) in FitmentCatalogTables)
            if (_schema.ContainsKey(table))
                _loadedFitmentCatalog[table] = Query($"SELECT * FROM main.\"{table}\"")
                    .ToLookup(r => Convert.ToInt64(r[owner]));
    }

    FitmentRestoreGroup FitmentBaseline(string table, string owner, long id)
    {
        bool embedded = _stockReferenceAttached && ReferenceHasCar(_car.Id);
        long source = id, shift = 0;
        if (embedded && owner != "Ordinal" &&
            (ScalarL("SELECT COUNT(*) FROM stockref.List_UpgradeCarBody WHERE Ordinal=? AND CarBodyID=?", _car.Id, id) ?? 0) == 0)
        {
            // Tool-created bodies clone the stock body's upgrade rows into a
            // separate 100-ID block. Restore that same catalogue and ID mapping.
            var stockBody = Query("SELECT CarBodyID FROM stockref.List_UpgradeCarBody WHERE Ordinal=? AND IsStock=1", _car.Id);
            long baseBody = _car.Id * 1000;
            if (stockBody.Count == 1 && Convert.ToInt64(stockBody[0]["CarBodyID"]) == baseBody &&
                id > baseBody && id < baseBody + 10 &&
                (ScalarL("SELECT COUNT(*) FROM main.List_UpgradeCarBody WHERE Ordinal=? AND CarBodyID=? AND Id=? AND IsStock=0", _car.Id, id, FitId(id, 0)) ?? 0) == 1)
            { source = baseBody; shift = (id - baseBody) * 100; }
            else embedded = false;
        }
        List<Dictionary<string, object>> rows;
        if (embedded)
        {
            if (!TableExists("stockref", table)) throw new InvalidDataException("The matching stock reference is missing " + table + ". No reset was kept.");
            rows = Query($"SELECT * FROM stockref.\"{table}\" WHERE \"{owner}\"=? ORDER BY Id", source);
            foreach (var row in rows)
            {
                if (shift != 0 && (Convert.ToInt64(row["Id"]) < source || Convert.ToInt64(row["Id"]) >= source + 100))
                    throw new InvalidDataException("The stock fitment IDs cannot be mapped safely to this added body.");
                row["Id"] = checked(Convert.ToInt64(row["Id"]) + shift);
                row[owner] = id;
            }
        }
        else
        {
            if (!_loadedFitmentCatalog.TryGetValue(table, out var loaded) || !(owner == "Ordinal" ? _loadedFitmentCars : _loadedFitmentBodies).Contains(id))
                throw new InvalidDataException("No stock or loaded fitment baseline exists for " + table + " / " + id + ". No reset was kept.");
            rows = loaded[id].Select(r => new Dictionary<string, object>(r, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        foreach (var row in rows)
        {
            if (!_schema[table].SetEquals(row.Keys)) throw new InvalidDataException(table + ": the fitment schema differs from its baseline. No reset was kept.");
            if ((ScalarL($"SELECT COUNT(*) FROM main.\"{table}\" WHERE Id=? AND \"{owner}\"<>?", row["Id"], id) ?? 0) != 0)
                throw new InvalidDataException(table + ": a factory part ID is used by another car/body. No reset was kept.");
        }
        return new(table, owner, id, rows, embedded);
    }

    void RestoreFitmentCatalog(long[] bodies)
    {
        var groups = new List<FitmentRestoreGroup>();
        foreach (var (table, owner) in FitmentCatalogTables)
        {
            if (!_schema.ContainsKey(table)) continue;
            foreach (long id in owner == "Ordinal" ? new[] { _car.Id } : bodies)
                groups.Add(FitmentBaseline(table, owner, id));
        }
        foreach (var group in groups)
        {
            var expected = group.Rows.ToDictionary(r => Convert.ToInt64(r["Id"]));
            var current = Query($"SELECT * FROM main.\"{group.Table}\" WHERE \"{group.Owner}\"=?", group.Id)
                .ToDictionary(r => Convert.ToInt64(r["Id"]));
            // IsStock=0 means an optional upgrade, not a modded row. Preserve
            // every native upgrade ID so existing saved part references survive.
            foreach (long id in current.Keys.Where(id => !expected.ContainsKey(id)))
                Exec($"DELETE FROM main.\"{group.Table}\" WHERE Id=? AND \"{group.Owner}\"=?", id, group.Id);
            foreach (var (id, row) in expected)
            {
                if (!current.TryGetValue(id, out var old)) { InsertClonedRow(group.Table, row); continue; }
                if (row.All(p => Equals(old[p.Key], p.Value))) continue;
                var columns = row.Keys.Where(c => !c.Equals("Id", StringComparison.OrdinalIgnoreCase)).ToArray();
                string assignments = string.Join(',', columns.Select(c => "\"" + c.Replace("\"", "\"\"") + "\"=?"));
                Exec($"UPDATE main.\"{group.Table}\" SET {assignments} WHERE Id=?", columns.Select(c => row[c]).Append((object)id).ToArray());
            }
        }
        if (groups.Any(g => !g.Embedded)) Log("Fitment without an embedded baseline was restored to the values loaded with this DB.", "info");
    }
}
