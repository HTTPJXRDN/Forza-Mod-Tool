using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FH6CarEditor;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

public sealed partial class Fh2SaveEditorView
{
    CarThumbnailLibrary? _fh2Thumbnails;
    Image? _fh2Preview;
    TextBlock? _fh2PreviewName, _fh2PreviewSource;
    int _fh2ThumbnailRevision, _fh2PreviewRevision;

    void AddFh2ThumbnailColumn()
    {
        var image = new FrameworkElementFactory(typeof(Image));
        image.Name = "Fh2NativeCarThumbnail";
        image.SetValue(FrameworkElement.WidthProperty, 100d); image.SetValue(FrameworkElement.HeightProperty, 56d);
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(Fh2ThumbnailLoaded));
        _cars!.Columns.Add(new DataGridTemplateColumn { Header = "Preview", Width = 110, MinWidth = 110, CanUserResize = false, CellTemplate = new DataTemplate(typeof(CarRow)) { VisualTree = image } });
    }
    async void Fh2ThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        if (image.Tag == null)
        {
            image.Tag = true;
            image.DataContextChanged += (_, _) => _ = LoadFh2RowThumbnailAsync(image);
        }
        await LoadFh2RowThumbnailAsync(image);
    }
    async Task LoadFh2RowThumbnailAsync(Image image)
    {
        image.Source = null;
        if (_disposed || image.DataContext is not CarRow row || _fh2Thumbnails is not { } library) return;
        int revision = _fh2ThumbnailRevision;
        string media = _catalog?.Cars.FirstOrDefault(c => c.Id == row.CarId)?.MediaName ?? "";
        var result = await Task.Run(() => library.Load(row.CarId, media));
        if (_disposed || revision != _fh2ThumbnailRevision || !ReferenceEquals(row, image.DataContext)) return;
        image.Source = result.Image; image.ToolTip = result.Status;
    }
    void BuildFh2ThumbnailControls(Panel parent)
    {
        var panel = new DockPanel { Margin = new(0, 4, 0, 6), LastChildFill = true };
        _fh2Preview = new Image { Width = 220, Height = 124, Stretch = Stretch.Uniform, Margin = new(0, 0, 14, 0) };
        DockPanel.SetDock(_fh2Preview, Dock.Left); panel.Children.Add(_fh2Preview);
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _fh2PreviewName = Text("Select one car for its stock preview."); details.Children.Add(_fh2PreviewName);
        _fh2PreviewSource = Text("Finding FH2 thumbnails…"); details.Children.Add(_fh2PreviewSource);
        var buttons = new WrapPanel();
        Add(buttons, "Thumbnail folder…", async () =>
        {
            var picker = new OpenFolderDialog { Title = "Choose FH2 game, Mount, media or thumbnail folder" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) await ConfigureFh2ThumbnailsAsync(picker.FolderName);
        });
        Add(buttons, "Auto-find", async () => await ConfigureFh2ThumbnailsAsync(resetAuto: true));
        details.Children.Add(buttons); panel.Children.Add(details); parent.Children.Add(panel);
    }
    async Task ConfigureFh2ThumbnailsAsync(string? manual = null, bool resetAuto = false)
    {
        int revision = ++_fh2ThumbnailRevision; ++_fh2PreviewRevision;
        if (_fh2PreviewSource != null) _fh2PreviewSource.Text = "Finding FH2 thumbnails…";
        string? source = _session?.SourcePath;
        try
        {
            if (resetAuto) CarThumbnailLibrary.SaveSource(ForzaGame.Horizon2, null);
            var library = await Task.Run(() =>
            {
                string? preferred = manual ?? (resetAuto ? null : CarThumbnailLibrary.SavedSource(ForzaGame.Horizon2));
                if (preferred != null)
                    try { return new CarThumbnailLibrary(preferred); }
                    catch (Exception ex) when (manual == null && ex is IOException or UnauthorizedAccessException) { }
                foreach (string folder in CarThumbnailLibrary.FindSources(source, ForzaGame.Horizon2))
                    try { return new CarThumbnailLibrary(folder); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return null;
            });
            if (_disposed || revision != _fh2ThumbnailRevision) return;
            if (manual != null && library != null) CarThumbnailLibrary.SaveSource(ForzaGame.Horizon2, library.SourcePath);
            _fh2Thumbnails = library;
            if (_fh2PreviewSource != null)
            {
                _fh2PreviewSource.Text = library == null ? "Choose the FH2 game folder to load stock previews." : $"{library.Count} stock FH2 previews · not saved paint or modifications";
                _fh2PreviewSource.ToolTip = library?.SourcePath;
            }
            RefreshFh2ThumbnailImages(_cars);
            await UpdateFh2PreviewAsync();
        }
        catch (Exception ex)
        {
            if (!_disposed && revision == _fh2ThumbnailRevision && _fh2PreviewSource != null)
            { _fh2PreviewSource.Text = "Thumbnail source unavailable"; _fh2PreviewSource.ToolTip = ex.Message; }
        }
    }
    void RefreshFh2ThumbnailImages(DependencyObject? node)
    {
        if (node == null) return;
        if (node is Image { Name: "Fh2NativeCarThumbnail" } image) _ = LoadFh2RowThumbnailAsync(image);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) RefreshFh2ThumbnailImages(VisualTreeHelper.GetChild(node, i));
    }
    async Task UpdateFh2PreviewAsync()
    {
        if (_disposed || _fh2Preview == null || _fh2PreviewName == null) return;
        int revision = ++_fh2PreviewRevision;
        _fh2Preview.Source = null;
        if (_cars?.SelectedItems.Count != 1 || _cars.SelectedItem is not CarRow row)
        { _fh2PreviewName.Text = "Select one car for its stock preview."; return; }
        _fh2PreviewName.Text = row.Name;
        if (_fh2Thumbnails is not { } library) return;
        string media = _catalog?.Cars.FirstOrDefault(c => c.Id == row.CarId)?.MediaName ?? "";
        var result = await Task.Run(() => library.Load(row.CarId, media));
        if (_disposed || revision != _fh2PreviewRevision || !ReferenceEquals(library, _fh2Thumbnails)) return;
        _fh2Preview.Source = result.Image; _fh2Preview.ToolTip = result.Status;
    }
}
