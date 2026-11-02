using System.IO;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool.GarageViewer;

internal static class GarageCatalogSource
{
    internal static GarageCarCatalog Load(string? preferred, IEnumerable<string> gameRoots, out string message, IReadOnlyCollection<long>? ownedCars = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try { var catalog = GarageCarCatalog.Load(preferred); message = "Selected GameDB"; return catalog; }
            catch (Exception ex) when (ReadFailure(ex))
            { message = "Selected GameDB unavailable. Choose it again or use Auto-find GameDB."; return GarageCarCatalog.Embedded(); }
        }
        var candidates = Candidates(gameRoots).ToArray();
        if (candidates.Length == 1)
        {
            try { var catalog = GarageCarCatalog.Load(candidates[0]); message = "Installed GameDB detected"; return catalog; }
            catch (Exception ex) when (ReadFailure(ex))
            { message = "Installed GameDB could not be read. Using built-in car names; choose GameDB to retry."; return GarageCarCatalog.Embedded(); }
        }
        if (candidates.Length > 1 && ownedCars is { Count: > 0 })
        {
            var owned = ownedCars.ToHashSet();
            var loaded = new List<(GarageCarCatalog Catalog, int Matches)>();
            try
            {
                foreach (string path in candidates)
                {
                    try
                    {
                        var catalog = GarageCarCatalog.Load(path);
                        loaded.Add((catalog, catalog.Cars.Count(c => owned.Contains(c.Id))));
                    }
                    catch (Exception ex) when (ReadFailure(ex)) { }
                }
                int most = loaded.Select(c => c.Matches).DefaultIfEmpty().Max();
                var best = loaded.Where(c => c.Matches == most && most > 0).ToArray();
                if (best.Length == 1)
                {
                    var selected = best[0].Catalog;
                    loaded.RemoveAll(c => ReferenceEquals(c.Catalog, selected));
                    message = "Installed GameDB matched to garage"; return selected;
                }
            }
            finally { foreach (var item in loaded) item.Catalog.Dispose(); }
        }
        message = candidates.Length > 1 ? "Several FH6 installs found. Choose the GameDB used by this save."
            : "Using built-in car names. Choose GameDB for added cars.";
        return GarageCarCatalog.Embedded();
    }

    internal static IEnumerable<string> Candidates(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            string? selected = null;
            try
            {
                string modded = Path.GetFullPath(Path.Combine(root, "MediaPC", "stripped", "gamedbRC.slt"));
                string native = Path.GetFullPath(Path.Combine(root, "media", "stripped", "gamedbRC.slt"));
                selected = File.Exists(modded) ? modded : File.Exists(native) ? native : null;
            }
            catch (Exception ex) when (ReadFailure(ex)) { }
            if (selected != null && seen.Add(selected)) yield return selected;
        }
    }

    static bool ReadFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or NotSupportedException or SqliteException or System.Security.SecurityException;
}
