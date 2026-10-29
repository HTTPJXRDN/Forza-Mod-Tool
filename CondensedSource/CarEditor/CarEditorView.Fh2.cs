#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using FH6LocalCryptoTool;

namespace FH6CarEditor;

public partial class CarEditorView
{
    ForzaGame _editorGame;
    bool IsFh2 => _editorGame == ForzaGame.Horizon2;
    bool IsFh5 => _editorGame == ForzaGame.Horizon5;
    bool IsFh6 => _editorGame == ForzaGame.Horizon6;
    string EditorGameName => GameDbIdentity.DisplayName(_editorGame);

    void DetectEditorCompatibility()
    {
        // Independent schema signatures separate all four games. Extensions,
        // Autoshow, IDs and CarTrackOffsets are not sufficient game identities.
        _editorGame = GameDbIdentity.Detect(_conn);
    }

    void ConfigureEditorCompatibility()
    {
        Fh2SteeringPanel.Visibility = IsFh2 ? Visibility.Visible : Visibility.Collapsed;
        OptFh2DriftAngle.IsChecked = false;
        OptSlam.Content = IsFh2 ? "Add / repair custom drift suspension (FH2)" : "Remove suspension limit — drift suspension";
        OptSlam.ToolTip = IsFh2
            ? "Adds an extra Race-tier option using this car's stock geometry and native FH2 tuning ranges. Reapply to repair. Steering is optional and car-wide, not kit-specific. In-game testing required."
            : "Adds Drift suspension when missing, repairs locked tuning ranges when reapplied, lowers ride-height limits, and applies the steering angle below.";
        DriftSteeringAngle.IsEnabled = !IsFh2;
        DriftSteeringAngle.ToolTip = IsFh2
            ? "Car-wide SteerMaxAngle / SteerMaxAngleFiltered override in degrees. Uncheck Override steering angle and Apply to restore the matching stock values; the custom kit is kept."
            : "Degrees written to the Drift suspension's SteerMaxAngle and SteerMaxAngleFiltered fields.";
        OptFmTrack.Content = "Direct track offsets (mm)";
        DirectTrackHint.Text = IsFh2 ? "Not supported in FH2. Use the separate FH2 wheel mod for track width."
            : "Enter mm; 15 stores as 0.015 m. Base and upgraded offsets are separate per-car fields; no extra upgrade-menu choices. Check this option and Apply. In-game effect still needs verification.";
        OptFeTires.IsEnabled = !IsFh2;
        if (IsFh2)
        {
            OptFeTires.IsChecked = false;
            OptFeTires.ToolTip = "FH2 has no Forza Edition tire assets. No newer-game compounds or graphics are inserted.";
            AllPriceOneBtn.Content = "All cars = 1 CR";
            AllPriceOneBtn.ToolTip = "Sets FH2 Data_Car.BaseCost only; does not change DLC ownership or hidden-content flags.";
            FiltBodykit.IsChecked = false;
            Log("FH2-compatible schema · plain SQLite SLT export · native engine/motor, power, fitment, Race/Rally and custom drift support. Modern FH6-only options stay disabled.", "info");
        }
        else OptFeTires.ToolTip = "Adds generic Forza Edition tire choices without deleting existing tires.";
        WhiteTiresHint.Text = IsFh2 || IsFh5 ? "Copies this game's native whitewall / vintage tire set and matching brand."
            : "Whitewall, vintage, vintage race, yellowwall + full grip. Brand swap is required for vintage compounds to load.";
        OptWhite.ToolTip = IsFh2 || IsFh5 ? "Uses this game's whiteWall donor and native brand; no FH6-only tire model names."
            : "Sets TireBrandID = 4 and clones a curated 13-tire set (required for vintage compounds).";
        FeTiresHint.Text = IsFh5 ? "Adds the native FH5 Horizon Edition tire variants present in this DB, not FH6's _FE graphics."
            : IsFh2 ? "FH2 has no Forza Edition tire assets."
            : "Adds Street, Sport, Semi-Slick, Rally, Slick, Offroad, Snow and Drag variants using the game's _FE tire models.";
        if (IsFh5) OptFeTires.ToolTip = "Clones FH5's own Horizon Edition tire variants without deleting existing ordinary tires.";
        if (IsFh5)
            Log("FH5 schema · authenticated FH5 SLT export · native FH5 engines, upgrades and matching embedded stock reference. FH6 engine/audio imports and enhanced Lotus package are not used.", "info");
        FiltBodykit.IsEnabled = HasColumn("UpgradePresetPackages", "CarBody");
        FiltBodykit.ToolTip = FiltBodykit.IsEnabled ? null : "This schema has no bodykit preset-package table.";
    }

    void ShowFh2SteeringAngle()
    {
        if (!IsFh2 || _car == null || _batchRunning) return;
        using var scope = SuppressOptionAutoCheck();
        long id = _car.Id;
        var baseline = Fh2SteeringBaseline(id);
        OptFh2DriftAngle.IsEnabled = baseline != null;
        OptFh2DriftAngle.ToolTip = baseline != null ? "Uncheck and Apply to restore this car's stock steering; the drift kit stays. Export to keep."
            : "A matching original car is required so the steering override can also be removed safely after reopening.";
        var current = Query("SELECT SteerMaxAngle,SteerMaxAngleFiltered FROM Data_Car WHERE Id=?", id).Single();
        bool changed = baseline != null && (Convert.ToDouble(current["SteerMaxAngle"]) != Convert.ToDouble(baseline["SteerMaxAngle"]) ||
            Convert.ToDouble(current["SteerMaxAngleFiltered"]) != Convert.ToDouble(baseline["SteerMaxAngleFiltered"]));
        OptFh2DriftAngle.IsChecked = changed;
        DriftSteeringAngle.IsEnabled = changed;
        DriftSteeringAngle.Text = (changed ? Convert.ToDouble(current["SteerMaxAngle"]) : 50d)
            .ToString("0.######", System.Globalization.CultureInfo.CurrentCulture);
    }

    Dictionary<string, object> Fh2SteeringBaseline(long carId) => _stockReferenceAttached
        ? Query(@"SELECT s.SteerMaxAngle,s.SteerMaxAngleFiltered FROM stockref.Data_Car s
                  JOIN main.Data_Car c ON c.Id=s.Id AND c.MediaName=s.MediaName WHERE s.Id=?", carId).SingleOrDefault()
        : null;

    void Fh2DriftAngle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsFh2) return;
        DriftSteeringAngle.IsEnabled = OptFh2DriftAngle.IsChecked == true;
        OptSlam.IsChecked = true; // Both enable and disable are queued until Apply.
    }

    void AddNativeHorizonRwdConversion(long carId)
    {
        var donor = Query(@"SELECT u.DrivetrainID d,u.PowertrainId p,u.ManufacturerId m,u.MassDiff mass,u.WeightDistDiff weight
                            FROM List_UpgradeDrivetrain u JOIN Data_Drivetrain d ON d.DrivetrainID=u.DrivetrainID
                            JOIN Powertrains p ON p.Id=u.PowertrainId
                            JOIN Data_Car c ON c.Id=? AND c.EnginePlacementID=p.EnginePlacementId
                            WHERE d.DrivetypeID=2 AND u.IsStock=0
                            ORDER BY (u.DrivetrainID=2170) DESC,u.Id LIMIT 1", carId).FirstOrDefault()
            ?? throw new InvalidOperationException($"No native {EditorGameName} RWD conversion matches this car's engine placement.");
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=0 AND DrivetrainID=?", carId, donor["d"]) ?? 0) > 0) return;
        InsertUpgrade("List_UpgradeDrivetrain", "Id,Ordinal,DrivetrainID,PowertrainId,MassDiff,WeightDistDiff,Level,ManufacturerId,Price,IsStock",
            NextId("List_UpgradeDrivetrain", carId), carId, donor["d"], donor["p"], donor["mass"], donor["weight"],
            NextLevel("List_UpgradeDrivetrain", "Ordinal", carId), donor["m"], 50L, 0L);
        Log($"✓ {EditorGameName} RWD option added with native engine-placement / powertrain mapping", "ok");
    }

    Dictionary<string, object> Fh2SuspensionTuning(long carId, int level, bool rear)
    {
        string database = _stockReferenceAttached ? "stockref" : "main";
        string link = rear ? "RearSpringDamperPhysicsID" : "FrontSpringDamperPhysicsID";
        string columns = string.Join(",", SuspensionTuningRates.SelectMany(r => new[] { r.Minimum, r.Maximum }).Select(c => "p.\"" + c + "\""));
        string valid = string.Join(" AND ", SuspensionTuningRates.Select(r => $"p.\"{r.Minimum}\">=0 AND p.\"{r.Maximum}\">p.\"{r.Minimum}\""));
        string from = $" FROM {database}.List_UpgradeSpringDamper u JOIN {database}.List_SpringDamperPhysics p ON p.SpringDamperPhysicsID=u.{link} WHERE u.Level=? AND {valid}";
        return Query("SELECT " + columns + from + " AND u.Ordinal=? ORDER BY u.Id LIMIT 1", level, carId).FirstOrDefault()
            ?? Query("SELECT " + columns + from + " GROUP BY " + columns + " ORDER BY COUNT(*) DESC,MIN(u.Id) LIMIT 1", level).FirstOrDefault()
            ?? throw new InvalidOperationException("No healthy native FH2 suspension tuning profile was found.");
    }

    bool Fh2SuspensionNeedsRepair(Dictionary<string, object> row) => SuspensionTuningRates.Any(r => !SuspensionRateValid(row, r));

    Dictionary<string, object> Fh2SuspensionChanges(Dictionary<string, object> row, Dictionary<string, object> native, bool initialize)
    {
        var changes = new Dictionary<string, object>();
        foreach (var rate in SuspensionTuningRates)
        {
            if (!initialize && SuspensionRateValid(row, rate)) continue;
            if (!SuspensionNumber(row, rate.Default, out double value)) throw new InvalidOperationException("Invalid FH2 suspension default: " + rate.Default);
            // FH2's native Race profile is 20–160 / 1–13 / 1–13, not the
            // modern high-velocity damping model. Keep the car's own defaults.
            double low = Math.Min(Convert.ToDouble(native[rate.Minimum]), value);
            double high = Math.Max(Convert.ToDouble(native[rate.Maximum]), value);
            if (!SuspensionNumber(row, rate.Minimum, out double oldLow) || oldLow != low) changes[rate.Minimum] = low;
            if (!SuspensionNumber(row, rate.Maximum, out double oldHigh) || oldHigh != high) changes[rate.Maximum] = high;
        }
        var verified = new Dictionary<string, object>(row);
        foreach (var pair in changes) verified[pair.Key] = pair.Value;
        if (Fh2SuspensionNeedsRepair(verified)) throw new InvalidOperationException("FH2 suspension tuning ranges did not validate.");
        return changes;
    }

    void ApplyFh2DriftSuspension(long carId, double? angle)
    {
        // FH2 has no native Drift level or per-kit steering fields. Use a unique
        // extra Race-tier part instead of nonexistent FH6 Level 5 / type 701.
        // Stable IDs let a later editor session recognize/reapply the same kit.
        long partId = checked(carId * 1000 + 905), frontId = partId, rearId = partId + 1;
        var upgrade = Query("SELECT * FROM List_UpgradeSpringDamper WHERE Id=?", partId).SingleOrDefault();
        if (upgrade == null)
        {
            if (!EnsureStockSuspensionRow(carId, null)) throw new InvalidOperationException("No stock FH2 suspension to clone.");
            if ((ScalarL("SELECT COUNT(*) FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID IN (?,?)", frontId, rearId) ?? 0) != 0)
                throw new InvalidOperationException("The custom FH2 drift physics ID slots are occupied; nothing was replaced.");
            upgrade = Query("SELECT * FROM List_UpgradeSpringDamper WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", carId).First();
            foreach (var axle in new[] { (Column: "FrontSpringDamperPhysicsID", Rear: false, Id: frontId), (Column: "RearSpringDamperPhysicsID", Rear: true, Id: rearId) })
            {
                var physics = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", upgrade[axle.Column]).Single();
                physics["SpringDamperPhysicsID"] = axle.Id;
                physics["Ordinal"] = carId;
                foreach (var change in Fh2SuspensionChanges(physics, Fh2SuspensionTuning(carId, 3, axle.Rear), true)) physics[change.Key] = change.Value;
                physics["MinRideHeight"] = 0.01d;
                physics["MaxCompressHeight"] = 0.005d;
                InsertClonedRow("List_SpringDamperPhysics", physics);
                upgrade[axle.Column] = axle.Id;
            }
            upgrade["Id"] = partId; upgrade["Ordinal"] = carId; upgrade["Level"] = 3L;
            upgrade["IsStock"] = 0L; upgrade["Price"] = 2050L;
            InsertClonedRow("List_UpgradeSpringDamper", upgrade);
        }
        if (Convert.ToInt64(upgrade["Ordinal"]) != carId || Convert.ToInt64(upgrade["Level"]) != 3 || Convert.ToInt64(upgrade["IsStock"]) != 0 ||
            Convert.ToInt64(upgrade["FrontSpringDamperPhysicsID"]) != frontId || Convert.ToInt64(upgrade["RearSpringDamperPhysicsID"]) != rearId ||
            (_stockReferenceAttached && (ScalarL("SELECT COUNT(*) FROM stockref.List_UpgradeSpringDamper WHERE Id=?", partId) ?? 0) > 0))
            throw new InvalidOperationException("The custom FH2 drift part ID is occupied by a different part; nothing was replaced.");
        foreach (var axle in new[] { (Id: frontId, Rear: false), (Id: rearId, Rear: true) })
        {
            var physics = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=? AND Ordinal=?", axle.Id, carId).Single();
            long uses = ScalarL(@"SELECT COALESCE(SUM((FrontSpringDamperPhysicsID=?) + (RearSpringDamperPhysicsID=?)),0) FROM List_UpgradeSpringDamper", axle.Id, axle.Id) ?? 0;
            if (uses != 1) throw new InvalidOperationException("Custom FH2 drift physics are shared; editing was refused.");
            var changes = Fh2SuspensionChanges(physics, Fh2SuspensionTuning(carId, 3, axle.Rear), false);
            changes["MinRideHeight"] = 0.01d; changes["MaxCompressHeight"] = 0.005d;
            SetColumns("List_SpringDamperPhysics", "SpringDamperPhysicsID", axle.Id, changes.Keys.ToArray(), changes);
        }
        var baseline = Fh2SteeringBaseline(carId);
        if (angle.HasValue)
        {
            if (baseline == null) throw new InvalidOperationException("No matching stock car for a reversible steering override.");
            Exec("UPDATE Data_Car SET SteerMaxAngle=?,SteerMaxAngleFiltered=? WHERE Id=?", angle.Value, angle.Value, carId);
        }
        else if (baseline != null)
            Exec("UPDATE Data_Car SET SteerMaxAngle=?,SteerMaxAngleFiltered=? WHERE Id=?", baseline["SteerMaxAngle"], baseline["SteerMaxAngleFiltered"], carId);
        Log($"✓ FH2 custom drift part {partId} (extra Race-tier choice); " + (angle.HasValue ? $"car-wide steering {angle:0.##}°" : "original steering kept/restored") + ". Export, then test in game.", "ok");
    }

    void ApplyNativeHorizonWhitewalls(long carId)
    {
        long donor = ScalarL("SELECT Ordinal FROM List_UpgradeTireCompound WHERE TireModelName='whiteWall' ORDER BY Id LIMIT 1")
            ?? throw new InvalidOperationException($"No {EditorGameName} whitewall donor exists.");
        var rows = Query("SELECT * FROM List_UpgradeTireCompound WHERE Ordinal=? ORDER BY Level,Id", donor);
        if (rows.Count(r => Convert.ToInt64(r["IsStock"]) == 1) != 1) throw new InvalidOperationException("The vintage donor has no unique stock tire.");
        object brand = Scalar("SELECT TireBrandID FROM Data_Car WHERE Id=?", donor) ?? throw new InvalidOperationException("Missing native vintage tire brand.");
        Exec("DELETE FROM List_UpgradeTireCompound WHERE Ordinal=?", carId);
        int slot = 0;
        foreach (var row in rows)
        {
            row["Id"] = checked(carId * 1000 + slot++); row["Ordinal"] = carId;
            row["Price"] = Convert.ToInt64(row["IsStock"]) == 1 ? 0L : 50L;
            InsertClonedRow("List_UpgradeTireCompound", row);
        }
        Exec("UPDATE Data_Car SET TireBrandID=? WHERE Id=?", brand, carId);
        Log($"✓ {EditorGameName} native whitewall / vintage tire set and matching brand copied", "ok");
    }
}
