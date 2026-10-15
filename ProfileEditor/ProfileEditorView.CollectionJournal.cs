#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    string _journalCampaign = "All campaigns";
    string _journalCategory = "All categories";
    string _journalSearch = "";
    bool _journalIncompleteOnly = true;
    Action _refreshCollectionJournalDisplay;

    void AddCollectionJournalControls()
    {
        var heading = SectionHeader("Collection Journal"); heading.Name = "CollectionJournalHeading";
        OverviewPanel.Children.Add(heading);
        var journal = CollectionJournalState.ReadForDisplay(_container);
        string Total(int? points) => points?.ToString("n0") ?? "Unknown";
        string StatusText() => (journal.CanEdit ? $"{journal.Completed:n0} completed · " : "Read-only · ")
            + $"Festival: {Total(journal.FestivalPoints)} · Discover Japan: {Total(journal.DiscoveryPoints)}"
            + (journal.PartialCatalog ? " · " + journal.Warning : journal.TotalsAgree ? "" : " · Points will sync when you complete challenges.");
        var status = Note(StatusText());
        status.Name = "CollectionJournalStatus"; OverviewPanel.Children.Add(status);
        var help = Note(!journal.CanEdit
            ? "The reference challenges are shown below. Unknown does not mean unfinished; saved completion could not be read. Editing stays off."
            : journal.PartialCatalog
                ? "Select rows (Ctrl-click for more, Ctrl+A for all), then click Complete selected challenges. Unsupported rows are kept unchanged."
                : "Select rows (Ctrl-click for more, Ctrl+A for all), then Complete selected challenges. Points, wristbands, linked race results, PR stunts and job rank update automatically.");
        help.Name = "CollectionJournalHelp"; OverviewPanel.Children.Add(help);
        OverviewPanel.Children.Add(Note("World sync: boards/mascots, landmarks and Story stars. For regions, complete all ten rows. Story results use completion and stars; online records stay unchanged."));

        var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        var campaign = new ComboBox
        {
            Name = "JournalCampaignFilter", ItemsSource = new[] { "All campaigns", "Horizon Festival", "Discover Japan" },
            SelectedItem = _journalCampaign, Width = 170, MinHeight = 30, Margin = new Thickness(0, 0, 8, 4),
        };
        var category = new ComboBox { Name = "JournalCategoryFilter", Width = 205, MinHeight = 30, Margin = new Thickness(0, 0, 8, 4) };
        var search = new TextBox
        {
            Name = "JournalSearch", Text = _journalSearch, Width = 250, MinHeight = 30,
            Style = (Style)FindResource("Input"), ToolTip = "Search challenge name, objective, subtitle or car ordinal", Margin = new Thickness(0, 0, 8, 4),
        };
        var incomplete = new CheckBox { Name = "JournalIncompleteOnly", Content = "Unfinished only", Style = (Style)FindResource("PinkCheckBox"), IsChecked = _journalIncompleteOnly, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) };
        filters.Children.Add(campaign); filters.Children.Add(category); filters.Children.Add(search); filters.Children.Add(incomplete);
        OverviewPanel.Children.Add(filters);
        var grid = new DataGrid
        {
            Name = "CollectionJournalGrid", Style = (Style)FindResource("PdGrid"), Height = 400, Margin = new Thickness(0, 4, 0, 4),
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
        };
        var statusCheckBoxStyle = (Style)FindResource("JournalStatusCheckBox");
        if (journal.PartialCatalog)
            grid.Columns.Add(new DataGridTextColumn { Header = "Done", Binding = new Binding("CompletionText"), Width = 75 });
        else
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Done", Binding = new Binding("Completed"), Width = 50, ElementStyle = statusCheckBoxStyle });
        grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new Binding("Category"), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Challenge", Binding = new Binding("Name"), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Objective", Binding = new Binding("Objective"), Width = new DataGridLength(1.7, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Points", Binding = new Binding("PointsDisplay"), Width = 65 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Auto bonus", Binding = new Binding("Automatic"), Width = 90, ElementStyle = statusCheckBoxStyle });
        if (journal.PartialCatalog)
            grid.Columns.Add(new DataGridTextColumn { Header = "Compatibility", Binding = new Binding("Compatibility"), Width = 230 });
        OverviewPanel.Children.Add(grid);
        var complete = TestButton("CompleteJournalSelectionButton", "Complete selected challenges", journal.CanEdit);
        OverviewPanel.Children.Add(complete);
        if (journal.PartialCatalog)
        {
            var details = new TextBox
            {
                Name = "JournalCompatibilityDetails", Text = journal.Diagnostics, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Style = (Style)FindResource("Input"), MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            OverviewPanel.Children.Add(new Expander
            {
                Name = "JournalCompatibilityExpander", Header = "Journal compatibility details", Content = details,
                Foreground = (System.Windows.Media.Brush)FindResource("Dim"), Margin = new Thickness(0, 6, 0, 0),
            });
        }

        bool ready = false;
        void Render()
        {
            if (!ready) return;
            _journalCampaign = Convert.ToString(campaign.SelectedItem) ?? "All campaigns";
            _journalCategory = Convert.ToString(category.SelectedItem) ?? "All categories";
            _journalSearch = search.Text.Trim(); _journalIncompleteOnly = incomplete.IsChecked == true;
            grid.ItemsSource = journal.Rows.Where(row =>
                (_journalCampaign == "All campaigns" || row.Campaign == _journalCampaign) &&
                (_journalCategory == "All categories" || row.Category == _journalCategory) &&
                (!_journalIncompleteOnly || !row.StateKnown || !row.Completed) &&
                (_journalSearch.Length == 0 || (row.Name + " " + row.Objective + " " + row.Subtitle).Contains(_journalSearch, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(row => row.Campaign).ThenBy(row => row.Category).ThenBy(row => row.Name).ToArray();
        }
        void Categories()
        {
            bool prior = ready; ready = false;
            string selectedCampaign = Convert.ToString(campaign.SelectedItem) ?? "All campaigns";
            string[] names = new[] { "All categories" }.Concat(journal.Rows.Where(r => selectedCampaign == "All campaigns" || r.Campaign == selectedCampaign).Select(r => r.Category).Distinct().Order()).ToArray();
            category.ItemsSource = names; category.SelectedItem = names.Contains(_journalCategory) ? _journalCategory : names[0];
            ready = prior;
        }
        campaign.SelectionChanged += (_, _) => { Categories(); Render(); };
        category.SelectionChanged += (_, _) => Render(); search.TextChanged += (_, _) => Render();
        incomplete.Checked += (_, _) => Render(); incomplete.Unchecked += (_, _) => Render();
        Categories(); ready = true; Render();
        _refreshCollectionJournalDisplay = () =>
        {
            journal = CollectionJournalState.ReadForDisplay(_container);
            status.Text = StatusText(); Categories(); Render();
        };
        complete.Click += (_, _) =>
        {
            try
            {
                var selected = grid.SelectedItems.OfType<CollectionJournalState.Row>().ToArray();
                // Ctrl+A also selects bonus rows; those must be earned through their dependencies, not awarded directly.
                ulong[] ids = selected.Where(row => row.Supported && !row.Automatic).Select(row => row.Id).ToArray();
                int unavailable = selected.Count(row => !row.Supported);
                if (!journal.CanEdit) { Log("Journal is read-only. See compatibility details below the list.", "info"); return; }
                if (selected.Length > 0 && ids.Length == 0)
                {
                    Log("Select supported unfinished challenges. Missing/changed definitions stay read-only; bonuses are automatic.", "info");
                    return;
                }
                if (!CollectionJournalState.TryPreview(_container, ids, out var preview, out string error))
                    throw new InvalidOperationException(error);
                if (!preview.Changed) { Log("Selected journal challenges were already completed; no points were added", "info"); return; }
                if (!_confirmUnlockTest($"Complete {preview.SelectedCompleted} challenge(s) and {preview.AutomaticCompleted} bonus(es)? "
                    + (preview.RacesUpdated > 0 ? $"Sync {preview.RacesUpdated} linked race result(s). " : "")
                    + (preview.PrStuntsUpdated > 0 ? $"Sync {preview.PrStuntsUpdated} PR stunt(s); new PBs use the selected star cutoff + 0–2%, preserving better scores. " : "")
                    + (preview.JobLevel > 0 ? $"Set Food Delivery to level {preview.JobLevel}, with matching Job XP. " : "")
                    + (preview.BoardsUpdated + preview.MascotsUpdated > 0 ? $"Collect {preview.BoardsUpdated} board(s) and {preview.MascotsUpdated} mascot(s). " : "")
                    + (preview.LandmarksUpdated > 0 ? $"Discover {preview.LandmarksUpdated} permanent landmark(s). " : "")
                    + (preview.RegionsUpdated ? "Discover all ten regions and reveal map fog. " : "")
                    + (preview.StoriesUpdated > 0 ? $"Sync {preview.StoriesUpdated} Story chapter completion/star result(s). Story PB times remain unchanged. " : "")
                    + $"Festival points: {preview.PreviousFestivalPoints:n0} → {preview.FestivalPoints:n0}; Discover Japan: {preview.PreviousDiscoveryPoints:n0} → {preview.DiscoveryPoints:n0}. "
                    + $"Wristband: {ProfileUnlockTestState.Wristbands[preview.PreviousBand].Name} → {ProfileUnlockTestState.Wristbands[preview.Band].Name}. "
                    + (unavailable > 0 ? $"{unavailable} unavailable row(s) will stay unchanged. " : "")
                    + "This changes local progress only; it does not add cars, photos, online results or claimed rewards.")) return;
                if (!CollectionJournalState.TryComplete(_container, ids, out var changed, out error)) throw new InvalidOperationException(error);
                _hasUnlockTestEdits |= changed.Changed;
                RefreshSaveStateDisplays(); BuildCareer(); BuildOverview();
                Log($"Collection Journal: {changed.SelectedCompleted} selected + {changed.AutomaticCompleted} automatic; "
                    + $"Festival {changed.PreviousFestivalPoints:n0} → {changed.FestivalPoints:n0}; Discovery {changed.PreviousDiscoveryPoints:n0} → {changed.DiscoveryPoints:n0}; "
                    + $"wristband {ProfileUnlockTestState.Wristbands[changed.Band].Name}; {changed.RacesUpdated} race result(s), {changed.PrStuntsUpdated} PR stunt(s) synced"
                    + (changed.JobLevel > 0 ? $"; Food Delivery level {changed.JobLevel}" : "") + " — Save edited to export separately", "ok");
                if (changed.BoardsUpdated + changed.MascotsUpdated + changed.LandmarksUpdated + changed.StoriesUpdated > 0 || changed.RegionsUpdated)
                    Log($"World progress: {changed.BoardsUpdated} boards, {changed.MascotsUpdated} mascots, {changed.LandmarksUpdated} landmarks, "
                        + $"regions {(changed.RegionsUpdated ? "revealed" : "unchanged")}, {changed.StoriesUpdated} Story chapter(s). Story finishing-time PBs were not edited.", "info");
                StatusLine.Text = "Journal completion and progression applied to working copy — Save edited to export separately";
            }
            catch (Exception ex) { Fail("complete Collection Journal challenges", ex); }
        };
    }
}
#endif
