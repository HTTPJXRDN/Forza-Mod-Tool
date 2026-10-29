using System.Windows;
using System.Windows.Automation;

namespace FH6LocalCryptoTool.Help;

public partial class TabHelpWindow : Window
{
    internal TabHelpWindow(TabHelpDocument document)
    {
        InitializeComponent();
        SetDocument(document);
    }

    internal void SetDocument(TabHelpDocument document)
    {
        Title = document.Title + " help";
        HelpTitle.Text = Title;
        HelpText.Text = document.Text;
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Window_StateChanged(object? sender, EventArgs e)
    {
        if (HelpMaximizeButton != null)
        {
            bool maximized = WindowState == WindowState.Maximized;
            HelpMaximizeButton.Content = maximized ? "\uE923" : "\uE922";
            HelpMaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
            AutomationProperties.SetName(HelpMaximizeButton, maximized ? "Restore help" : "Maximize help");
        }
        if (HelpRootBorder != null)
            HelpRootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
