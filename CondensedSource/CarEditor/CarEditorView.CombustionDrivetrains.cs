#nullable disable
using System;
using System.Collections.Generic;

namespace FH6CarEditor;

public partial class CarEditorView
{
    Dictionary<string, object> CombustionDrivetrainDonor(long driveType, long preferredCarId)
    {
        // Use the transmission's actual ratios. A previously converted EV can
        // advertise six gears in Data_Car while retaining its one-speed gearbox.
        return Query(@"SELECT u.DrivetrainID,u.PowertrainId,u.ManufacturerId,
                              t.NumGears-1 AS ForwardGears
                       FROM List_UpgradeDrivetrain u
                       JOIN Data_Car c ON c.Id=u.Ordinal
                       JOIN Data_Drivetrain d ON d.DrivetrainID=u.DrivetrainID
                       JOIN List_UpgradeDrivetrainTransmission t
                         ON t.DrivetrainID=u.DrivetrainID AND t.IsStock=1
                       WHERE u.IsStock=1 AND d.DrivetypeID=? AND d.ShiftSystemID<>5
                         AND c.EngineConfigID<>6 AND c.Displacement>0
                         AND EXISTS(SELECT 1 FROM List_UpgradeEngine e
                                    WHERE e.Ordinal=c.Id AND e.IsStock=1)
                         AND NOT EXISTS(SELECT 1 FROM List_UpgradeMotor m
                                        WHERE m.Ordinal=c.Id AND m.IsStock=1)
                         AND t.NumGears>2 AND t.GearRatio1>0 AND t.GearRatio2>0
                         AND EXISTS(SELECT 1 FROM List_UpgradeDrivetrainClutch p
                                    WHERE p.DrivetrainID=u.DrivetrainID AND p.IsStock=1)
                         AND EXISTS(SELECT 1 FROM List_UpgradeDrivetrainDriveline p
                                    WHERE p.DrivetrainID=u.DrivetrainID AND p.IsStock=1)
                         AND EXISTS(SELECT 1 FROM List_UpgradeDrivetrainDifferential p
                                    WHERE p.DrivetrainID=u.DrivetrainID AND p.IsStock=1)
                       ORDER BY (c.Id=?) DESC,(t.NumGears=7) DESC,(d.ShiftSystemID=1) DESC,
                                (SELECT COUNT(*) FROM List_UpgradeDrivetrainTransmission p
                                 WHERE p.DrivetrainID=u.DrivetrainID AND p.IsStock=0) DESC,
                                c.Id,u.Id,t.Id
                       LIMIT 1", driveType, preferredCarId).FirstOrDefault();
    }

    int FitCombustionDrivetrains(long carId, long preferredCarId)
    {
        var options = Query(@"SELECT u.Id,u.DrivetrainID,u.IsStock,d.DrivetypeID
                              FROM List_UpgradeDrivetrain u
                              LEFT JOIN Data_Drivetrain d ON d.DrivetrainID=u.DrivetrainID
                              WHERE u.Ordinal=? ORDER BY u.IsStock DESC,u.Id", carId);
        if (options.Count(option => Convert.ToInt64(option["IsStock"]) == 1) != 1)
            throw new InvalidOperationException("EV-to-engine conversion needs exactly one stock drivetrain option.");

        int changed = 0;
        foreach (var option in options)
        {
            long drivetrainId = Convert.ToInt64(option["DrivetrainID"]);
            bool stock = Convert.ToInt64(option["IsStock"]) == 1;
            if (option["DrivetypeID"] == null)
                throw new InvalidOperationException($"Drivetrain option {option["Id"]} has no drivetrain definition.");

            long driveType = Convert.ToInt64(option["DrivetypeID"]);
            var donor = CombustionDrivetrainDonor(driveType, preferredCarId)
                ?? throw new InvalidOperationException($"No geared combustion drivetrain with drive type {driveType} and matching parts is available in this DB.");

            // Keep the car's upgrade IDs and its drive layout. Referencing a
            // complete donor brings its transmission, clutch, driveline and diff.
            Exec(@"UPDATE List_UpgradeDrivetrain
                   SET DrivetrainID=?,PowertrainId=?,ManufacturerId=? WHERE Id=?",
                donor["DrivetrainID"], donor["PowertrainId"], donor["ManufacturerId"], option["Id"]);
            if (stock)
                Exec("UPDATE Data_Car SET PowertrainID=?,NumGears=?,DriveTypeID=? WHERE Id=?",
                    donor["PowertrainId"], donor["ForwardGears"], driveType, carId);
            if (Convert.ToInt64(donor["DrivetrainID"]) != drivetrainId) changed++;
        }
        return changed;
    }
}
