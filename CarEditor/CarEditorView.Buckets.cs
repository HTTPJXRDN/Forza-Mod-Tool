using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FH6CarEditor;

public partial class CarEditorView
{
    IReadOnlyList<CarBucketChoice> _carBuckets = Array.Empty<CarBucketChoice>();
    bool _updatingCarBucket;
    bool HasCarBuckets => HasColumn("CarBuckets", "Id") && HasColumn("CarBuckets", "Name") &&
        HasColumn("Data_Car_Buckets", "CarId") && HasColumn("Data_Car_Buckets", "CarBucket");

    void ResetCarBuckets()
    {
        _updatingCarBucket = true;
        try
        {
            _carBuckets = Array.Empty<CarBucketChoice>();
            CarBucketSelector.ItemsSource = null;
            CarBucketSelector.IsEnabled = SetCarBucketButton.IsEnabled = false;
            CarBucketStatus.Text = "Select a car to see its bucket.";
        }
        finally { _updatingCarBucket = false; }
    }

    void LoadCarBuckets()
    {
        ResetCarBuckets();
        if (!HasCarBuckets) return;
        try
        {
            string parent = HasColumn("CarBuckets", "ParentBucketId") ? "ParentBucketId" : "NULL AS ParentBucketId";
            var rows = Query($"SELECT Id,Name,{parent} FROM CarBuckets ORDER BY Id");
            _carBuckets = CarBucketCatalog.ReadForGame(rows.Select(r => (Convert.ToInt64(r["Id"]),
                Convert.ToString(r["Name"], CultureInfo.InvariantCulture) ?? "",
                r["ParentBucketId"] == null ? (long?)null : Convert.ToInt64(r["ParentBucketId"]))), _origPath, IsFh2 || IsFh5);
            CarBucketSelector.ItemsSource = _carBuckets;
        }
        catch (Exception ex)
        {
            ResetCarBuckets();
            Log("car buckets unavailable: " + ex.Message, "warn");
        }
    }

    void ShowCarBucket()
    {
        _updatingCarBucket = true;
        try
        {
            CarBucketSelector.SelectedItem = null;
            CarBucketSelector.IsEnabled = SetCarBucketButton.IsEnabled = false;
            var cars = CarList.SelectedItems.Cast<CarItem>().ToArray();
            if (_conn == null || cars.Length == 0) { CarBucketStatus.Text = "Select a car to see its bucket."; return; }
            if (!HasCarBuckets || _carBuckets.Count == 0) { CarBucketStatus.Text = "No supported car-bucket tables in this DB."; return; }
            var ids = cars.Select(c => c.Id).ToArray();
            var rows = Query($"SELECT CarId,CarBucket FROM Data_Car_Buckets WHERE CarId IN ({SqlSlots(ids.Length)})", SqlValues(ids));
            // Only edit existing mappings. Inserting incomplete traffic/custom rows
            // could require additional schema-specific fields and defaults.
            if (rows.Count != cars.Length) { CarBucketStatus.Text = "One or more selected cars have no bucket mapping."; return; }
            var buckets = rows.Select(r => r["CarBucket"] == null ? (long?)null : Convert.ToInt64(r["CarBucket"])).Distinct().ToArray();
            CarBucketSelector.IsEnabled = !_batchRunning;
            if (buckets.Length == 1)
            {
                var choice = _carBuckets.FirstOrDefault(b => b.Id == buckets[0]);
                CarBucketSelector.SelectedItem = choice;
                CarBucketStatus.Text = choice != null ? $"Current: {choice.Name} · ID {choice.Id}" :
                    $"Current ID: {buckets[0]?.ToString(CultureInfo.InvariantCulture) ?? "NULL"} (not listed).";
            }
            else CarBucketStatus.Text = "Different buckets · choose one for all selected cars.";
        }
        finally { _updatingCarBucket = false; }
        UpdateCarBucketButton();
    }

    void UpdateCarBucketButton() => SetCarBucketButton.IsEnabled = _conn != null && !_batchRunning &&
        CarList.SelectedItems.Count > 0 && CarBucketSelector.IsEnabled &&
        CarBucketSelector.SelectedItem is CarBucketChoice { CanAssign: true };

    void CarBucket_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingCarBucket && SetCarBucketButton != null) UpdateCarBucketButton();
    }

    void SetCarBucket_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRunning || _conn == null || !HasCarBuckets || !CarBucketSelector.IsEnabled ||
            CarBucketSelector.SelectedItem is not CarBucketChoice { CanAssign: true } bucket ||
            !_carBuckets.Contains(bucket)) return;
        long[] ids = CarList.SelectedItems.Cast<CarItem>().Select(c => c.Id).ToArray();
        if (ids.Length == 0) return;
        bool savepoint = false;
        try
        {
            Exec("SAVEPOINT edit_car_buckets"); savepoint = true;
            if ((ScalarL("SELECT COUNT(*) FROM CarBuckets WHERE Id=?", bucket.Id) ?? 0) != 1)
                throw new InvalidOperationException("The selected bucket definition changed. Reload the database.");
            foreach (long id in ids)
                if (Exec("UPDATE Data_Car_Buckets SET CarBucket=? WHERE CarId=?", bucket.Id, id) != 1)
                    throw new InvalidOperationException("A selected car has no unique bucket mapping; no buckets were changed.");
            Exec("RELEASE edit_car_buckets"); savepoint = false;
            MarkModified(ids);
            Log($"set bucket to {bucket.Name} (ID {bucket.Id}) for {ids.Length} selected car(s), working copy", "ok");
        }
        catch (Exception ex)
        {
            if (savepoint) try { Exec("ROLLBACK TO edit_car_buckets"); Exec("RELEASE edit_car_buckets"); } catch { }
            Log("car bucket update failed: " + ex.Message, "err");
        }
        finally { ShowCarBucket(); }
    }

}
