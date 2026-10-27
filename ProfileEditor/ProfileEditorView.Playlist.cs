#nullable disable
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    uint? _selectedPlaylist;

    bool AddPlaylistPointsControls()
    {
#if PROFILE_UNLOCK_TESTING
        if (!PlaylistPointsState.TryReadPlaylists(_container, out var playlists, out string reason) || playlists.Length == 0)
        {
            OverviewPanel.Children.Add(Note(string.IsNullOrEmpty(reason) ? "No saved Playlists found." : reason));
            return false;
        }
        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        var picker = new ComboBox
        {
            Name = "PlaylistSeriesPicker", ItemsSource = playlists, DisplayMemberPath = "Display",
            MinWidth = 300, MinHeight = 32, Margin = new Thickness(0, 0, 8, 4),
        };
        picker.SelectedItem = playlists.SingleOrDefault(p => p.Series == _selectedPlaylist) ?? playlists[0];
        var complete = TestButton("CompletePlaylistButton", "Complete playlist", true);
        complete.Margin = new Thickness(0, 0, 0, 4);
        var status = Note(""); status.Name = "PlaylistCompletionStatus";
        void Refresh()
        {
            if (picker.SelectedItem is not PlaylistPointsState.Playlist selected) return;
            _selectedPlaylist = selected.Series; complete.IsEnabled = selected.Supported;
            status.Text = selected.Supported
                ? "Complete all four seasons. Playlist History points update automatically; rewards stay unclaimed. Click Export save to write a separate file."
                : selected.Reason;
        }
        picker.SelectionChanged += (_, _) => Refresh(); Refresh();
        complete.Click += (_, _) =>
        {
            if (picker.SelectedItem is not PlaylistPointsState.Playlist selected || !selected.Supported) return;
            try
            {
                if (!_confirmUnlockTest($"Complete Series {selected.Series} — all four seasons ({selected.Maximum:n0} points)? "
                    + "This updates saved activities, score totals and Playlist History points. It does not claim rewards or create online records. Export a separate save and test in-game.")) return;
                byte[] before = _container.Serialize();
                if (!PlaylistPointsState.TryCompletePlaylist(_container, selected.Series, out var result, out string error)) throw new InvalidOperationException(error);
                _hasUnlockTestEdits |= !before.AsSpan().SequenceEqual(_container.Serialize());
                RefreshSaveStateDisplays(); BuildCareer(); BuildOverview();
                Log($"Playlist Series {selected.Series}: {result.Activities:n0} activity record(s), {result.Steps:n0} steps completed; {result.Points:n0} points. History: +{result.HistoryPointsAdded:n0} → {result.HistoryPoints:n0}. Reward claims unchanged. Click Export save to write a separate file.", "ok");
                StatusLine.Text = "Playlist completion applied to working copy — click Export save";
            }
            catch (Exception ex) { Log("Playlist completion: " + ex.Message, "error"); }
        };
        controls.Children.Add(picker); controls.Children.Add(complete);
        OverviewPanel.Children.Add(controls); OverviewPanel.Children.Add(status); return true;
#else
        OverviewPanel.Children.Add(Note("Playlist completion is unavailable in this build. Saved activity and rewards are preserved."));
        return false;
#endif
    }
}
