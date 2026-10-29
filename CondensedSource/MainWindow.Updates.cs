using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    Func<Task<ReleaseUpdateResult>> _checkForUpdates = ReleaseUpdates.CheckAsync;
    bool _checkingForUpdates;
    bool _downloadingUpdate;
    ReleaseUpdateResult? _availableUpdate;
    StagedReleaseUpdate? _stagedUpdate;
    Func<ReleaseUpdateResult, string, IProgress<int>, Task<StagedReleaseUpdate>> _prepareUpdate =
        (release, executable, progress) => ReleaseUpdateInstaller.PrepareAsync(release, executable, progress);
    Action<StagedReleaseUpdate> _startUpdateInstaller = ReleaseUpdateInstaller.StartHelper;
    Action _shutdownForUpdate = () => Application.Current.Shutdown();
    Action? _openUpdatePage = null;

    public string AppVersionLabel => "V" + ReleaseUpdates.InstalledVersionText;

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    internal async Task CheckForUpdatesAsync()
    {
        MainTabs.ApplyTemplate();
        var button = (Button)MainTabs.Template.FindName("CheckUpdatesButton", MainTabs);
        var notice = (Border)MainTabs.Template.FindName("UpdateNotice", MainTabs);
        var status = (TextBlock)MainTabs.Template.FindName("UpdateStatus", MainTabs);
        var download = (TextBlock)MainTabs.Template.FindName("UpdateDownloadText", MainTabs);
        if (_checkingForUpdates || _downloadingUpdate) return;
        _checkingForUpdates = true;
        button.IsEnabled = false;
        button.Content = "Checking…";
        notice.Visibility = Visibility.Visible;
        status.Text = "Checking for updates…";
        status.Foreground = (System.Windows.Media.Brush)FindResource("Dim");
        download.Visibility = Visibility.Collapsed;
        _availableUpdate = null;
        _stagedUpdate = null;
        SetDownloadLabel("Download update now");
        try
        {
            var result = await _checkForUpdates();
            _availableUpdate = result.UpdateAvailable ? result : null;
            status.Text = result.UpdateAvailable ? "Update available: v" + result.Version : "No new updates";
            status.Foreground = (System.Windows.Media.Brush)FindResource(result.UpdateAvailable ? "Pink" : "Dim");
            download.Visibility = result.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            status.Text = "Couldn't check for updates. Try again later.";
            Log("Update check: " + ex.Message);
        }
        finally
        {
            button.Content = "Check for updates";
            button.IsEnabled = true;
            _checkingForUpdates = false;
        }
    }

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e) => await DownloadOrInstallUpdateAsync();

    internal async Task DownloadOrInstallUpdateAsync()
    {
        if (_downloadingUpdate || _availableUpdate == null) return;
        var button = (Button)MainTabs.Template.FindName("CheckUpdatesButton", MainTabs);
        var status = (TextBlock)MainTabs.Template.FindName("UpdateStatus", MainTabs);
        var link = (Hyperlink)MainTabs.Template.FindName("UpdateDownloadLink", MainTabs);
        if (_availableUpdate.Asset == null)
        {
            status.Text = "This release needs a manual download. Opening GitHub…";
            if (_openUpdatePage != null) _openUpdatePage();
            else OpenHeaderLink(ReleaseUpdates.LatestReleaseUrl, "Download update");
            return;
        }
        try
        {
            if (_stagedUpdate != null)
            {
                _startUpdateInstaller(_stagedUpdate);
                _shutdownForUpdate();
                return;
            }
            _downloadingUpdate = true;
            button.IsEnabled = false;
            link.IsEnabled = false;
            status.Text = "Downloading update…";
            var progress = new Progress<int>(percent => { if (_downloadingUpdate && _stagedUpdate == null) status.Text = $"Downloading update… {percent}%"; });
            _stagedUpdate = await _prepareUpdate(_availableUpdate,
                Environment.ProcessPath ?? "", progress);
            status.Text = "Update downloaded. Export any unsaved edits, then restart to install.";
            SetDownloadLabel("Restart to install update");
        }
        catch (Exception ex)
        {
            status.Text = "Couldn't prepare the update. Current files are unchanged. Try again.";
            Log("Update download/install: " + ex.Message);
        }
        finally
        {
            _downloadingUpdate = false;
            button.IsEnabled = true;
            link.IsEnabled = true;
        }
    }

    void SetDownloadLabel(string text)
    {
        var link = (Hyperlink)MainTabs.Template.FindName("UpdateDownloadLink", MainTabs);
        link.Inlines.Clear(); link.Inlines.Add(new Run(text));
    }
}
