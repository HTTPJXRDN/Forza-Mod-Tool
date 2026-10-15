#nullable disable
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    bool AddConsumableEdit(ProfileConsumableState.Kind kind, string label, string hint)
    {
        if (!ProfileConsumableState.TryRead(_container, kind, out int current, out _))
        {
            OverviewPanel.Children.Add(Note(label + ": unavailable in this save; not editable."));
            return false;
        }
        var row = EditRow(label, hint, current.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return (false, "enter a whole number from 0 to 2,147,483,647");
            if (!ProfileConsumableState.TrySet(_container, kind, value, out bool changed, out string reason)) return (false, reason);
#if PROFILE_UNLOCK_TESTING
            _hasUnlockTestEdits |= changed;
#endif
            RefreshSaveStateDisplays();
            return (true, value.ToString(CultureInfo.InvariantCulture));
        });
        row.Name = kind + "BalanceRow";
        row.Children.OfType<TextBox>().Single().Name = kind + "BalanceInput";
        row.Children.OfType<Button>().Single().Name = kind + "BalanceApply";
        OverviewPanel.Children.Add(row); return true;
    }
}
