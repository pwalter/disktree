using System.IO;
using System.Text.Json;
using DiskTree.Core;
using DiskTree.Core.Storage;
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
    /// <summary>Duplicate search of a remote source: bytes one run may download before it asks (0 = no limit).</summary>
    public long DuplicateDownloadLimit { get; set; } = 2L << 30;
    /// <summary>Saved remote sources (S3, SFTP …) without their secrets: those are in the Windows Credential Manager (SourceSecrets).</summary>
    public List<SourceProfile> Sources { get; set; } = new();
    /// <summary>A scan of a source that charges per request (S3) pauses and asks after this many requests (0 = never).</summary>
    public long RequestWarnThreshold { get; set; } = 100_000;

    private static string FilePath => Portable.SettingsIn(Portable.DataDir);

    public static AppSettings Load() => LoadFrom(FilePath);

    /// <summary>
    /// Reads the settings file. Missing file: defaults. A file that cannot be parsed is kept as a backup
    /// (<c>settings.json.bak</c>, or with a time stamp if that exists already) before the defaults are returned,
    /// because the next Save would overwrite it.
    /// </summary>
    public static AppSettings LoadFrom(string file)
    {
        if (TryRead(file, out var settings, out var text)) return settings;
        if (!string.IsNullOrWhiteSpace(text)) BackUpDamaged(file, text);
        // Missing, empty or damaged: the version before the last save (see SaveTo) still has the sources and the rest.
        return TryRead(PreviousOf(file), out var previous, out _) ? previous : new AppSettings();
    }

    /// <summary>The copy of the settings before the last save ("settings.json.prev").</summary>
    public static string PreviousOf(string file) => file + ".prev";

    /// <summary>Reads and parses a settings file; false if it is missing, unreadable, empty or damaged (<paramref name="text"/> then holds what was read).</summary>
    private static bool TryRead(string file, out AppSettings settings, out string? text)
    {
        settings = new AppSettings();
        text = null;
        try { text = File.ReadAllText(file); }
        catch { return false; } // missing or unreadable

        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
            return true;
        }
        catch
        {
            return false;
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

    public void Save() => SaveTo(FilePath);

    /// <summary>
    /// Writes the settings so that a crash or power cut at any moment leaves a readable file: first a complete
    /// "settings.json.tmp" (flushed to the disk), then it replaces settings.json in one step; the version before goes to
    /// "settings.json.prev", which <see cref="LoadFrom"/> falls back to. Errors are swallowed (nothing to remember is harmless).
    /// </summary>
    public bool SaveTo(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true });
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            if (!File.Exists(file))
            {
                File.Move(tmp, file);
                return true;
            }
            // Keep the old version only if it is readable: a damaged file must not overwrite the last good copy.
            bool keepOld = TryRead(file, out _, out _);
            try
            {
                if (keepOld) File.Replace(tmp, file, PreviousOf(file), ignoreMetadataErrors: true);
                else File.Move(tmp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // File.Replace is not offered everywhere (some network shares, FAT sticks in portable mode): copy, then move.
                if (keepOld) File.Copy(file, PreviousOf(file), overwrite: true);
                File.Move(tmp, file, overwrite: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
