#nullable disable
using System;
using System.Globalization;
using FH6LocalCryptoTool;

namespace FH6CarEditor;

public partial class CarEditorView
{
    long? _widebodyTrackBodyId;
    double _loadedBaseTrackFront, _loadedBaseTrackRear;
    string _loadedBaseTrackFrontText = "", _loadedBaseTrackRearText = "";

    void ResetWidebodyBaseTrack()
    {
        _widebodyTrackBodyId = null;
        WidebodyBaseTrackFields.IsEnabled = false;
        WidebodyBaseTrackFront.Text = WidebodyBaseTrackRear.Text = "";
        WidebodyBaseTrackHint.Text = "Select an added Widebody tab. Stock bodies and factory kits are protected.";
    }

    bool CanEditWidebodyBaseTrack(long bodyId, out string reason)
    {
        reason = "Select one car and an added Widebody tab; batch editing is not supported.";
        if (_conn == null || _car == null || _batchRunning || CarList.SelectedItems.Count != 1) return false;
        reason = "The stock reference is required to protect stock bodies and factory kits.";
        if (!_stockReferenceAttached || !ReferenceHasCar(_car.Id)) return false;
        reason = "This database has no supported model-track fields.";
        if (!HasColumn("Data_CarBody", "ModelFrontTrackOuter") ||
            !HasColumn("Data_CarBody", "ModelRearTrackOuter")) return false;
        reason = "Select an added Widebody tab. Stock bodies and factory kits are protected.";
        // A body can be referenced by more than one upgrade/car. Do not allow a
        // seemingly local edit to affect another kit, or any game-authored body.
        if ((ScalarL("SELECT COUNT(*) FROM main.List_UpgradeCarBody WHERE CarBodyID=?", bodyId) ?? 0) != 1 ||
            (ScalarL("SELECT COUNT(*) FROM main.List_UpgradeCarBody WHERE Ordinal=? AND CarBodyID=? AND IsStock=0",
                     _car.Id, bodyId) ?? 0) != 1 ||
            (ScalarL("SELECT COUNT(*) FROM stockref.Data_CarBody WHERE Id=?", bodyId) ?? 0) != 0 ||
            (ScalarL("SELECT COUNT(*) FROM stockref.List_UpgradeCarBody WHERE CarBodyID=?", bodyId) ?? 0) != 0 ||
            (ScalarL("SELECT COUNT(*) FROM main.Data_CarBody WHERE Id=?", bodyId) ?? 0) != 1) return false;
        reason = "";
        return true;
    }

    void FillWidebodyBaseTrack(long bodyId)
    {
        ResetWidebodyBaseTrack();
        if (!CanEditWidebodyBaseTrack(bodyId, out string reason))
        {
            WidebodyBaseTrackHint.Text = reason;
            return;
        }
        var row = Query("SELECT ModelFrontTrackOuter,ModelRearTrackOuter FROM main.Data_CarBody WHERE Id=?", bodyId)[0];
        if (row["ModelFrontTrackOuter"] is null or DBNull || row["ModelRearTrackOuter"] is null or DBNull) return;
        _loadedBaseTrackFront = Convert.ToDouble(row["ModelFrontTrackOuter"], CultureInfo.InvariantCulture);
        _loadedBaseTrackRear = Convert.ToDouble(row["ModelRearTrackOuter"], CultureInfo.InvariantCulture);
        if (!double.IsFinite(_loadedBaseTrackFront) || !double.IsFinite(_loadedBaseTrackRear)) return;
        _loadedBaseTrackFrontText = _loadedBaseTrackFront.ToString("0.######", CultureInfo.CurrentCulture);
        _loadedBaseTrackRearText = _loadedBaseTrackRear.ToString("0.######", CultureInfo.CurrentCulture);
        WidebodyBaseTrackFront.Text = _loadedBaseTrackFrontText;
        WidebodyBaseTrackRear.Text = _loadedBaseTrackRearText;
        _widebodyTrackBodyId = bodyId;
        WidebodyBaseTrackFields.IsEnabled = true;
        WidebodyBaseTrackHint.Text = "Absolute model track in metres (greater than 0, up to 10). Apply changes saves only this added kit. Stock body, tire widths and track-offset upgrade rows stay unchanged.";
    }

    bool TryReadWidebodyBaseTrackEdit(out long bodyId, out double front, out double rear,
        out bool changed, out string reason)
    {
        bodyId = _widebodyTrackBodyId ?? 0;
        front = _loadedBaseTrackFront;
        rear = _loadedBaseTrackRear;
        changed = false;
        reason = "";
        if (_batchRunning || !WidebodyBaseTrackFields.IsEnabled) return true;
        reason = "The body selection changed; select the added widebody again.";
        if (_widebodyTrackBodyId != FirstBody()) return false;
        if (!CanEditWidebodyBaseTrack(bodyId, out reason)) return false;

        // Preserve full DB precision when an untouched prefilled box was rounded
        // for display, including when only the other axle is edited.
        if ((WidebodyBaseTrackFront.Text.Trim() != _loadedBaseTrackFrontText &&
             !NumericText.TryParseDouble(WidebodyBaseTrackFront.Text, out front)) ||
            (WidebodyBaseTrackRear.Text.Trim() != _loadedBaseTrackRearText &&
             !NumericText.TryParseDouble(WidebodyBaseTrackRear.Text, out rear)) ||
            !double.IsFinite(front) || !double.IsFinite(rear) || front <= 0 || rear <= 0 || front > 10 || rear > 10)
        {
            reason = "Front and rear base track must be finite numbers greater than 0 and at most 10 metres.";
            return false;
        }
        changed = front != _loadedBaseTrackFront || rear != _loadedBaseTrackRear;
        reason = "";
        return true;
    }
}
