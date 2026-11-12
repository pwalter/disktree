using System.IO;

namespace DiskTree.Infrastructure;

/// <summary>
/// Portable mode: a file DiskTree.portable next to the exe (or --portable on the command line) keeps settings.json
/// next to the exe instead of in %AppData%\DiskTree.
/// </summary>
public static class Portable
{
    public const string MarkerName = "DiskTree.portable";

    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string MarkerPath => Path.Combine(ExeDir, MarkerName);
    public static bool HasMarker => File.Exists(MarkerPath);

    /// <summary>Set from the command line (--portable).</summary>
    public static bool ForcedByFlag { get; set; }

    public static bool IsPortable => ForcedByFlag || HasMarker;

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskTree");

    /// <summary>Folder for settings.json and the other small state files.</summary>
    public static string DataDir => IsPortable ? ExeDir : AppDataDir;

    public static string SettingsIn(string dir) => Path.Combine(dir, "settings.json");

    /// <summary>
    /// Switches the marker file. When enabling, the current settings are written next to the exe; when disabling, they
    /// are written back to %AppData% and the portable copy is removed. Throws if the folder is not writable.
    /// </summary>
    public static void SetEnabled(bool enable, AppSettings current)
    {
        if (enable)
        {
            File.WriteAllText(MarkerPath, "");
            current.Save();                       // now lands next to the exe
        }
        else
        {
            ForcedByFlag = false;
            if (HasMarker) File.Delete(MarkerPath);
            current.Save();                       // now lands in %AppData%
            try { File.Delete(SettingsIn(ExeDir)); } catch { /* leftover copy is harmless */ }
        }
    }
}
