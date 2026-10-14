#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace FH6CarEditor;

public partial class CarEditorView
{
    // Keep the exact input values (including NULL), not the embedded baseline.
    // Loading/exporting never invents a release date or changes ownership.
    sealed record CarReleaseDate(long Id, string ContentId, object Date);
    readonly Dictionary<long, CarReleaseDate> _loadedCarReleaseDates = new();
    bool HasCarReleaseDates => HasAutoshow && new[] { "Id", "ContentId", "ContentType", "ReleaseDateUTC" }
        .All(column => HasColumn("ContentOffersMapping", column));

    static string ReleaseDateText(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    List<CarReleaseDate> ReadCarReleaseDates() => Query(
        "SELECT Id,ContentId,ReleaseDateUTC FROM ContentOffersMapping WHERE ContentType=1 ORDER BY Id")
        .Select(row => new CarReleaseDate(Convert.ToInt64(row["Id"]), ReleaseDateText(row["ContentId"]), row["ReleaseDateUTC"]))
        .ToList();

    void CaptureCarReleaseDates()
    {
        _loadedCarReleaseDates.Clear();
        if (HasCarReleaseDates)
            foreach (var row in ReadCarReleaseDates()) _loadedCarReleaseDates.Add(row.Id, row);
    }

    void SetCarReleaseDateUnavailable(string reason)
    {
        OptRemoveReleaseDate.IsEnabled = false;
        OptRemoveReleaseDate.IsThreeState = false;
        OptRemoveReleaseDate.IsChecked = false;
        CarReleaseDateStatus.Text = reason;
    }

    bool TrySelectedCarReleaseDates(out List<CarReleaseDate> rows, out string reason)
    {
        rows = new();
        reason = "Select a car to see its scheduled release date.";
        if (_conn == null || CarList.SelectedItems.Count == 0) return false;
        if (!HasCarReleaseDates)
        {
            reason = "No supported FH6 car release-date mapping in this DB.";
            return false;
        }
        var carIds = CarList.SelectedItems.Cast<CarItem>()
            .Select(car => car.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        rows = ReadCarReleaseDates().Where(row => carIds.Contains(row.ContentId)).ToList();
        if (rows.Select(row => row.ContentId).Distinct().Count() != carIds.Count)
        {
            reason = carIds.Count == 1 ? "This car has no scheduled release mapping."
                : "Select cars that all have a release mapping to edit dates together.";
            return false;
        }
        if (rows.Any(row => !_loadedCarReleaseDates.TryGetValue(row.Id, out var loaded) || loaded.ContentId != row.ContentId))
        {
            reason = "Release mappings changed since load; reload the DB before editing their dates.";
            return false;
        }
        return true;
    }

    void ShowCarReleaseDate()
    {
        if (!TrySelectedCarReleaseDates(out var rows, out string reason))
        {
            SetCarReleaseDateUnavailable(reason);
            return;
        }
        string[] dates = rows.Select(row => ReleaseDateText(row.Date)).Distinct(StringComparer.Ordinal).ToArray();
        bool clear = dates.All(string.IsNullOrEmpty);
        bool restorePossible = rows.Any(row => !string.IsNullOrEmpty(ReleaseDateText(_loadedCarReleaseDates[row.Id].Date)));
        OptRemoveReleaseDate.IsThreeState = dates.Any(string.IsNullOrEmpty) && !clear;
        OptRemoveReleaseDate.IsChecked = clear ? true : dates.Any(string.IsNullOrEmpty) ? null : false;
        OptRemoveReleaseDate.IsEnabled = !clear || restorePossible;
        CarReleaseDateStatus.Text = clear ? restorePossible
            ? "No scheduled date · uncheck to restore loaded date(s)."
            : "No scheduled release date in the loaded DB."
            : dates.Length == 1 ? "Release (UTC): " + dates[0]
            : "Different release dates · check to clear selected cars' dates.";
    }

    void RemoveReleaseDate_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRunning) return;
        if (!TrySelectedCarReleaseDates(out var rows, out string reason))
        {
            SetCarReleaseDateUnavailable(reason);
            return;
        }
        // The indeterminate state only describes a mixed selection.
        if (OptRemoveReleaseDate.IsChecked is not bool remove) { ShowCarReleaseDate(); return; }
        bool savepoint = false;
        try
        {
            Exec("SAVEPOINT edit_car_release_dates"); savepoint = true;
            foreach (var row in rows)
            {
                object date = remove ? "" : _loadedCarReleaseDates[row.Id].Date;
                if (Exec(@"UPDATE ContentOffersMapping SET ReleaseDateUTC=?
                           WHERE Id=? AND ContentType=1 AND ContentId=? AND ReleaseDateUTC IS ?",
                         date, row.Id, row.ContentId, row.Date) != 1)
                    throw new InvalidOperationException("A selected release mapping changed; no dates were updated.");
            }
            Exec("RELEASE edit_car_release_dates"); savepoint = false;
            string subject = CarList.SelectedItems.Count == 1 ? _car.Media : $"{CarList.SelectedItems.Count} selected cars";
            Log($"{subject} · {(remove ? "removed scheduled release date" : "restored loaded release date")} ({rows.Count} mapping row(s), working copy)", "ok");
            if (remove) Log("This edits the local release date only; DLC ownership and other game checks are unchanged. Export and test in-game.", "info");
        }
        catch (Exception ex)
        {
            if (savepoint) try { Exec("ROLLBACK TO edit_car_release_dates"); Exec("RELEASE edit_car_release_dates"); } catch { }
            Log("release-date update failed: " + ex.Message, "err");
        }
        finally { ShowCarReleaseDate(); }
    }
}
