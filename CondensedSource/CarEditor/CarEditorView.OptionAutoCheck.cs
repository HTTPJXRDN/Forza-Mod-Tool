#nullable disable
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace FH6CarEditor;

public partial class CarEditorView
{
    int _optionAutoCheckSuppression;

    void InitializeOptionAutoCheck()
    {
        foreach (var (box, option) in new (TextBox, CheckBox)[]
        {
            (PowerBoostScale, OptPowerBoost), (PowerRedline, OptPowerRedline),
            (PowerEngineMass, OptPowerEngineMass), (PowerWeightDistribution, OptPowerWeight),
            (PowerEvTorque, OptPowerEvTorque), (DriftSteeringAngle, OptSlam),
            (FmTrackFront, OptFmTrack), (FmTrackRear, OptFmTrack),
            (FmTrackUpgradeFront, OptFmTrack), (FmTrackUpgradeRear, OptFmTrack),
        })
            box.TextChanged += (_, _) =>
            {
                if (box.IsEnabled) AutoCheckEditedOption(option);
            };
    }

    void AutoCheckEditedOption(CheckBox option)
    {
        // Selecting cars, refreshing a body/engine, and replaying a saved draft
        // are not edits. A real value edit only queues the corresponding option;
        // the user still chooses when to Apply, and may uncheck it explicitly.
        if (_optionAutoCheckSuppression != 0 || _batchRunning || _conn == null || _car == null ||
            !IsEnabled || CarList.SelectedItems.Count == 0 || option?.IsEnabled != true) return;
        option.IsChecked = true;
    }

    CheckBox FitmentOption(List<TextBox> values)
        => ReferenceEquals(values, _rf) ? OptRims
         : ReferenceEquals(values, _wf) || ReferenceEquals(values, _wr) ? OptWidth
         : ReferenceEquals(values, _sw) ? OptAspect
         : ReferenceEquals(values, _ofF) || ReferenceEquals(values, _ofR) ? OptTrack
         : null;

    void FitmentValueChanged(TextBox box)
    {
        if (!box.IsEnabled) return;
        foreach (var values in new[] { _rf, _wf, _wr, _sw, _ofF, _ofR })
            if (values.Contains(box))
            {
                AutoCheckEditedOption(FitmentOption(values));
                return;
            }
    }

    void AddFitmentValue(Panel panel, List<TextBox> values)
    {
        int count = values.Count;
        AddNum(panel, values);
        if (values.Count != count) AutoCheckEditedOption(FitmentOption(values));
    }

    IDisposable SuppressOptionAutoCheck()
    {
        _optionAutoCheckSuppression++;
        return new OptionAutoCheckScope(this);
    }

    sealed class OptionAutoCheckScope(CarEditorView owner) : IDisposable
    {
        CarEditorView _owner = owner;
        public void Dispose()
        {
            if (_owner == null) return;
            _owner._optionAutoCheckSuppression--;
            _owner = null;
        }
    }
}
