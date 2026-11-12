using System.Windows;

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

    private static MessageBoxResult Show(Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage icon) =>
        owner == null
            ? MessageBox.Show(text, title, buttons, icon)
            : MessageBox.Show(owner, text, title, buttons, icon);
}
