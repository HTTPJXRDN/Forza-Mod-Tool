#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    string _unlockTestInputPath;
    bool _hasUnlockTestEdits;
    Func<string, bool> _confirmUnlockTest;

    sealed record UnlockItem(ulong Id, string Name, string Category, bool Unlocked, bool Selectable = true, string Detail = "", string Suffix = "")
    {
        public string Display => (Unlocked ? "Owned · " : "Locked · ") + Name + (Category.Length == 0 ? "" : " — " + Category)
            + (Suffix.Length == 0 ? "" : " · " + Suffix) + (Selectable ? "" : " (definition changed; preserved)");
    }

    void AddUnlockTestingControls()
    {
        OverviewPanel.Children.Add(SectionHeader("Unlocks & progression"));
        OverviewPanel.Children.Add(Note("Search and choose items below each button. Ctrl+A in a list selects its visible items. Save edited exports a separate file."));
        AddClothingUnlockControls();
        AddHornUnlockControls();
        AddEmoteUnlockControls();
        AddForzaLinkUnlockControls();
        AddBadgeUnlockControls();
        AddCollectionJournalControls();
    }

    Button TestButton(string name, string text, bool enabled) => new()
    {
        Name = name, Content = text, Style = (Style)FindResource("PinkButton"),
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
        IsEnabled = enabled, Opacity = enabled ? 1 : 0.45,
    };

    void AddClothingUnlockControls() => AddUnlockPicker("Clothing", "UnlockAllClothesButton", "clothes",
        () => CharacterClothingUnlockState.TryReadUnlocked(_container, out var ids)
            ? CharacterClothingUnlockState.AllClothing.Where(c => !CharacterClothingUnlockState.IsEntitlementManaged(c.Id))
                .Select(c => new UnlockItem(c.Id, ProfileInventoryNames.Get("Clothes", c.Id, c.Name), ClothingCategory(c.Slot), ids.Contains(c.Id))).ToArray() : null,
        selected => { bool ok = CharacterClothingUnlockState.TryUnlockSelected(_container, selected, out var result); return (ok, result.Changed); },
        "The two VIP-managed items, your outfit and existing NEW flags stay unchanged.");

    void AddHornUnlockControls() => AddUnlockPicker("Horn", "UnlockAllHornsButton", "horns",
        () => CarHornUnlockState.TryReadUnlocked(_container, out var ids)
            ? CarHornUnlockState.AllHorns.Select(h => new UnlockItem(h.Id, ProfileInventoryNames.Get("Horns", h.Id, h.Name),
                h.Category switch { 0 => "Standard", 1 => "Musical", _ => "Sound effects" }, ids.Contains(h.Id),
                Detail: CollectionJournalState.RewardRequirementDescription(JournalRewardKind.Horn, h.Id),
                Suffix: CollectionJournalState.RewardRequirementDescription(JournalRewardKind.Horn, h.Id).StartsWith("Journal:") ? "Journal linked" : "")).ToArray() : null,
        selected => ApplyJournalRewards(JournalRewardKind.Horn, selected),
        "Journal-linked selections also complete their requirements. Your equipped horn stays unchanged.",
        linkedPreview: selected => PreviewJournalRewards(JournalRewardKind.Horn, selected));

    void AddEmoteUnlockControls() => AddUnlockPicker("Emote", "UnlockAllEmotesButton", "emotes",
        () => EmoteUnlockState.TryReadUnlocked(_container, out var ids)
            ? EmoteUnlockState.AllEmotes.Select(e => new UnlockItem(e.Id, ProfileInventoryNames.Get("Emotes", e.Id, e.Name), "", ids.Contains(e.Id))).ToArray() : null,
        selected => { bool ok = EmoteUnlockState.TryUnlockSelected(_container, selected, out int changed); return (ok, changed); },
        "All three equipped emotes stay unchanged.");

    void AddForzaLinkUnlockControls() => AddUnlockPicker("ForzaLink", "UnlockForzaLinkPhrasesButton", "Forza Link phrases",
        () => ForzaLinkPhraseUnlockState.TryReadUnlocked(_container, out var ids)
            ? ForzaLinkPhraseUnlockState.AllRewardPhrases
                .Select(p => new UnlockItem(p.Id, p.Name, p.JournalReward ? "Journal reward" : "Other reward", ids.Contains(p.Id),
                    Detail: CollectionJournalState.RewardRequirementDescription(JournalRewardKind.Phrase, p.Id))).ToArray() : null,
        selected => ApplyJournalRewards(JournalRewardKind.Phrase, selected),
        "Journal-linked selections also complete their requirements. Your chat slots stay unchanged.",
        linkedPreview: selected => PreviewJournalRewards(JournalRewardKind.Phrase, selected));

    (bool Success, int Changed) ApplyJournalRewards(JournalRewardKind kind, ulong[] selected)
    {
        if (!CollectionJournalState.TryUnlockRewards(_container, kind, selected, out var change, out string reason)) throw new InvalidDataException(reason);
        return (true, change.Changed ? Math.Max(1, change.Added) : 0);
    }

    (bool Changed, string Detail) PreviewJournalRewards(JournalRewardKind kind, ulong[] selected)
    {
        if (!CollectionJournalState.TryPreviewUnlockRewards(_container, kind, selected, out var change, out string reason)) throw new InvalidDataException(reason);
        var j = change.Journal;
        return (change.Changed, change.LinkedRewards == 0 ? "" :
            $"\n\nJournal requirements: {change.Requirements}.\n{j.SelectedCompleted:n0} challenges and {j.AutomaticCompleted:n0} automatic bonuses will complete. " +
            $"Native updates: {j.RacesUpdated} races, {j.PrStuntsUpdated} PR stunts, {j.StoriesUpdated} Story chapters, {j.BoardsUpdated} boards, {j.MascotsUpdated} mascots and {j.LandmarksUpdated} landmarks. " +
            (j.JobLevel > 0 ? $"Food Delivery level {j.JobLevel}. " : "") + (j.RegionsUpdated ? "Regions discovered. " : "") +
            "Points and wristbands update automatically. This edits local progress only: no online records, photos, cars, purchases or reward claims are created. " +
            "Mapped PR PBs use the required star cutoff + 0–2%; better PBs are preserved. Stories use completion and stars, not invented times.");
    }

    static string ClothingCategory(string slot) => slot switch
    {
        "UpperBodyLayer1" => "Tops", "UpperBodyLayer2" => "Jackets", "LowerBody" => "Legs",
        "Socks" => "Ankles", "Hat" => "Hats", "Eye" => "Eyes", "LeftWrist" => "Wrist",
        "Dress" => "Dresses", "Outfit" => "Outfits", _ => slot,
    };

    void AddUnlockPicker(string kind, string buttonName, string title, Func<UnlockItem[]> read,
        Func<ulong[], (bool Success, int Changed)> apply, string preservation,
        Action<StackPanel, ListBox, Action> addDetails = null, Func<ulong[], string> preview = null,
        Func<ulong[], (bool Changed, string Detail)> linkedPreview = null)
    {
        var rows = read();
        var button = TestButton(buttonName, "Unlock selected " + title, false);
        button.ToolTip = "Choose items in the list below. Ctrl-click for multiple; Ctrl+A in the list selects visible items only.";
        var toggle = new ToggleButton
        {
            Name = kind + "UnlockPickerToggle", Content = "Choose " + title + " · 0 selected",
            Style = (Style)FindResource("UnlockDropdownToggle"), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = rows != null, IsChecked = false,
        };
        var list = new ListBox
        {
            Name = kind + "UnlockList", SelectionMode = SelectionMode.Extended, MaxHeight = 260,
            MinHeight = 110, Background = (System.Windows.Media.Brush)FindResource("Card2"),
            Foreground = (System.Windows.Media.Brush)FindResource("Ink"), BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)FindResource("UnlockListItem"), DisplayMemberPath = "Display",
            IsEnabled = rows != null, IsTextSearchEnabled = true,
        };
        TextSearch.SetTextPath(list, "Name");
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var search = new TextBox
        {
            Name = kind + "UnlockSearch", Style = (Style)FindResource("Input"),
            Margin = new Thickness(4, 0, 4, 4), IsEnabled = rows != null,
            ToolTip = "Search names, categories or descriptions. Filtering clears hidden selections. Esc clears the search.",
        };
        var searchLabel = Note("Search " + title); searchLabel.Margin = new Thickness(4, 0, 4, 2);
        var matches = Note(""); matches.Name = kind + "UnlockSearchStatus"; matches.Margin = new Thickness(4, 0, 4, 4);
        var panel = new StackPanel();
        panel.Children.Add(searchLabel); panel.Children.Add(search); panel.Children.Add(matches); panel.Children.Add(list);
        var dropdown = new Border
        {
            Name = kind + "UnlockDropdown", Child = panel, BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
            Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed,
        };
        dropdown.SetBinding(VisibilityProperty, new Binding("IsChecked") { Source = toggle, Converter = new BooleanToVisibilityConverter() });
        var status = Note(""); status.Name = kind + "UnlockStatus";
        OverviewPanel.Children.Add(button);
        OverviewPanel.Children.Add(toggle);
        OverviewPanel.Children.Add(dropdown);
        OverviewPanel.Children.Add(status);

        void SelectionChanged()
        {
            // Unsupported badge definitions cannot be selected, including via Ctrl+A.
            foreach (var item in list.SelectedItems.OfType<UnlockItem>().Where(i => !i.Selectable).ToArray()) list.SelectedItems.Remove(item);
            int count = list.SelectedItems.Count;
            toggle.Content = "Choose " + title + " · " + count + " selected";
            button.IsEnabled = rows != null && count > 0; button.Opacity = button.IsEnabled ? 1 : 0.45;
        }
        void Filter()
        {
            var selected = list.SelectedItems.OfType<UnlockItem>().Select(i => i.Id).ToHashSet();
            string query = search.Text.Trim();
            list.ItemsSource = rows?.Where(r => query.Length == 0 ||
                    (r.Name + " " + r.Category + " " + r.Detail).Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Category).ThenBy(r => r.Name).ThenBy(r => r.Id).ToArray();
            if (rows != null) foreach (var item in list.Items.OfType<UnlockItem>().Where(i => i.Selectable && selected.Contains(i.Id))) list.SelectedItems.Add(item);
            matches.Text = rows == null ? "Search unavailable."
                : list.Items.Count + " / " + rows.Length + " shown · Ctrl+A in the list selects visible items";
            SelectionChanged();
        }
        void Refresh()
        {
            rows = read();
            toggle.IsEnabled = list.IsEnabled = search.IsEnabled = rows != null;
            status.Text = rows == null ? "This inventory layout is not safely recognized; unlocking is disabled."
                : rows.Count(r => r.Unlocked) + " / " + rows.Length + " owned. " + preservation;
            Filter();
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        list.SelectionChanged += (_, _) => SelectionChanged();
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll,
            (_, e) => { list.SelectedItems.Clear(); foreach (var item in list.Items.OfType<UnlockItem>().Where(i => i.Selectable)) list.SelectedItems.Add(item); e.Handled = true; },
            (_, e) => { e.CanExecute = list.IsEnabled && list.Items.Count > 0; e.Handled = true; }));
        list.InputBindings.Add(new KeyBinding(ApplicationCommands.SelectAll, Key.A, ModifierKeys.Control));
        button.Click += (_, _) =>
        {
            try
            {
                var chosen = list.SelectedItems.OfType<UnlockItem>().Where(i => i.Selectable).Select(i => i.Id).ToHashSet();
                var latest = read();
                if (latest == null) throw new InvalidDataException("The inventory layout is not safely recognized. Nothing was changed.");
                var selected = latest.Where(i => i.Selectable && chosen.Contains(i.Id)).ToArray();
                if (selected.Length == 0) { Log("Choose " + title + " in the list below the button.", "info"); return; }
                int pending = selected.Count(i => !i.Unlocked);
                var selection = selected.Select(i => i.Id).ToArray();
                var linked = linkedPreview?.Invoke(selection) ?? default;
                if (pending == 0 && !linked.Changed) { Log("Selected " + title + " are already unlocked; nothing changed.", "info"); return; }
                string details = (preview?.Invoke(selection) ?? "") + linked.Detail;
                if (!_confirmUnlockTest("Unlock " + pending + " selected " + title + "? " + preservation
                    + " Account/DLC licenses and reward claims are unchanged. Save edited exports separately." + details)) return;
                var result = apply(selection);
                if (!result.Success) throw new InvalidDataException("The selected items are not safely supported. Nothing was changed.");
                _hasUnlockTestEdits |= result.Changed > 0;
                Refresh();
                RefreshUnlockTestDisplays(linkedPreview == null ? title + ": " + result.Changed + " selected items unlocked" : title + ": selected inventory and linked Journal progress applied");
                if (linkedPreview != null) { RefreshSaveStateDisplays(); _refreshCollectionJournalDisplay?.Invoke(); }
            }
            catch (Exception ex) { Fail("unlock selected " + title, ex); }
        };
        addDetails?.Invoke(panel, list, Refresh);
        Refresh();
    }

    void RefreshUnlockTestDisplays(string message)
    {
        BuildCareer();
        Log(message + " — Save edited to export a separate file", "ok");
        StatusLine.Text = "Unlock changes applied to the working copy — Save edited to export separately";
    }
}
#endif
