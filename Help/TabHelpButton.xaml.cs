using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FH6LocalCryptoTool.Help;

public partial class TabHelpButton : UserControl
{
    TabHelpWindow? _helpWindow;

    public static readonly DependencyProperty TopicProperty = DependencyProperty.Register(
        nameof(Topic), typeof(string), typeof(TabHelpButton), new PropertyMetadata("", TopicChanged));

    public string Topic
    {
        get => (string)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    public TabHelpButton()
    {
        InitializeComponent();
        UpdateLabel();
    }

    static void TopicChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((TabHelpButton)sender).UpdateLabel();

    void UpdateLabel()
    {
        if (HelpButton == null) return;
        var topic = TabHelpContent.Find(Topic);
        HelpButton.IsEnabled = topic != null;
        HelpButton.ToolTip = "Help with " + (topic?.Title ?? "this tab");
        AutomationProperties.SetName(HelpButton, "Help with " + (topic?.Title ?? "this tab"));
    }

    void Help_Click(object sender, RoutedEventArgs e)
    {
        if (TabHelpContent.Find(Topic) == null) return;
        var document = TabHelpContent.Load(Topic);
        if (_helpWindow != null)
        {
            _helpWindow.SetDocument(document);
            if (_helpWindow.WindowState == WindowState.Minimized) _helpWindow.WindowState = WindowState.Normal;
            _helpWindow.Activate();
            return;
        }

        var window = new TabHelpWindow(document) { Owner = Window.GetWindow(this) };
        _helpWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_helpWindow, window)) _helpWindow = null; };
        // Owned modeless windows stay with the tool, but do not disable it.
        window.Show();
    }
}
