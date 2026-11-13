using System.IO;
using System.Text.Json;
using DiskTree.Core;
using DiskTree.Ui;

namespace DiskTree.Infrastructure;

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
    /// <summary>Timestamp the next scan builds the age buckets from (see AgeStamps).</summary>
    public AgeStamp AgeStamp { get; set; } = AgeStamp.Modified;
    /// <summary>Look for a newer release at start (see Updater).</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;
    /// <summary>Folder patterns the scan skips (see <see cref="ExcludeRules"/>).</summary>
    public List<string> Excludes { get; set; } = new();
    /// <summary>Duplicate search ignores files smaller than this.</summary>
    public long DuplicateMinBytes { get; set; } = 1 << 20;

    private static string FilePath => Portable.SettingsIn(Portable.DataDir);

    public static AppSettings Load() => LoadFrom(FilePath);

    /// <summary>
    /// Reads the settings file. Missing file: defaults. A file that cannot be parsed is kept as a backup
    /// (<c>settings.json.bak</c>, or with a time stamp if that exists already) before the defaults are returned,
    /// because the next Save would overwrite it.
    /// </summary>
    public static AppSettings LoadFrom(string file)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch { return new AppSettings(); } // missing or unreadable: nothing to rescue

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(text)) BackUpDamaged(file, text);
            return new AppSettings();
        }
    }

    private static void BackUpDamaged(string file, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(file)!;
            var name = Path.GetFileName(file);
            // Loaded twice in one start (before and after the window): do not keep identical copies.
            foreach (var old in Directory.EnumerateFiles(dir, name + "*.bak"))
                if (File.ReadAllText(old) == text) return;

            var target = file + ".bak";
            if (File.Exists(target)) target = $"{file}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.WriteAllText(target, text);
        }
        catch
        {
            // A backup that cannot be written must not stop the program from starting.
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
