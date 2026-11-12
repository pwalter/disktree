using System.Diagnostics;

namespace DiskTree.Infrastructure;

public static class ShellOpen
{
    /// <summary>Opens the folder that contains the item in the Windows Explorer and selects the item there.</summary>
    public static void InExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
}
