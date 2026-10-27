#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;
using Microsoft.Win32;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    Action _refreshOnlineXpPreview;
    void AddGameDataControls()
    {
        var data = ProfileGameData.GetStatus(_container);
        var button = new Button { Name = "ProfileGameDataButton", Content = "Choose game data…", Style = (Style)FindResource("PinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        var status = Note(data.Loaded ? "Game data detected. New compatible content is checked when you load a save." : data.Message);
        status.Name = "ProfileGameDataStatus"; status.ToolTip = data.Source + "\n" + data.Message;
        OverviewPanel.Children.Add(status); OverviewPanel.Children.Add(button);
        button.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Select media\\ObjectModelGame.zip from your matching FH6 install", Filter = "FH6 game definitions (ObjectModelGame.zip)|ObjectModelGame.zip" };
            if (dialog.ShowDialog() != true) return;
            var result = ProfileGameData.Attach(_container, dialog.FileName, remember: true); Log(result.Message, result.Loaded ? "ok" : "err");
            foreach (string warning in ProfileGameData.Warnings(_container)) Log(warning, "info");
            BuildOverview();
        };
    }
    int AddOnlineXpControls()
    {
        _refreshOnlineXpPreview = null;
        if (!OnlineXpState.TryRead(_container, out var level, out var reason)) { OverviewPanel.Children.Add(Note("Online XP: " + reason)); return 0; }
        var summary = Note($"Online level: {level.Number:n0} (Horizon Open)"); summary.Name = "OnlineLevelSummary";
        var preview = Note(""); preview.Name = "OnlineXpPreview";
        var row = EditRow("Online XP", "Horizon Open XP; also syncs driver XP and earned-XP totals by the same amount",
            level.Xp.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (!uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value > int.MaxValue) return (false, "enter a whole number from 0 to 2,147,483,647");
                if (!OnlineXpState.TrySet(_container, value, out bool changed, out string error)) return (false, error);
                _hasUnlockTestEdits |= changed; BuildProperties(); RefreshSaveStateDisplays(); BuildOverview();
                return (true, value.ToString(CultureInfo.InvariantCulture));
            });
        row.Name = "OnlineXpRow"; var input = row.Children.OfType<TextBox>().Single(); input.Name = "OnlineXpInput";
        row.Children.OfType<Button>().Single().Name = "OnlineXpApply";
        void Preview()
        {
            if (!uint.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint value)) { preview.Text = "Online XP preview: enter a whole number from 0 to 2,147,483,647."; return; }
            if (!OnlineXpState.TryPreview(_container, value, out var result, out string error)) { preview.Text = "Online XP preview unavailable: " + error; return; }
            preview.Text = $"Preview: online level {result.Online.Number:n0}" + (result.Online.ToNextLevel is { } next ? $" · {next:n0} XP to next" : " · Maximum mapped level")
                + $" | Driver: {result.Driver.Display}. Rewards are not claimed.";
        }
        _refreshOnlineXpPreview = Preview; input.TextChanged += (_, _) => Preview(); Preview();
        OverviewPanel.Children.Add(summary); OverviewPanel.Children.Add(row); OverviewPanel.Children.Add(preview); return 2;
    }
}
#endif
