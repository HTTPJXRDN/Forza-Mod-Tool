#nullable disable
using System.Globalization;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    int AddDriverXpControls()
    {
        if (!DriverXpState.TryRead(_container, out var level))
        {
            OverviewPanel.Children.Add(Note("Driver level / XP: unavailable in this save; not editable.")); return 0;
        }
        var summary = Note("Driver level: " + level.Display); summary.Name = "DriverLevelSummary";
        summary.Foreground = (System.Windows.Media.Brush)FindResource("Ink"); summary.FontSize = 13;
        OverviewPanel.Children.Add(summary);
        var preview = Note(""); preview.Name = "DriverXpPreview";
        var row = EditRow("Driver XP", "preview the resulting level before applying; level-up rewards are not granted",
            level.Xp.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (!uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
                    return (false, "enter a whole number from 0 to 4,294,967,295");
                if (!DriverXpState.TrySet(_container, value, out bool changed)) return (false, "this save's driver XP layout is not recognized");
#if PROFILE_UNLOCK_TESTING
                _hasUnlockTestEdits |= changed;
#endif
                summary.Text = "Driver level: " + DriverXpState.Calculate(value).Display;
                BuildProperties();
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        row.Name = "DriverXpRow";
        var input = row.Children.OfType<TextBox>().Single(); input.Name = "DriverXpInput";
        row.Children.OfType<Button>().Single().Name = "DriverXpApply";
        void Preview()
        {
            if (!uint.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            { preview.Text = "XP preview: enter a whole number from 0 to 4,294,967,295."; return; }
            var result = DriverXpState.Calculate(value);
            preview.Text = "XP preview: " + result.Display + (result.XpToNextLevel is { } next ? $" · {next:n0} XP to next level" : " · Maximum level");
        }
        input.TextChanged += (_, _) => Preview(); Preview();
        OverviewPanel.Children.Add(row); OverviewPanel.Children.Add(preview); return 2;
    }
}
