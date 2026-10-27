using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

/// <summary>Keeps FH2 state/UI isolated without rebuilding either existing FH6 tab.</summary>
public sealed class Fh2SaveHost
{
    readonly FrameworkElement _normal;
    readonly Grid _root = new();
    readonly bool _garage;
    Fh2SaveEditorView? _view;
    public Fh2SaveHost(UserControl owner, bool garage)
    {
        _garage = garage; _normal = (FrameworkElement)owner.Content;
        owner.Content = null; _root.Children.Add(_normal); owner.Content = _root;
    }
    public async Task ChooseAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose FH2/Azimuth revision, revisions, or data folder" };
        if (dialog.ShowDialog() == true) await OpenAsync(dialog.FolderName);
    }
    public async Task OpenAsync(string path)
    {
        if (_view == null)
        {
            _view = new Fh2SaveEditorView(_garage, () =>
            {
                _normal.Visibility = Visibility.Visible;
                if (_view != null) _root.Children.Remove(_view);
                _view = null;
            });
            _normal.Visibility = Visibility.Collapsed; _root.Children.Add(_view);
        }
        await _view.LoadRevisionAsync(path);
    }
}
