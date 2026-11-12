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
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;
    /// <summary>Folder patterns the scan skips (see <see cref="ExcludeRules"/>).</summary>
    public List<string> Excludes { get; set; } = new();

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskTree", "settings.json");

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
