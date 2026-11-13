using System.IO;
using System.Text;

namespace DiskTree.Infrastructure;

/// <summary>Writes unexpected errors to crash.log next to settings.json (next to the exe in portable mode, else %AppData%\DiskTree).</summary>
public static class CrashLog
{
    public const string FileName = "crash.log";
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    /// <summary>Tests redirect the log here so that they do not touch the real data folder.</summary>
    public static string? FileOverride { get; set; }

    public static string FilePath => FileOverride ?? Path.Combine(Portable.DataDir, FileName);

    /// <summary>Appends the error; never throws (a log that cannot be written is ignored). Returns whether it was written.</summary>
    public static bool Write(string source, Exception ex) => WriteTo(FilePath, source, ex);

    public static bool WriteTo(string file, string source, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                // Keep the log small: when it grows too big the old content is dropped.
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes) File.Delete(file);
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").AppendLine(source);
                sb.AppendLine(ex.ToString());
                sb.AppendLine();
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
