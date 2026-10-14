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

    void AddCollectionJournalControls()
    {
        var heading = SectionHeader("Collection Journal"); heading.Name = "CollectionJournalHeading";
        OverviewPanel.Children.Add(heading);
        if (!CollectionJournalState.TryRead(_container, out var journal, out string reason))
        {
            var unsupported = Note(reason); unsupported.Name = "CollectionJournalStatus";
            OverviewPanel.Children.Add(unsupported); return;
        }
        var status = Note($"{journal.Completed:n0} / {journal.Rows.Length:n0} completed · "
            + $"Festival: {journal.FestivalPoints:n0} · Discover Japan: {journal.DiscoveryPoints:n0}"
            + (journal.TotalsAgree ? "" : " · Points will sync when you complete challenges."));
        status.Name = "CollectionJournalStatus"; OverviewPanel.Children.Add(status);
        var help = Note("Select rows (Ctrl-click for more, Ctrl+A for all), then click Complete selected challenges. Points, bonuses and wristbands update automatically.");
        help.Name = "CollectionJournalHelp"; OverviewPanel.Children.Add(help);

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
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Done", Binding = new Binding("Completed"), Width = 50, ElementStyle = statusCheckBoxStyle });
        grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new Binding("Category"), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Challenge", Binding = new Binding("Name"), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Objective", Binding = new Binding("Objective"), Width = new DataGridLength(1.7, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Points", Binding = new Binding("Points"), Width = 65 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Auto bonus", Binding = new Binding("Automatic"), Width = 90, ElementStyle = statusCheckBoxStyle });
        OverviewPanel.Children.Add(grid);
        var complete = TestButton("CompleteJournalSelectionButton", "Complete selected challenges", true);
        OverviewPanel.Children.Add(complete);

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
                (!_journalIncompleteOnly || !row.Completed) &&
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
        complete.Click += (_, _) =>
        {
            try
            {
                var selected = grid.SelectedItems.OfType<CollectionJournalState.Row>().ToArray();
                // Ctrl+A also selects bonus rows; those must be earned through their dependencies, not awarded directly.
                ulong[] ids = selected.Where(row => !row.Automatic).Select(row => row.Id).ToArray();
                if (selected.Length > 0 && ids.Length == 0)
                {
                    Log("Bonuses complete automatically. Select their unfinished challenges instead.", "info");
                    return;
                }
                if (!CollectionJournalState.TryPreview(_container, ids, out var preview, out string error))
                    throw new InvalidOperationException(error);
                if (!preview.Changed) { Log("Selected journal challenges were already completed; no points were added", "info"); return; }
                if (!_confirmUnlockTest($"Complete {preview.SelectedCompleted} challenge(s) and {preview.AutomaticCompleted} bonus(es)? "
                    + $"Festival points: {preview.PreviousFestivalPoints:n0} → {preview.FestivalPoints:n0}; Discover Japan: {preview.PreviousDiscoveryPoints:n0} → {preview.DiscoveryPoints:n0}. "
                    + $"Wristband: {ProfileUnlockTestState.Wristbands[preview.PreviousBand].Name} → {ProfileUnlockTestState.Wristbands[preview.Band].Name}. "
                    + "This changes local journal progress only; it does not add cars, photos, online results or claimed rewards.")) return;
                if (!CollectionJournalState.TryComplete(_container, ids, out var changed, out error)) throw new InvalidOperationException(error);
                _hasUnlockTestEdits |= changed.Changed;
                RefreshSaveStateDisplays(); BuildCareer(); BuildOverview();
                Log($"Collection Journal: {changed.SelectedCompleted} selected + {changed.AutomaticCompleted} automatic; "
                    + $"Festival {changed.PreviousFestivalPoints:n0} → {changed.FestivalPoints:n0}; Discovery {changed.PreviousDiscoveryPoints:n0} → {changed.DiscoveryPoints:n0}; "
                    + $"wristband {ProfileUnlockTestState.Wristbands[changed.Band].Name} — Save edited to export separately", "ok");
                StatusLine.Text = "Journal completion and progression applied to working copy — Save edited to export separately";
            }
            catch (Exception ex) { Fail("complete Collection Journal challenges", ex); }
        };
    }
}
#endif
