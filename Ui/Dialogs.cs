using System.Windows;
using System.Windows.Controls;

namespace DiskTree.Ui;

/// <summary>Message boxes with the application title; the owner may be null (then the box has no owner window).</summary>
public static class Dialogs
{
    public const string Title = "DiskTree";

    public static void Info(Window? owner, string text, string title = Title) =>
        Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(Window? owner, string text, string title = Title) =>
        Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Yes / No question. True when the user chose Yes.</summary>
    public static bool Confirm(Window? owner, string text, MessageBoxImage icon = MessageBoxImage.Question, string title = Title) =>
        Show(owner, text, title, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;

    /// <summary>
    /// The start question (administrator, installation): heading, text, two buttons and "Nicht mehr fragen".
    /// Returns true for the first button. <paramref name="dontAskAgain"/> is only meaningful for the second one.
    /// </summary>
    public static bool AskAtStart(Window owner, string heading, string text, string yesText, string noText, out bool dontAskAgain)
    {
        bool yes = false, never = false;

        var neverBox = new CheckBox { Content = "Nicht mehr fragen", Margin = new Thickness(0, 14, 0, 0) };
        var yesBtn = new Button { Content = yesText, Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var noBtn = new Button { Content = noText, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var dlg = new Window
        {
            Title = Title,
            Owner = owner,
            Icon = owner.Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new TextBlock { Text = heading, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
                    neverBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 18, 0, 0), Children = { yesBtn, noBtn },
                    },
                },
            },
        };

        yesBtn.Click += (_, _) => { yes = true; dlg.Close(); };
        noBtn.Click += (_, _) => { never = neverBox.IsChecked == true; dlg.Close(); };
        dlg.ShowDialog();

        dontAskAgain = never;
        return yes;
    }

    private static MessageBoxResult Show(Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage icon) =>
        owner == null
            ? MessageBox.Show(text, title, buttons, icon)
            : MessageBox.Show(owner, text, title, buttons, icon);
}
