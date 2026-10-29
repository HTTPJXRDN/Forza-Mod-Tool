#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FH6CarEditor;

public partial class CarEditorView
{
    static readonly (string Default, string Minimum, string Maximum)[] SuspensionTuningRates =
    {
        ("DefSpringRate", "MinSpringRate", "MaxSpringRate"),
        ("DefDampenBumpRate", "MinDampenBumpRate", "MaxDampenBumpRate"),
        ("DefDampenReboundRate", "MinDampenReboundRate", "MaxDampenReboundRate")
    };
    static readonly (string Minimum, string Maximum)[] SuspensionFastDampingRanges =
    {
        ("MinDampenBumpRateHighVelocity", "MaxDampenBumpRateHighVelocity"),
        ("MinDampenBumpRateSuperHighVelocity", "MaxDampenBumpRateSuperHighVelocity"),
        ("MinDampenReboundRateHighVelocity", "MaxDampenReboundRateHighVelocity"),
        ("MinDampenReboundRateSuperHighVelocity", "MaxDampenReboundRateSuperHighVelocity")
    };
    static readonly (string High, string SuperHigh)[] SuspensionDampingThresholds =
    {
        ("DampenBumpHighVelocityThreshold", "DampenBumpSuperHighVelocityThreshold"),
        ("DampenReboundHighVelocityThreshold", "DampenReboundSuperHighVelocityThreshold")
    };
    static readonly string[] SuspensionTuningColumns = SuspensionTuningRates.SelectMany(r => new[] { r.Minimum, r.Maximum })
        .Concat(SuspensionFastDampingRanges.SelectMany(r => new[] { r.Minimum, r.Maximum }))
        .Concat(SuspensionDampingThresholds.SelectMany(r => new[] { r.High, r.SuperHigh })).ToArray();

    static bool SuspensionNumber(Dictionary<string, object> row, string column, out double value)
    {
        value = 0;
        return row.TryGetValue(column, out var raw) && raw != null && raw != DBNull.Value
            && double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;
    }
    static bool SuspensionRangeValid(Dictionary<string, object> row, string minimum, string maximum)
        => SuspensionNumber(row, minimum, out double low) && SuspensionNumber(row, maximum, out double high) && high > low;
    static bool SuspensionRateValid(Dictionary<string, object> row, (string Default, string Minimum, string Maximum) rate)
        => SuspensionRangeValid(row, rate.Minimum, rate.Maximum) && SuspensionNumber(row, rate.Default, out double value)
            && value >= Convert.ToDouble(row[rate.Minimum]) && value <= Convert.ToDouble(row[rate.Maximum]);
    static bool SuspensionThresholdValid(Dictionary<string, object> row, (string High, string SuperHigh) pair)
        => SuspensionNumber(row, pair.High, out double high) && SuspensionNumber(row, pair.SuperHigh, out double superHigh) && superHigh > high;
    static bool SuspensionNeedsTuningRepair(Dictionary<string, object> row)
        => SuspensionTuningRates.Any(r => !SuspensionRateValid(row, r))
            || SuspensionFastDampingRanges.Any(r => !SuspensionRangeValid(row, r.Minimum, r.Maximum))
            || SuspensionDampingThresholds.Any(r => !SuspensionThresholdValid(row, r));

    Dictionary<string, object> NativeSuspensionTuning(long carId, int level, bool rear)
    {
        if (level is not (3 or 5)) throw new ArgumentOutOfRangeException(nameof(level));
        if (!SuspensionTuningColumns.All(c => HasColumn("List_SpringDamperPhysics", c)))
            throw new InvalidOperationException("This database does not have the supported suspension tuning fields; its schema was not changed.");
        string database = _stockReferenceAttached ? "stockref" : "main";
        string link = rear ? "RearSpringDamperPhysicsID" : "FrontSpringDamperPhysicsID";
        string columns = string.Join(",", SuspensionTuningColumns.Select(c => "p.\"" + c + "\""));
        string valid = string.Join(" AND ", SuspensionTuningRates.Select(r => (r.Minimum, r.Maximum))
            .Concat(SuspensionFastDampingRanges).Select(r => $"p.\"{r.Minimum}\">=0 AND p.\"{r.Maximum}\">p.\"{r.Minimum}\"")
            .Concat(SuspensionDampingThresholds.Select(r => $"p.\"{r.High}\">=0 AND p.\"{r.SuperHigh}\">p.\"{r.High}\"")));
        string from = $" FROM {database}.List_UpgradeSpringDamper u JOIN {database}.List_SpringDamperPhysics p ON p.SpringDamperPhysicsID=u.\"{link}\" WHERE u.Level=? AND {valid}";
        // Prefer the matching clean, same-car/axle upgrade. Otherwise use the
        // most common complete native profile, not another car's geometry or
        // spring/damper defaults. Unknown DB versions can use healthy authored
        // rows from their own DB; no schema or cross-version reference is added.
        var native = Query("SELECT " + columns + from + " AND u.Ordinal=? ORDER BY u.IsStock,u.Id LIMIT 1", level, carId).FirstOrDefault()
            ?? Query("SELECT " + columns + from + " AND u.IsStock=0 GROUP BY " + columns + " ORDER BY COUNT(*) DESC,MIN(u.Id) LIMIT 1", level).FirstOrDefault();
        // A single-car FH6 export may contain no native Race/Drift donors and
        // no VersionInfo for matching stockref. Both levels use this verified
        // modal FH6 range profile (944 Race / 1,150 Drift axles inspected).
        // Defaults and geometry still come from the target, never this fallback.
        // Do not assume these FH6 values for an unidentified Motorsport schema.
        if (native == null && IsFh6)
            native = new Dictionary<string, object>
            {
                ["MinSpringRate"] = 40d, ["MaxSpringRate"] = 200d,
                ["MinDampenBumpRate"] = 1d, ["MaxDampenBumpRate"] = 20d,
                ["MinDampenReboundRate"] = 1d, ["MaxDampenReboundRate"] = 20d,
                ["MinDampenBumpRateHighVelocity"] = 0.67d, ["MaxDampenBumpRateHighVelocity"] = 13.33d,
                ["MinDampenBumpRateSuperHighVelocity"] = 0.33d, ["MaxDampenBumpRateSuperHighVelocity"] = 6.67d,
                ["MinDampenReboundRateHighVelocity"] = 0.5d, ["MaxDampenReboundRateHighVelocity"] = 10d,
                ["MinDampenReboundRateSuperHighVelocity"] = 0.33d, ["MaxDampenReboundRateSuperHighVelocity"] = 6.67d,
                ["DampenBumpHighVelocityThreshold"] = 0.2d, ["DampenBumpSuperHighVelocityThreshold"] = 1.5d,
                ["DampenReboundHighVelocityThreshold"] = 0.2d, ["DampenReboundSuperHighVelocityThreshold"] = 1.5d
            };
        if (native == null || SuspensionTuningColumns.Any(c => !SuspensionNumber(native, c, out _)))
            throw new InvalidOperationException($"No valid native {(level == 3 ? "Race" : "Drift")} tuning profile was found; no suspension changes were kept.");
        return native;
    }

    static Dictionary<string, object> SuspensionTuningChanges(Dictionary<string, object> row, Dictionary<string, object> native, bool initialize)
    {
        var changes = new Dictionary<string, object>();
        void Set(string column, double value)
        {
            if (!SuspensionNumber(row, column, out double old) || old != value) changes[column] = value;
        }
        foreach (var rate in SuspensionTuningRates)
        {
            if (!initialize && SuspensionRateValid(row, rate)) continue;
            if (!SuspensionNumber(row, rate.Default, out double value))
                throw new InvalidOperationException($"Invalid {rate.Default}; suspension tuning was not changed.");
            // Vanilla Race springs already use a lower minimum on soft cars.
            // Keep the vehicle's existing default, widening the native bounds
            // only when necessary; never paste a donor's stiffness into a truck.
            Set(rate.Minimum, Math.Min(Convert.ToDouble(native[rate.Minimum]), value));
            Set(rate.Maximum, Math.Max(Convert.ToDouble(native[rate.Maximum]), value));
        }
        foreach (var pair in SuspensionFastDampingRanges)
        {
            if (!initialize && SuspensionRangeValid(row, pair.Minimum, pair.Maximum)) continue;
            Set(pair.Minimum, Convert.ToDouble(native[pair.Minimum]));
            Set(pair.Maximum, Convert.ToDouble(native[pair.Maximum]));
        }
        foreach (var pair in SuspensionDampingThresholds)
        {
            // Authored off-road thresholds legitimately differ from road cars.
            // Keep an ordered vehicle-specific pair; repair only invalid pairs.
            if (SuspensionThresholdValid(row, pair)) continue;
            Set(pair.High, Convert.ToDouble(native[pair.High]));
            Set(pair.SuperHigh, Convert.ToDouble(native[pair.SuperHigh]));
        }
        var verified = new Dictionary<string, object>(row);
        foreach (var change in changes) verified[change.Key] = change.Value;
        if (SuspensionNeedsTuningRepair(verified)) throw new InvalidOperationException("The generated suspension tuning ranges failed validation.");
        return changes;
    }

    void InitializeSuspensionTuning(long carId, int level, bool rear, Dictionary<string, object> row)
    {
        if (IsFh2)
        {
            foreach (var change in Fh2SuspensionChanges(row, Fh2SuspensionTuning(carId, level, rear), true)) row[change.Key] = change.Value;
            return;
        }
        if (level is not (3 or 5)) return; // Rally/lift behavior is unchanged.
        foreach (var change in SuspensionTuningChanges(row, NativeSuspensionTuning(carId, level, rear), initialize: true))
            row[change.Key] = change.Value;
    }

    long ExclusiveSuspensionPhysics(long carId, Dictionary<string, object> upgrade, string column, Dictionary<string, object> physics)
    {
        long id = Convert.ToInt64(physics["SpringDamperPhysicsID"]), part = Convert.ToInt64(upgrade["Id"]);
        long uses = ScalarL(@"SELECT COALESCE(SUM((FrontSpringDamperPhysicsID=?) + (RearSpringDamperPhysicsID=?)),0)
                            FROM List_UpgradeSpringDamper", id, id) ?? 0;
        if (uses == 1 && Convert.ToInt64(physics["Ordinal"]) == carId) return id;
        bool rear = column == "RearSpringDamperPhysicsID";
        long start = carId * 1000 + (rear ? 100 : 0);
        long clone = FreeSpringPhysicsId(start + Convert.ToInt64(upgrade["Level"]), start + 100);
        var copy = new Dictionary<string, object>(physics) { ["SpringDamperPhysicsID"] = clone, ["Ordinal"] = carId };
        InsertClonedRow("List_SpringDamperPhysics", copy);
        if (Exec($"UPDATE List_UpgradeSpringDamper SET \"{column}\"=? WHERE Id=? AND Ordinal=? AND IsStock=0", clone, part, carId) != 1)
            throw new InvalidOperationException("Suspension physics ownership could not be isolated.");
        return clone;
    }

    int RepairSuspensionTuning(long carId, int level)
    {
        int repaired = 0;
        foreach (var upgrade in Query("SELECT * FROM List_UpgradeSpringDamper WHERE Ordinal=? AND Level=? AND IsStock=0 ORDER BY Id", carId, level))
        foreach (var axle in new[] { (Column: "FrontSpringDamperPhysicsID", Rear: false), (Column: "RearSpringDamperPhysicsID", Rear: true) })
        {
            var physics = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", upgrade[axle.Column]).SingleOrDefault()
                ?? throw new InvalidOperationException("A suspension upgrade references missing physics; no changes were kept.");
            if (IsFh2 ? !Fh2SuspensionNeedsRepair(physics) : !SuspensionNeedsTuningRepair(physics)) continue;
            var changes = IsFh2 ? Fh2SuspensionChanges(physics, Fh2SuspensionTuning(carId, level, axle.Rear), false)
                : SuspensionTuningChanges(physics, NativeSuspensionTuning(carId, level, axle.Rear), initialize: false);
            if (changes.Count == 0) continue;
            long id = ExclusiveSuspensionPhysics(carId, upgrade, axle.Column, physics);
            SetColumns("List_SpringDamperPhysics", "SpringDamperPhysicsID", id, changes.Keys.ToArray(), changes);
            var verified = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", id).Single();
            if (IsFh2 ? Fh2SuspensionNeedsRepair(verified) : SuspensionNeedsTuningRepair(verified)) throw new InvalidOperationException("Repaired suspension tuning did not validate.");
            repaired++;
        }
        return repaired;
    }
}
