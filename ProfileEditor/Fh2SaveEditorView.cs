using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using FH6LocalCryptoTool.GarageViewer;

namespace FH6LocalCryptoTool;

/// <summary>Separate FH2 surface: FH6 unlock, Journal, account and XP handlers are never called.</summary>
public sealed partial class Fh2SaveEditorView : UserControl, IDisposable
{
    readonly bool _garageMode;
    readonly Action _back;
    readonly Grid _actions = new(), _body = new();
    readonly TextBlock _path = Text("Choose an FH2/Azimuth revision folder."), _status = Text("Original saves remain untouched.");
    readonly Button _export = Button("Export save"), _revert = Button("Revert edits");
    Fh2SaveRevision? _session;
    GarageCarCatalog? _catalog;
    DataGrid? _cars;
    ListBox? _addList;
    TextBox? _search, _owner, _distance, _speed;
    Button? _applyOwner, _applyDetails;
    TextBlock? _ownerSelection;
    CheckBox? _missing;
    Window? _window;
    bool _busy, _disposed, _refreshingCars;
    public Fh2SaveRevision? Session => _session;
    public bool HasChanges => _session?.HasChanges == true;
    public string StatusMessage => _status.Text;
    public event Action<string>? Exported;

    public Fh2SaveEditorView(bool garageMode, Action back)
    {
        _garageMode = garageMode; _back = back;
        Background = Brush("#1D1D1F"); Foreground = Brush("#F4F4F6"); FontSize = 12;
        Resources[SystemColors.HighlightBrushKey] = Brush("#A51A49");
        Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brush("#622B40");
        // Native disabled/header visuals otherwise turn white on some Windows themes.
        var buttonStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="Shell" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="6" BorderThickness="1" BorderBrush="{TemplateBinding BorderBrush}" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Shell" Property="Background" Value="#542B3D"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Shell" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        Resources[typeof(Button)] = buttonStyle;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Border { Padding = new(20, 11, 20, 11), BorderBrush = Brush("#E8175D"), BorderThickness = new(0, 0, 0, 2), Background = new LinearGradientBrush(Color.FromRgb(58,58,63),Color.FromRgb(48,48,52),90) };
        var dock = new DockPanel { MinHeight = 46 }; var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Right); dock.Children.Add(buttons);
        Add(buttons, "Load FH2 save…", async () => await ChooseAsync());
        buttons.Children.Add(_export); _export.Click += async (_, _) => await ExportAsync();
        buttons.Children.Add(_revert); _revert.Click += async (_, _) => { if (_session != null) await LoadRevisionAsync(_session.SourcePath); };
        Add(buttons, "Back to FH6", () => { if (ConfirmDiscard()) { _back(); Dispose(); } });
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text(garageMode ? "Garage Viewer · FH2" : "Profile Editor · FH2"); title.FontSize = 22; title.FontWeight = FontWeights.Bold; title.Foreground = Brush("#E8175D");
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal }; titleRow.Children.Add(title);
        titleRow.Children.Add(new Help.TabHelpButton { Topic = garageMode ? "GarageViewer" : "ProfileEditor" });
        heading.Children.Add(titleRow); heading.Children.Add(Text("Azimuth saves • private working copy")); dock.Children.Add(heading); header.Child = dock; root.Children.Add(header);
        _actions.Children.Add(_body); Grid.SetRow(_actions, 1); _actions.Margin = new(18); root.Children.Add(_actions);
        var footer = new StackPanel { Margin = new(18, 0, 18, 12) }; footer.Children.Add(_path);
        footer.Children.Add(Text("Close FH2 and Azimuth, then back up your User save folder. Copy the exported commit and whole data folder into BOTH folders inside revisions. Keep their existing names; the revision numbers change. Replacing only one can stop the edited save loading."));
        footer.Children.Add(_status); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root; AllowDrop = true;
        DragOver += (_, e) => { e.Effects = !_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { e.Handled = true; if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } p) await LoadRevisionAsync(p[0]); };
        Loaded += (_, _) => { if (_window != null) return; _window = Window.GetWindow(this); if (_window != null) { _window.Closing += Closing; _window.Closed += Closed; } };
        UpdateEnabled();
    }
    static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    static TextBlock Text(string value) => new() { Text = value, Foreground = Brush("#B8B8C1"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 4) };
    static Button Button(string text) => new() { Content = text, Padding = new(12, 7, 12, 7), Margin = new(3), Background = Brush("#333337"), Foreground = Brush("#F4F4F6"), BorderBrush = Brush("#E8175D"), MinHeight = 32 };
    static TextBox Input(string text = "") => new() { Text = text, Padding = new(8, 5, 8, 5), Margin = new(0, 3, 8, 3), Background = Brush("#232326"), Foreground = Brush("#F4F4F6"), BorderBrush = Brush("#55555B"), SelectionBrush = Brush("#E8175D"), MinWidth = 110 };
    static void Add(Panel panel, string text, Action action) { var b = Button(text); b.Click += (_, _) => action(); panel.Children.Add(b); }
    void UpdateEnabled() { _actions.IsEnabled = !_busy; _export.IsEnabled = !_busy && _session != null; _revert.IsEnabled = !_busy && HasChanges; }
    public bool ConfirmDiscard() => !_busy && (!HasChanges || MessageBox.Show("Discard unexported FH2 edits? The original revision is unchanged.", "FH2 save", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    void Closing(object? sender, CancelEventArgs e) { if (!e.Cancel && !ConfirmDiscard()) e.Cancel = true; }
    void Closed(object? sender, EventArgs e) => Dispose();
    async Task ChooseAsync()
    {
        if (_busy) return;
        var picker = new OpenFolderDialog { Title = "Choose an FH2 revision (or revisions/data folder)" };
        if (picker.ShowDialog() == true) await LoadRevisionAsync(picker.FolderName);
    }
    public async Task LoadRevisionAsync(string path)
    {
        if (_busy || !ConfirmDiscard()) return;
        _busy = true; UpdateEnabled(); _status.Text = "Validating all five files and their manifest…";
        try
        {
            var next = await Task.Run(() => Fh2SaveRevision.Load(path));
            try { if (_garageMode && _catalog == null) _catalog = await Task.Run(() => GarageCarCatalog.Embedded(ForzaGame.Horizon2)); }
            catch { next.Dispose(); throw; }
            var previous = _session; _session = next; previous?.Dispose();
            _path.Text = next.SourcePath;
            if (_garageMode) BuildGarage(); else BuildProfile();
            _status.Text = "Loaded. Apply changes, then Export save.";
        }
        catch (Exception ex) { _status.Text = "Load failed: " + ex.Message; }
        finally { _busy = false; UpdateEnabled(); }
    }
    void Apply(Action action)
    {
        if (_busy || _session == null) return;
        try { action(); _status.Text = "Applied to the working copy. Export save to keep it."; UpdateEnabled(); }
        catch (Exception ex) { _status.Text = "Not applied: " + ex.Message; }
    }
    void BuildProfile()
    {
        _body.Children.Clear(); _body.RowDefinitions.Clear(); _body.ColumnDefinitions.Clear();
        var panel = new StackPanel(); var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; _body.Children.Add(scroll);
        panel.Children.Add(Text("FH2 BALANCES"));
        ValueRow(panel, "Credits", _session!.Credits, v => _session.SetValue("Credits", v));
        ValueRow(panel, "Available skill points", _session.SkillTokens, v => _session.SetSkillTokens(v)).Name = "Fh2SkillPoints";
        ValueRow(panel, "Wheelspins", _session.Wheelspins, v => _session.SetWheelspins(v));
        BuildDriverXp(panel);
        BuildBoards(panel);
        BuildRoads(panel);
        BuildExtraUnlocks(panel);
        BuildChampionships(panel);
        BuildIslandChampionships(panel);
    }
    TextBox ValueRow(Panel panel, string name, uint value, Action<uint> setter, bool enabled = true)
    {
        var row = new Grid { Margin = new(0, 5, 0, 5), MaxWidth = 850, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = enabled };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new(200) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(Text(name)); var box = Input(value.ToString(CultureInfo.InvariantCulture)); Grid.SetColumn(box, 1); row.Children.Add(box);
        var apply = Button("Apply"); Grid.SetColumn(apply, 2); row.Children.Add(apply);
        apply.Click += (_, _) => Apply(() => { if (!uint.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint entered)) throw new FormatException("Enter a whole number from 0 to 4294967295."); setter(entered); }); panel.Children.Add(row); return box;
    }
    void BuildDriverXp(Panel panel)
    {
        panel.Children.Add(Text("FH2 DRIVER XP / LEVEL"));
        string restriction = _session!.DriverXpRestriction;
        if (restriction.Length != 0) { panel.Children.Add(Text($"Saved XP: {_session.Xp:N0} • stored level index: {_session.Level:N0}. " + restriction)); return; }
        var preview = Text(""); preview.Name = "Fh2XpPreview";
        var repair = Text(""); repair.Name = "Fh2XpCounterRepair";
        void RefreshRepair() => repair.Text = _session.DriverXpNeedsCounterRepair ? "Apply XP to repair an earlier level counter." : "";
        var xpBox = ValueRow(panel, "Driver XP", _session.Xp, v => { _session.SetDriverXp(v); RefreshRepair(); }); xpBox.Name = "Fh2XpInput";
        void PreviewXp()
        {
            try
            {
                if (!uint.TryParse(xpBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint xp)) throw new FormatException("Enter a whole XP value.");
                var p = Fh2DriverProgression.Preview(xp);
                preview.Text = $"Preview: level {p.Level:N0} • {p.Wristband} wristband • {p.IntoLevel:N0} / {p.RequiredXp:N0} XP toward the next level.";
            }
            catch (Exception ex) { preview.Text = "Preview unavailable: " + ex.Message; }
        }
        xpBox.TextChanged += (_, _) => PreviewXp(); PreviewXp(); panel.Children.Add(preview);
        RefreshRepair(); panel.Children.Add(repair);
        panel.Children.Add(Text("Level follows XP. No level-up rewards are added."));
    }
    static ComboBox ProgressPicker(string name)
    {
        var groups = new ComboBox { Name = name, MinWidth = 290, Margin = new(0, 3, 8, 3), Padding = new(8, 5, 8, 5), Background = Brush("#232326"), Foreground = Brush("#F4F4F6") };
        groups.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" Content="{TemplateBinding SelectionBoxItem}" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border Background="#232326" BorderBrush="#55555B" BorderThickness="1" CornerRadius="6" Padding="9,7">
                      <DockPanel><TextBlock DockPanel.Dock="Right" Text="▼" Foreground="#E8175D" Margin="12,0,0,0"/><ContentPresenter TextElement.Foreground="#F4F4F6" VerticalAlignment="Center"/></DockPanel>
                    </Border>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                  <Border Background="#232326" BorderBrush="#55555B" BorderThickness="1" CornerRadius="6" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="240"><ItemsPresenter/></ScrollViewer></Border>
                </Popup>
              </Grid>
            </ControlTemplate>
            """);
        groups.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBoxItem">
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="Item" Background="#232326" Padding="10,7"><ContentPresenter TextElement.Foreground="#F4F4F6"/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#622B40"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        return groups;
    }
    void BuildBoards(Panel panel)
    {
        panel.Children.Add(Text("FH2 BONUS BOARDS"));
        string restriction = _session!.BoardRestriction;
        if (restriction.Length != 0) { panel.Children.Add(Text(restriction)); return; }
        var row = new WrapPanel(); panel.Children.Add(row);
        var groups = ProgressPicker("Fh2BoardGroup"); row.Children.Add(groups);
        var complete = Button("Complete board group"); complete.Name = "Fh2CompleteBoards"; row.Children.Add(complete);
        void Refresh()
        {
            string selected = (groups.SelectedItem as ComboBoxItem)?.Tag as string ?? "all"; var boards = _session.Boards(); var island = _session.IslandBoards(); groups.Items.Clear();
            var choices = new List<(string Key, string Label)> { ("all", "All base-game boards"), ("travel", "Fast travel boards"), ("xp_a", "Small XP boards"), ("xp_b", "Medium XP boards"), ("xp_c", "High XP boards") };
            if (island.Count == 25) choices.Add(("island", "Storm Island upgrade boards"));
            foreach (var (key, label) in choices)
            {
                var entries = (key == "island" ? island : boards.Where(b => key == "all" || b.Group == key)).ToArray();
                var item = new ComboBoxItem { Content = $"{label} • {entries.Count(b => b.State == "Collected")} / {entries.Length}", Tag = key, Background = Brush("#232326"), Foreground = Brush("#F4F4F6") }; groups.Items.Add(item); if (key == selected) groups.SelectedItem = item;
            }
        }
        complete.Click += (_, _) => Apply(() => { _session.CompleteBoards((string)((ComboBoxItem)groups.SelectedItem).Tag); Refresh(); }); Refresh();
        panel.Children.Add(Text("Completes selected boards without awarding XP or credits."));
    }
    void BuildRoads(Panel panel)
    {
        panel.Children.Add(Text("FH2 ROAD DISCOVERY"));
        foreach (bool island in new[] { false, true })
        {
            string label = island ? "Storm Island" : "Mainland";
            string restriction = island ? _session!.IslandRoadRestriction : _session!.RoadRestriction;
            if (restriction.Length != 0) { panel.Children.Add(Text(label + ": " + restriction)); continue; }
            uint total = island ? Fh2RoadDiscoveryState.IslandRoadCount : Fh2RoadDiscoveryState.RoadCount;
            var row = new WrapPanel(); panel.Children.Add(row);
            var button = Button(island ? "Discover Storm Island roads" : "Discover mainland roads"); button.Name = island ? "Fh2DiscoverIslandRoads" : "Fh2DiscoverRoads"; row.Children.Add(button);
            var status = Text($"{label}: {(island ? _session.IslandRoadsDiscovered : _session.RoadsDiscovered)} / {total} roads"); status.Name = island ? "Fh2IslandRoadStatus" : "Fh2RoadStatus"; row.Children.Add(status);
            button.Click += (_, _) => Apply(() => { _session.DiscoverAllRoads(island); status.Text = $"{label}: {(island ? _session.IslandRoadsDiscovered : _session.RoadsDiscovered)} / {total} roads"; });
        }
    }
    void BuildExtraUnlocks(Panel panel)
    {
        panel.Children.Add(Text("FH2 LOCAL UNLOCKS"));
        if (_session!.PerkRestriction is { Length: > 0 } perkReason) panel.Children.Add(Text("Perks: " + perkReason));
        else
        {
            var row = new WrapPanel(); panel.Children.Add(row);
            var button = Button("Unlock all perks"); button.Name = "Fh2UnlockPerks"; row.Children.Add(button);
            var status = Text($"{_session.UnlockedPerks} / 25 perks unlocked"); status.Name = "Fh2PerkStatus"; row.Children.Add(status);
            button.Click += (_, _) => Apply(() => { int changed = _session.UnlockAllPerks(); status.Text = $"{_session.UnlockedPerks} / 25 perks unlocked • {changed} added"; });
        }
        panel.Children.Add(Text("Skill-point balance stays unchanged."));
        if (_session.PhotoRestriction is { Length: > 0 } photoReason) panel.Children.Add(Text("Promo: " + photoReason));
        else
        {
            var row = new WrapPanel(); panel.Children.Add(row);
            var button = Button("Mark owned cars photographed"); button.Name = "Fh2PhotographOwnedCars"; row.Children.Add(button);
            var status = Text(""); status.Name = "Fh2PhotoStatus"; row.Children.Add(status);
            void Refresh() { var progress = _session.OwnedPhotoProgress(); status.Text = $"{progress.Photographed} / 351 photographed • {progress.PendingOwned} owned remaining" + (progress.UnknownOwned > 0 ? $" • {progress.UnknownOwned} unsupported skipped" : ""); }
            button.Click += (_, _) => Apply(() => { _session.PhotographOwnedCars(); Refresh(); }); Refresh();
        }
        panel.Children.Add(Text("Promo flags only—no photo images or reward payouts."));
        foreach (bool island in new[] { false, true })
        {
            string label = island ? "Storm Island" : "Mainland";
            if (_session.CameraRestriction(island) is { Length: > 0 } reason) { panel.Children.Add(Text(label + " cameras: " + reason)); continue; }
            var row = new WrapPanel(); panel.Children.Add(row);
            var button = Button("Reveal " + label + " speed cameras"); button.Name = island ? "Fh2RevealIslandCameras" : "Fh2RevealCameras"; row.Children.Add(button);
            var status = Text(""); status.Name = island ? "Fh2IslandCameraStatus" : "Fh2CameraStatus"; row.Children.Add(status);
            void Refresh() { var progress = _session.SpeedCameraProgress(island); status.Text = $"{progress.Discovered} / {progress.Total} speed traps/zones discovered"; }
            button.Click += (_, _) => Apply(() => { _session.RevealSpeedCameras(island); Refresh(); }); Refresh();
        }
        panel.Children.Add(Text("Reveals locations only; completion and personal bests stay unchanged."));
    }
    void BuildChampionships(Panel panel)
    {
        panel.Children.Add(Text("FH2 MAINLAND CHAMPIONSHIPS"));
        string restriction = _session!.ChampionshipRestriction;
        if (restriction.Length != 0) { panel.Children.Add(Text(restriction)); return; }
        var row = new WrapPanel(); panel.Children.Add(row);
        var groups = ProgressPicker("Fh2ChampionshipGroup"); row.Children.Add(groups);
        var complete = Button("Complete championships + races"); complete.Name = "Fh2CompleteChampionships"; row.Children.Add(complete);
        var status = Text(""); status.Name = "Fh2ChampionshipStatus"; panel.Children.Add(status);
        void Refresh()
        {
            int selected = (groups.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
            var progress = _session.MainlandChampionships(); groups.Items.Clear();
            foreach (var (id, label, won, total) in new[] { (0, "All mainland + finales / career repair", progress.Sum(p => p.Won), progress.Sum(p => p.Total)) }
                .Concat(progress.Select(p => (p.DestinationId, p.Name, p.Won, p.Total))))
            {
                var item = new ComboBoxItem { Content = $"{label} • {won} / {total} won", Tag = id, Background = Brush("#232326"), Foreground = Brush("#F4F4F6") };
                groups.Items.Add(item); if (id == selected) groups.SelectedItem = item;
            }
            complete.Content = selected == 0 ? "Complete mainland + finales / repair career" : "Complete championships + races";
            status.Text = $"{progress.Sum(p => p.Won)} / 168 championships • {progress.Sum(p => p.RacesWon)} / 672 races" +
                (_session.MainlandPostStoryComplete ? " • post-story mode" : "");
        }
        complete.Click += (_, _) => Apply(() => {
            var result = _session.CompleteMainlandChampionships((int)((ComboBoxItem)groups.SelectedItem).Tag); Refresh();
            status.Text += $" • {result.Championships} wins / {result.Races} races added";
            if ((int)((ComboBoxItem)groups.SelectedItem).Tag == 0)
                status.Text += " • post-story state synchronized";
        });
        Refresh();
        groups.SelectionChanged += (_, _) => complete.Content = (groups.SelectedItem as ComboBoxItem)?.Tag is int id && id == 0
            ? "Complete mainland + finales / repair career" : "Complete championships + races";
        panel.Children.Add(Text("All mainland includes both finales and post-story repair. One destination leaves the story unchanged. No reward cars or XP."));
    }
    void BuildIslandChampionships(Panel panel)
    {
        panel.Children.Add(Text("FH2 STORM ISLAND CHAMPIONSHIPS"));
        string restriction = _session!.IslandChampionshipRestriction;
        if (restriction.Length != 0) { panel.Children.Add(Text(restriction)); return; }
        var row = new WrapPanel(); panel.Children.Add(row);
        var complete = Button("Complete island championships + races"); complete.Name = "Fh2CompleteIslandChampionships"; row.Children.Add(complete);
        var status = Text(""); status.Name = "Fh2IslandChampionshipStatus"; row.Children.Add(status);
        void Refresh()
        {
            var progress = _session.IslandChampionships();
            status.Text = $"{progress.Won} / {progress.Total} championships • {progress.RacesWon} / {progress.TotalRaces} races";
        }
        complete.Click += (_, _) => Apply(() => {
            var result = _session.CompleteIslandChampionships(); Refresh();
            status.Text += $" • {result.Championships} wins / {result.Races} races added";
        });
        Refresh();
        panel.Children.Add(Text("Excludes the final gauntlet and island story progression. No reward cars or XP."));
    }
    public sealed record CarRow(long Id, long CarId, string Name, string PI, string Status);
    void BuildGarage()
    {
        _body.Children.Clear(); _body.RowDefinitions.Clear(); _body.ColumnDefinitions.Clear();
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto }); _body.RowDefinitions.Add(new()); _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new() { Width = new(300), MinWidth = 200 });
        _search = Input(); _search.ToolTip = "Search owned cars by name, model ID or garage ID"; _search.TextChanged += (_, _) => RefreshCars(); _body.Children.Add(_search);
        _cars = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeaderWidth = 0, RowHeight = 62, SelectionMode = DataGridSelectionMode.Extended, Background = Brush("#232326"), Foreground = Brush("#F4F4F6"), RowBackground = Brush("#29292D"), AlternatingRowBackground = Brush("#333337"), Margin = new(0, 6, 12, 6) };
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#333337"))); headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#F4F4F6"))); headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6))); _cars.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var selection = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true }; selection.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#622B40"))); selection.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); cellStyle.Triggers.Add(selection); _cars.CellStyle = cellStyle;
        AddFh2ThumbnailColumn();
        foreach (string field in new[] { "Name", "CarId", "Id", "PI", "Status" }) _cars.Columns.Add(new DataGridTextColumn { Header = field switch { "Id" => "Garage ID", "CarId" => "Car ID", _ => field }, Binding = new System.Windows.Data.Binding(field), MinWidth = field switch { "Name" => 220, "Status" => 155, "PI" => 60, _ => 85 }, Width = field == "Name" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.SizeToHeader });
        _cars.SelectionChanged += (_, _) => FillDetails(); Grid.SetRow(_cars, 1); _body.Children.Add(_cars);
        var add = new Grid { Margin = new(5, 0, 0, 0) }; add.RowDefinitions.Add(new() { Height = GridLength.Auto }); add.RowDefinitions.Add(new() { Height = GridLength.Auto }); add.RowDefinitions.Add(new()); add.RowDefinitions.Add(new() { Height = GridLength.Auto }); Grid.SetColumn(add, 1); Grid.SetRowSpan(add, 2); _body.Children.Add(add);
        add.Children.Add(Text($"ADD STOCK FH2 CARS ({_catalog!.Cars.Count(c => c.CanAddToGarage && !c.IsTraffic)}) • Ctrl/Shift select"));
        var filter = new StackPanel(); var addSearch = Input(); addSearch.TextChanged += (_, _) => FilterCatalog(addSearch.Text); filter.Children.Add(addSearch);
        _missing = new CheckBox { Content = "Missing cars only", Foreground = Brush("#F4F4F6"), Margin = new(0, 4, 0, 6) }; _missing.Checked += (_, _) => FilterCatalog(addSearch.Text); _missing.Unchecked += (_, _) => FilterCatalog(addSearch.Text); filter.Children.Add(_missing); Grid.SetRow(filter, 1); add.Children.Add(filter);
        _addList = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = "DisplayName", Background = Brush("#232326"), Foreground = Brush("#F4F4F6") }; Grid.SetRow(_addList, 2); add.Children.Add(_addList);
        var addButton = Button("Add selected cars"); addButton.IsEnabled = _session!.CanEditGarage; Grid.SetRow(addButton, 3); add.Children.Add(addButton);
        addButton.Click += async (_, _) => { var ids = _addList.SelectedItems.Cast<GarageCarDefinition>().Select(c => c.Id).ToArray(); await GarageEditAsync(() => _session!.AddStocks(ids, _catalog!)); FilterCatalog(addSearch.Text); };
        var bottom = new StackPanel(); Grid.SetRow(bottom, 2); Grid.SetColumnSpan(bottom, 2); _body.Children.Add(bottom);
        BuildFh2ThumbnailControls(bottom);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        Add(actions, "Duplicate selected", async () => { if (_cars.SelectedItems.Count == 1 && _cars.SelectedItem is CarRow row) await GarageEditAsync(() => _session!.Duplicate(row.Id)); });
        Add(actions, "Remove selected…", async () =>
        {
            var selected = _cars.SelectedItems.Cast<CarRow>().ToArray(); if (selected.Length == 0) return;
            if (MessageBox.Show($"Remove {selected.Length} garage car(s) and their purchased parts from the working copy?", "FH2 garage", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var known = _catalog!.Cars.Select(c => c.Id).ToHashSet();
            var locked = _catalog.Cars.Where(c => c.RemovalLocked).Select(c => c.Id).Concat(_session!.Cars().Where(c => !known.Contains(c.CarId)).Select(c => c.CarId)).ToHashSet();
            await GarageEditAsync(() => _session.Remove(selected.Select(c => c.Id).ToArray(), locked));
        });
        var ownerDetails = new WrapPanel(); bottom.Children.Add(ownerDetails);
        ownerDetails.Children.Add(Text("Original Owner")); _owner = Input(); _owner.MinWidth = 180; _owner.ToolTip = "Display label only, not account identity. An empty value clears the label on the selected cars."; ownerDetails.Children.Add(_owner);
        _applyOwner = Button("Apply Original Owner"); ownerDetails.Children.Add(_applyOwner);
        _applyOwner.Click += async (_, _) =>
        {
            var ids = _cars.SelectedItems.Cast<CarRow>().Select(c => c.Id).ToArray();
            if (ids.Length == 0) return;
            string owner = _owner.Text;
            await GarageEditAsync(() => _session!.UpdateOriginalOwners(ids, owner));
        };
        _ownerSelection = Text("Ctrl/Shift-select cars to batch-edit their owner labels."); ownerDetails.Children.Add(_ownerSelection);
        var details = new WrapPanel(); bottom.Children.Add(details);
        details.Children.Add(Text("Distance")); _distance = Input(); details.Children.Add(_distance); details.Children.Add(Text("Top speed")); _speed = Input(); details.Children.Add(_speed);
        _applyDetails = Button("Apply car fields"); details.Children.Add(_applyDetails);
        _applyDetails.Click += async (_, _) =>
        {
            if (_cars.SelectedItems.Count != 1 || _cars.SelectedItem is not CarRow row) { _status.Text = "Select one garage car for its fields."; return; }
            if (!long.TryParse(_distance.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long distance) || !NumericText.TryParseDouble(_speed.Text, out double speed)) { _status.Text = "Enter valid non-negative distance and speed values."; return; }
            string owner = _owner.Text; await GarageEditAsync(() => _session!.UpdateDetails(row.Id, owner, distance, speed));
        };
        bottom.Children.Add(Text("Raw DB units. Current/pending and barn-find cars are protected. Stock configurations use FH2's embedded DB, not FH6 parts. New cars, duplicated cars and exports still need in-game validation. No FH6 Collection Journal changes are made."));
        if (!_session.CanEditGarage) bottom.Children.Add(Text("Viewing only: " + _session.GarageRestriction));
        RefreshCars(); FilterCatalog("");
        _ = ConfigureFh2ThumbnailsAsync();
    }
    void RefreshCars()
    {
        if (_session == null || _cars == null || _catalog == null) return;
        var selected = _cars.SelectedItems.Cast<CarRow>().Select(c => c.Id).ToHashSet();
        var models = _catalog.Cars.ToDictionary(c => c.Id); string query = _search?.Text.Trim() ?? "";
        var rows = _session.Cars().Select(c => new CarRow(c.Id, c.CarId, models.TryGetValue(c.CarId, out var m) ? m.DisplayName : $"Unknown car ({c.CarId})", (c.PerformanceIndex is > 0 and <= 2 ? c.PerformanceIndex * 1000 : c.PerformanceIndex).ToString("0", CultureInfo.InvariantCulture), c.Protection))
            .Where(c => (c.Name + " " + c.CarId + " " + c.Id).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _refreshingCars = true;
        try { _cars.ItemsSource = rows; foreach (var row in rows.Where(c => selected.Contains(c.Id))) _cars.SelectedItems.Add(row); }
        finally { _refreshingCars = false; FillDetails(); }
    }
    void FilterCatalog(string query)
    {
        if (_addList == null || _catalog == null || _session == null) return;
        var owned = _session.Cars().Select(c => c.CarId).ToHashSet();
        _addList.ItemsSource = _catalog.Cars.Where(c => c.CanAddToGarage && !c.IsTraffic && (_missing?.IsChecked != true || !owned.Contains(c.Id)) && (c.DisplayName + " " + c.Id).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    void FillDetails()
    {
        if (_refreshingCars || _owner == null || _distance == null || _speed == null || _applyOwner == null || _applyDetails == null || _ownerSelection == null) return;
        var selected = _cars!.SelectedItems.Cast<CarRow>().ToArray();
        _ = UpdateFh2PreviewAsync();
        bool single = selected.Length == 1, editable = _session?.CanEditGarage == true;
        _owner.IsEnabled = _applyOwner.IsEnabled = selected.Length > 0 && editable;
        _distance.IsEnabled = _speed.IsEnabled = _applyDetails.IsEnabled = single && editable;
        _applyOwner.Content = selected.Length > 1 ? $"Apply Original Owner to {selected.Length} cars" : "Apply Original Owner";
        var owners = selected.Select(c => Convert.ToString(_session!.Details(c.Id)["OriginalOwner"]) ?? "").Distinct(StringComparer.Ordinal).Take(2).ToArray();
        _owner.Text = owners.Length == 1 ? owners[0] : "";
        _ownerSelection.Text = selected.Length > 1
            ? $"{selected.Length} cars selected{(owners.Length > 1 ? " • mixed owners" : "")}. Only Original Owner is replaced; distance/top speed stay unchanged."
            : "Ctrl/Shift-select cars to batch-edit their owner labels. This is not account identity.";
        if (!single) { _distance.Text = _speed.Text = ""; return; }
        var row = selected[0];
        var d = _session!.Details(row.Id); _owner.Text = Convert.ToString(d["OriginalOwner"]) ?? ""; _distance.Text = Convert.ToString(d["DistanceDriven"], CultureInfo.InvariantCulture) ?? "0"; _speed.Text = Convert.ToString(d["TopSpeed"], CultureInfo.InvariantCulture) ?? "0";
    }
    async Task GarageEditAsync(Action edit)
    {
        if (_busy || _session == null) return; _busy = true; UpdateEnabled();
        try
        {
            Exception? failure = await Task.Run(() => { try { edit(); return (Exception?)null; } catch (Exception ex) { return ex; } });
            if (failure != null) { _status.Text = "Not applied: " + failure.Message; return; }
            RefreshCars(); _status.Text = $"Working copy: {_session.GarageCount:N0} cars. Export save to keep edits.";
        }
        catch (Exception ex) { _status.Text = "Not applied: " + ex.Message; }
        finally { _busy = false; UpdateEnabled(); }
    }
    async Task ExportAsync()
    {
        if (_busy || _session == null) return;
        try
        {
            string initial = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!Directory.Exists(initial)) initial = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var picker = new OpenFolderDialog { Title = "Export FH2 save — choose Desktop/Documents, NOT the live User/revisions folder", InitialDirectory = initial };
            if (picker.ShowDialog() == true) await ExportToFolderAsync(picker.FolderName);
        }
        catch (Exception ex) { _status.Text = "Export could not start: " + ex.Message; }
    }

    /// <summary>The same async export path used by the button, also exercised by UI regression tests.</summary>
    public async Task<string?> ExportToFolderAsync(string parent)
    {
        if (_busy || _session == null || _disposed) return null;
        var session = _session;
        try
        {
            string destination = Path.Combine(Path.GetFullPath(parent), "FH2-save-edited-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
            if (!session.TryValidateExportDestination(destination, out string reason))
            { _status.Text = "Export not started: " + reason; return null; }
            _busy = true; UpdateEnabled(); _status.Text = "Exporting complete revision and verifying its manifest…";
            // Catch in the worker itself. Otherwise VS Just My Code can break
            // as 'user-unhandled' before the awaiting UI catch receives the error.
            Exception? failure = await Task.Run(() =>
            {
                try { session.Export(destination); return (Exception?)null; }
                catch (Exception ex) { return ex; }
            });
            if (failure != null) { _status.Text = "Export failed: " + failure.Message; return null; }
            _status.Text = "Exported: " + destination;
            Exported?.Invoke(destination); return destination;
        }
        catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; return null; }
        finally { _busy = false; UpdateEnabled(); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_window != null) { _window.Closing -= Closing; _window.Closed -= Closed; }
        _session?.Dispose(); _catalog?.Dispose();
    }
}
