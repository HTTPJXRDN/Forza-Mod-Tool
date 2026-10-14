using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FH6ProfileEditor;

/// <summary>Themed, modal profile-editor confirmation/error; presenting it never edits a save.</summary>
public sealed class ProfileMessageWindow : Window
{
    public ProfileMessageWindow(string title, string message, bool confirmation, ResourceDictionary theme)
    {
        Resources.MergedDictionaries.Add(theme);
        Title = title;
        Width = Math.Min(640, Math.Max(320, SystemParameters.WorkArea.Width - 48));
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 64);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Background = (Brush)FindResource("WinBg");
        Foreground = (Brush)FindResource("Ink");

        var layout = new Grid { Name = "ProfileDialogLayout" };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = new Border
        {
            Name = "ProfileDialogSurface", Background = (Brush)FindResource("AppGrad"),
            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = layout,
        };

        var titleGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(22, 22, 24)) };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        titleGrid.Children.Add(new TextBlock
        {
            Text = title, Foreground = (Brush)FindResource("Dim"), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = new Button
        {
            Name = "ProfileDialogClose", Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close",
        };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1); titleGrid.Children.Add(close);
        titleGrid.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        layout.Children.Add(titleGrid);
        var accent = new Border { Background = (Brush)FindResource("Pink") };
        Grid.SetRow(accent, 1); layout.Children.Add(accent);

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 0) };
        body.Children.Add(new TextBlock
        {
            Name = "ProfileDialogHeading", Text = confirmation ? "Apply changes?" : "Something went wrong",
            FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        var messageText = new TextBlock
        {
            Name = "ProfileDialogMessage", Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Ink"), Margin = new Thickness(0, 12, 12, 0),
        };
        body.Children.Add(new ScrollViewer
        {
            Name = "ProfileDialogScroll", Content = messageText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(100, Math.Min(350, SystemParameters.WorkArea.Height - 280)),
        });
        Grid.SetRow(body, 2); layout.Children.Add(body);

        if (confirmation)
        {
            var warning = new Border
            {
                Name = "ProfileDialogWarning", Background = (Brush)FindResource("Card2"),
                BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(24, 16, 24, 0),
                Child = new TextBlock
                {
                    Text = "Keep a backup of your entire save and close the game before using an edited export.",
                    Foreground = (Brush)FindResource("Dim"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                },
            };
            Grid.SetRow(warning, 3); layout.Children.Add(warning);
        }
        var buttons = new StackPanel
        {
            Name = "ProfileDialogButtons", Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 20, 24, 22),
        };
        if (confirmation)
        {
            var cancel = new Button
            {
                Name = "ProfileDialogCancel", Content = "Cancel", Style = (Style)FindResource("GhostButton"),
                IsCancel = true, Margin = new Thickness(0, 0, 10, 0), MinWidth = 90,
            };
            cancel.Click += (_, _) => DialogResult = false; buttons.Children.Add(cancel);
        }
        var accept = new Button
        {
            Name = "ProfileDialogAccept", Content = confirmation ? "Apply changes" : "OK",
            Style = (Style)FindResource("PinkFilled"), IsDefault = true, MinWidth = 90,
        };
        accept.Click += (_, _) => DialogResult = true; buttons.Children.Add(accept);
        Grid.SetRow(buttons, 4); layout.Children.Add(buttons);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            DialogResult = false; e.Handled = true;
        };
    }

    public static bool Show(FrameworkElement origin, string title, string message, bool confirmation)
    {
        var dialog = new ProfileMessageWindow(title, message, confirmation, origin.Resources);
        var owner = GetWindow(origin);
        if (owner is not null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
}
