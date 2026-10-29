#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6CarEditor;

public partial class CarEditorView
{
    double? ReadTrackStep()
    {
        if (TrackStepSizeBox == null || !NumericText.TryParseDouble(TrackStepSizeBox.Text, out double value) ||
            !double.IsFinite(value) || value <= 0) return null;
        value = Math.Round(value, 3);
        return value >= 0.001 ? value : null;
    }

    string TrackStepCaption() => ReadTrackStep() is double value
        ? "+" + value.ToString("0.###", CultureInfo.CurrentCulture) + " mm  wider" : "Set step (mm)";

    void TrackStepSize_Changed(object sender, TextChangedEventArgs e)
    {
        // Only current buttons retain their refresh actions; changing cars does
        // not leave handlers on discarded controls. Queued steps stay intact.
        if (OFBoxes == null || ORBoxes == null) return;
        foreach (var button in OFBoxes.Children.OfType<Button>().Concat(ORBoxes.Children.OfType<Button>()))
            if (button.Tag is Action refresh) refresh();
    }

    void RepairTrackSpacingNames(string table, long bodyId) => TrackSpacingLabels.Repair(_conn, table, bodyId);

    void RepairTrackSpacingNamesForCar(long carId)
    {
        foreach (var body in Query("SELECT DISTINCT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?", carId))
        {
            long id = Convert.ToInt64(body["CarBodyID"]);
            RepairTrackSpacingNames("List_UpgradeCarBodyTrackSpacingFront", id);
            RepairTrackSpacingNames("List_UpgradeCarBodyTrackSpacingRear", id);
        }
    }
}
