using System.Windows;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class UpdateCompleteWindow : Window
{
    internal UpdateCompleteWindow(Window owner, Version previous)
    {
        InitializeComponent();
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Icon = owner.Icon;
        VersionText.Text = $"Forza Mod Tool v{ReleaseUpdates.InstalledVersionText} · Changes since v{previous}";
        ChangesText.Text = UpdateReleaseNotes.Since(previous);
    }

    private void CloseNotice(object sender, RoutedEventArgs e) => Close();
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
