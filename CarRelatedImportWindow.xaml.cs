using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class CarRelatedImportWindow : Window
{
    public bool RebuildCar => RebuildCarRows.IsChecked == true;
    public bool ReplaceConflicts => RebuildCar || OverrideConflicts.IsChecked == true;
    private readonly int _carCount;
    private readonly bool _convertedReplacement;

    public CarRelatedImportWindow(CarRelatedDbExport.Car car, ModMergePreview preview,
                                  string basePath, string donorPath)
        : this(new[] { car }, preview, basePath, donorPath) { }

    public CarRelatedImportWindow(IReadOnlyList<CarRelatedDbExport.Car> cars, ModMergePreview preview,
                                  string basePath, string donorPath, bool convertedReplacement = false)
    {
        _convertedReplacement = convertedReplacement;
        if (cars.Count == 0) throw new ArgumentException("The donor contains no cars.", nameof(cars));
        _carCount = cars.Count;
        InitializeComponent();
        SourceText.Text = $"Base: {Path.GetFileName(basePath)}    Donor: {Path.GetFileName(donorPath)}";
        SourceText.ToolTip = $"Base: {basePath}\nDonor: {donorPath}";
        CarText.Text = cars.Count == 1 ? cars[0].ToString() :
            $"{cars.Count:n0} cars: " + string.Join("; ", cars.Take(3).Select(c => c.ToString())) +
            (cars.Count > 3 ? "; … (hover for all cars)" : "");
        CarText.ToolTip = string.Join("\n", cars);
        if (cars.Count > 1)
        {
            OverrideConflicts.Content = "Use donor values for conflicting rows of these cars";
            RebuildCarRows.Content = "Drop & rebuild all donor cars";
            RebuildDescription.Text = "Optional fresh replacement: remove all donor cars' existing rows, swaps and kits, including rows absent from the donor, then insert the donor's set in one operation. Whole tables are never dropped. Shared records used by unselected cars are kept; conflicting shared changes stop the rebuild. VersionInfo is unchanged.";
            ImportButton.Content = "Import cars";
        }
        int additions = 0, conflicts = 0;
        foreach (var table in preview.Tables)
            foreach (var row in table.Rows)
                if (row.IsConflict) conflicts++; else additions++;
        ChangesText.Text = $"{additions:n0} new row(s), {conflicts:n0} conflicting row(s) across {preview.Tables.Count} table(s).";
        if (_convertedReplacement)
        {
            OverrideConflicts.IsChecked = true;
            RebuildCarRows.IsEnabled = false;
            MergeDescription.Text = "Converted replacement pack: update this existing car slot, add the donor parts/physics and remove this target's obsolete upgrade links. Other cars, shared definitions and VersionInfo are preserved.";
            RebuildDescription.Text = "This tagged replacement is handled automatically with the top box. The separate Drop & rebuild workflow remains available for ordinary FH6 car donors.";
            OverrideConflicts.Checked += ReplacementChoiceChanged;
            OverrideConflicts.Unchecked += ReplacementChoiceChanged;
            ImportButton.Content = "Import replacement";
        }
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ReplacementChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_convertedReplacement) ImportButton.IsEnabled = OverrideConflicts.IsChecked == true;
    }

    private void RebuildMode_Changed(object sender, RoutedEventArgs e)
    {
        // IsChecked can raise its event while InitializeComponent is still
        // constructing later controls.
        if (OverrideConflicts is null || ImportButton is null) return;
        OverrideConflicts.IsEnabled = !RebuildCar;
        MergeDescription.Visibility = RebuildCar ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.Content = RebuildCar ? (_carCount == 1 ? "Rebuild car" : "Rebuild cars") :
            (_carCount == 1 ? "Import car" : "Import cars");
    }
}
