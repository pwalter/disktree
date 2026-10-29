using System.Runtime.InteropServices;
using System.Windows;

namespace DiskTree;

/// <summary>Command line: DiskTree.exe [Pfad] [--threads N] [--admin] [--no-admin-prompt] [--help]</summary>
public sealed class CliOptions
{
    public const string Usage =
        "DiskTree – Speicherplatz-Analyse\n" +
        "\n" +
        "Aufruf:\n" +
        "  DiskTree.exe [Pfad] [Optionen]\n" +
        "\n" +
        "  Pfad                  Laufwerk oder Ordner, der sofort nach dem Start gescannt wird\n" +
        "                        (z. B. C:\\ oder \"D:\\Daten\")\n" +
        "\n" +
        "Optionen:\n" +
        "  -t, --threads N       Anzahl der Scan-Threads (1-256, Standard: zuletzt benutzt, sonst 16)\n" +
        "      --admin           Direkt als Administrator neu starten (UAC-Abfrage), ohne Rückfrage\n" +
        "      --no-admin-prompt Die Frage nach Administratorrechten in diesem Start überspringen\n" +
        "  -h, --help, /?        Diese Hilfe\n" +
        "\n" +
        "Beispiele:\n" +
        "  DiskTree.exe C:\\\n" +
        "  DiskTree.exe \"D:\\Daten\" --threads 32 --admin\n";

    public string? Path { get; private set; }
    public int? Threads { get; private set; }
    public bool Admin { get; private set; }
    public bool NoAdminPrompt { get; private set; }
    public bool Help { get; private set; }
    public string? Error { get; private set; }

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var o = new CliOptions();
        var it = args.GetEnumerator();

        while (it.MoveNext())
        {
            var a = it.Current;
            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?" or "-?" or "/h":
                    o.Help = true;
                    break;
                case "--admin" or "/admin":
                    o.Admin = true;
                    break;
                case "--no-admin-prompt":
                    o.NoAdminPrompt = true;
                    break;
                case "-t" or "--threads" or "/t":
                    if (!it.MoveNext() || !int.TryParse(it.Current, out var n) || n is < 1 or > 256)
                        return o.Fail("--threads erwartet eine Zahl von 1 bis 256.");
                    o.Threads = n;
                    break;
                default:
                    if (a.StartsWith("--threads=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(a["--threads=".Length..], out var m) || m is < 1 or > 256)
                            return o.Fail("--threads erwartet eine Zahl von 1 bis 256.");
                        o.Threads = m;
                    }
                    else if (a.StartsWith('-') || (a.StartsWith('/') && a.Length <= 3 && !a.Contains('\\')))
                        return o.Fail($"Unbekannte Option: {a}");
                    else if (o.Path != null)
                        return o.Fail("Es kann nur ein Pfad angegeben werden.");
                    else
                        // "C:\" on a command line arrives as C:" (the backslash escapes the quote); clean that up.
                        o.Path = a.Trim().Trim('"');
                    break;
            }
        }
        return o;
    }

    /// <summary>The arguments to hand to the elevated instance (everything except --admin).</summary>
    public IEnumerable<string> ForwardArgs(string? pathOverride = null)
    {
        var path = pathOverride ?? Path;
        if (!string.IsNullOrEmpty(path)) yield return path;
        if (Threads is { } t) { yield return "--threads"; yield return t.ToString(); }
    }

    private CliOptions Fail(string message)
    {
        Error = message;
        return this;
    }

    /// <summary>Prints the help to the console the program was started from, or shows it in a window if there is none.</summary>
    public static void ShowHelp(string? error)
    {
        var text = (error != null ? $"Fehler: {error}\n\n" : "") + Usage;

        // A WinExe has no console of its own; borrow the one of the shell that started it.
        if (AttachConsole(ATTACH_PARENT_PROCESS))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(text);
        }
        else
        {
            MessageBox.Show(text, "DiskTree", MessageBoxButton.OK, error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
