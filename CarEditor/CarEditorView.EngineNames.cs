#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace FH6CarEditor;

public partial class CarEditorView
{
    sealed record StockEngineName(long Id, string Media, string Original, string Label);
    readonly Dictionary<long, StockEngineName> _stockEngineNames = new();
    readonly Dictionary<long, string> _engineDisplayNames = new();

    void ResetStockEngineNames()
    {
        _stockEngineNames.Clear();
        _engineDisplayNames.Clear();
        UpdateEngineDisplayNames();
        LabelStockEnginesBtn.IsEnabled = RestoreStockEngineNamesBtn.IsEnabled = false;
        StockEngineNamesStatus.Text = "Engine labels require a matching stock reference.";
    }

    bool HasStockEngineNameSchema()
    {
        if (_conn == null || !_stockReferenceAttached ||
            !new[] { "EngineID", "EngineName", "MediaName" }.All(c => HasColumn("Data_Engine", c))) return false;
        foreach (var (table, required) in new[]
        {
            ("Data_Engine", new[] { "EngineID", "EngineName", "MediaName" }),
            ("List_UpgradeEngine", new[] { "EngineID", "Ordinal", "IsStock" }),
            ("Data_Car", new[] { "Id", "MediaName", "Year" })
        })
        {
            if (!TableExists("stockref", table)) return false;
            var columns = Query($"PRAGMA stockref.table_info(\"{table}\")")
                .Select(r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!required.All(columns.Contains)) return false;
        }
        return true;
    }

    void LoadStockEngineNames()
    {
        ResetStockEngineNames();
        try
        {
            if (!HasStockEngineNameSchema()) return;
            // Display donorless engines using only their original generic name,
            // even if an imported mod prefixed their EngineName with a guessed car.
            foreach (var row in Query(@"SELECT e.EngineID,se.EngineName
                                        FROM main.Data_Engine e JOIN stockref.Data_Engine se
                                          ON se.EngineID=e.EngineID AND se.MediaName=e.MediaName"))
            {
                string original = row["EngineName"] as string;
                _engineDisplayNames[Convert.ToInt64(row["EngineID"])] =
                    string.IsNullOrWhiteSpace(original) ? "(unnamed engine)" : original;
            }
            // The clean reference identifies the ORIGINAL donor, even after a
            // working DB has had conversions, swaps or custom display labels.
            var rows = Query(@"SELECT e.EngineID,e.MediaName currentMedia,se.MediaName referenceMedia,
                                      se.EngineName originalName,c.Id donorId,c.MediaName donorMedia,c.Year donorYear
                               FROM stockref.Data_Engine se
                               JOIN stockref.List_UpgradeEngine l ON l.EngineID=se.EngineID AND l.IsStock=1
                               JOIN stockref.Data_Car c ON c.Id=l.Ordinal
                               JOIN main.Data_Engine e ON e.EngineID=se.EngineID
                               ORDER BY e.EngineID,c.Id");
            foreach (var group in rows.GroupBy(r => Convert.ToInt64(r["EngineID"])))
            {
                // Never pick an arbitrary donor if a future reference shares a
                // stock EngineID between cars. No generic/swap-only inference.
                if (group.Count() != 1) continue;
                var row = group.Single();
                string media = row["referenceMedia"] as string;
                string original = row["originalName"] as string;
                string donorMedia = row["donorMedia"] as string;
                if (string.IsNullOrWhiteSpace(media) || string.IsNullOrWhiteSpace(donorMedia) ||
                    !string.Equals(media, row["currentMedia"] as string, StringComparison.Ordinal)) continue;
                long donorId = Convert.ToInt64(row["donorId"]);
                long year = !IsFh6 ? Convert.ToInt64(row["donorYear"] ?? 0L) : CarDisplayNames.Year(donorId, donorMedia, Convert.ToInt64(row["donorYear"] ?? 0L));
                string car = Naming.Friendly(donorMedia);
                string donor = (year > 0 ? year.ToString(CultureInfo.InvariantCulture) + " " : "") + car;
                if (IsFh6 && CarDisplayNames.IsInitialDrive(donorId, donorMedia)) donor += " (Initial Drive)";
                // Some legitimate stock engines have no description at all.
                // Their verified original donor is still a useful, factual name;
                // do not invent an engine type/displacement or a trailing dash.
                string label = string.IsNullOrWhiteSpace(original) ? donor : donor + " - " + original.Trim();
                _engineDisplayNames[group.Key] = label;
                _stockEngineNames.Add(group.Key, new(group.Key, media, original, label));
            }
            UpdateEngineDisplayNames();
            LabelStockEnginesBtn.IsEnabled = RestoreStockEngineNamesBtn.IsEnabled = _stockEngineNames.Count > 0;
            StockEngineNamesStatus.Text = $"{_stockEngineNames.Count} original stock engines · shared swap labels · export to keep.";
        }
        catch (Exception ex)
        {
            ResetStockEngineNames();
            Log("stock engine names unavailable: " + ex.Message, "warn");
        }
    }

    void UpdateEngineDisplayNames() => _engines = _engines.Select(e => e with
    {
        DisplayName = _engineDisplayNames.TryGetValue(e.Id, out string label) ? label : null
    }).ToList();

    void LabelStockEngines_Click(object sender, RoutedEventArgs e) => ApplyStockEngineNames(false);
    void RestoreStockEngineNames_Click(object sender, RoutedEventArgs e) => ApplyStockEngineNames(true);

    void ApplyStockEngineNames(bool restore)
    {
        if (_batchRunning || _conn == null) return;
        LoadStockEngineNames();
        if (_stockEngineNames.Count == 0)
        {
            Log("stock engine naming requires a matching clean reference and verified donor mappings", "warn");
            return;
        }
        bool savepoint = false;
        int changed = 0;
        try
        {
            Exec("SAVEPOINT stock_engine_names"); savepoint = true;
            foreach (var engine in _stockEngineNames.Values)
            {
                string name = restore ? engine.Original : engine.Label;
                changed += Exec(@"UPDATE main.Data_Engine SET EngineName=?
                                  WHERE EngineID=? AND MediaName=? AND EngineName IS NOT ?",
                                name, engine.Id, engine.Media, name);
            }
            Exec("RELEASE stock_engine_names"); savepoint = false;
        }
        catch (Exception ex)
        {
            if (savepoint) try { Exec("ROLLBACK TO stock_engine_names"); Exec("RELEASE stock_engine_names"); }
                catch (Exception rollbackError) { Log("engine-name rollback could not be confirmed: " + rollbackError.Message, "err"); }
            Log("stock engine name update failed: " + ex.Message, "err");
            return;
        }
        try { RefreshStockEngineNameUi(); }
        catch (Exception ex) { Log("names were updated, but the display could not refresh: " + ex.Message, "warn"); }
        Log($"{(restore ? "restored original names for" : "labelled")} {changed} stock engine(s), working copy; no IDs, links or physics changed", "ok");
        StockEngineNamesStatus.Text = $"{changed} names {(restore ? "restored" : "updated")} · swaps share these labels · export to keep.";
    }

    void RefreshStockEngineNameUi()
    {
        using var autoCheckScope = SuppressOptionAutoCheck();
        var fitment = CaptureFitmentDraft();
        var power = CapturePowerDraft();
        long? selectedEngine = SelEng()?.Id;
        var names = Query("SELECT EngineID,EngineName FROM Data_Engine")
            .ToDictionary(r => Convert.ToInt64(r["EngineID"]), r => r["EngineName"] as string ?? "(unnamed)");
        _engines = _engines.Select(e => names.TryGetValue(e.Id, out string name) ? e with { Name = name } : e).ToList();
        UpdateEngineDisplayNames();
        RefreshModifiedCarsFromReference();
        if (FiltModified.IsChecked == true) RenderCars();
        if (CarList.SelectedItems.Count == 1) { ShowCarInfo(); PopulatePowerBuilder(); }
        if (!MotorMode)
        {
            RenderEngines();
            if (selectedEngine.HasValue)
                EngList.SelectedItem = EngList.Items.Cast<EngItem>().FirstOrDefault(e => e.Id == selectedEngine.Value);
        }
        RestoreFitmentDraft(fitment);
        RestorePowerDraft(power);
    }

}
