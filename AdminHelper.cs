using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;

namespace DiskTree;

public static class AdminHelper
{
    private static readonly string SuppressFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskTree", "no-admin-prompt");

    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool PromptSuppressed => File.Exists(SuppressFile);

    public static void SuppressPrompt()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SuppressFile)!);
            File.WriteAllText(SuppressFile, "");
        }
        catch { /* not being able to remember the choice is harmless */ }
    }

    /// <summary>
    /// Starts this program again with administrator rights (UAC prompt). Returns false when the user
    /// declined the UAC prompt, in which case the current instance simply keeps running.
    /// </summary>
    public static bool RestartElevated(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    /// <summary>Asks whether to restart as administrator. <paramref name="dontAskAgain"/> is only meaningful for "No".</summary>
    public static bool Ask(Window owner, out bool dontAskAgain)
    {
        bool restart = false, never = false;

        var never_ = new CheckBox { Content = "Nicht mehr fragen", Margin = new Thickness(0, 14, 0, 0) };
        var yes = new Button { Content = "Als Administrator neu starten", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var no = new Button { Content = "Ohne Administratorrechte fortfahren", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var dlg = new Window
        {
            Title = "DiskTree",
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
                    new TextBlock
                    {
                        Text = "Als Administrator neu starten?",
                        FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
                    },
                    new TextBlock
                    {
                        Text = "Ohne Administratorrechte kann DiskTree geschützte Ordner (z. B. „System Volume Information“ oder " +
                               "Ordner anderer Benutzer) nicht lesen. Die Größen auf einem Laufwerk sind dann etwas zu klein.\n\n" +
                               "Du kannst auch ohne Administratorrechte weiterarbeiten.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                    },
                    never_,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 18, 0, 0), Children = { yes, no },
                    },
                },
            },
        };

        yes.Click += (_, _) => { restart = true; dlg.Close(); };
        no.Click += (_, _) => { never = never_.IsChecked == true; dlg.Close(); };
        dlg.ShowDialog();

        dontAskAgain = never;
        return restart;
    }
}
