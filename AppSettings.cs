using System.IO;
using System.Text.Json;

namespace DiskTree;

/// <summary>Small bits of state that are remembered between runs (%AppData%\DiskTree\settings.json).</summary>
public sealed class AppSettings
{
    public string? LastPath { get; set; }
    public int Threads { get; set; } = 16;
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    /// <summary>Files at least this many months old count as "old" (age column / chart); one of AgeBuckets.LimitMonths.</summary>
    public int AgeThresholdMonths { get; set; } = 24;
    /// <summary>Look for a newer release at start (see Updater).</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;
    /// <summary>Folder patterns the scan skips (see <see cref="ExcludeRules"/>).</summary>
    public List<string> Excludes { get; set; } = new();
    /// <summary>Duplicate search ignores files smaller than this.</summary>
    public long DuplicateMinBytes { get; set; } = 1 << 20;

    private static string FilePath => Portable.SettingsIn(Portable.DataDir);

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember the settings is harmless.
        }
    }
}
