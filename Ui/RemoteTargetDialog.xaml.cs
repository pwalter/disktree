using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.FileOperations;

namespace DiskTree.Ui;

/// <summary>A folder of the scan in the destination tree; its subfolders are created when it is opened.</summary>
public sealed class TargetFolder : INotifyPropertyChanged
{
    private static readonly TargetFolder Placeholder = new("…", "", null, null);
    private readonly Node? _node;
    private readonly PathOps? _paths;
    private bool _expanded;
    private bool _loaded;

    public TargetFolder(string name, string path, Node? node, PathOps? paths)
    {
        Name = name;
        Path = path;
        _node = node;
        _paths = paths;
        if (node != null && node.Snapshot().Length > 0) Children.Add(Placeholder);
    }

    public string Name { get; }
    public string Path { get; }
    public ObservableCollection<TargetFolder> Children { get; } = new();

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            if (value) Load();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    private void Load()
    {
        if (_loaded || _node == null || _paths == null) return;
        _loaded = true;
        Children.Clear();
        foreach (var c in _node.Snapshot().OrderBy(c => c.Name, _paths.Comparer))
            Children.Add(new TargetFolder(c.Name, c.FullPath, c, _paths));
    }

    /// <summary>The subfolder on the way to <paramref name="path"/> (opening this one), or null.</summary>
    public TargetFolder? StepTowards(string path)
    {
        if (_paths == null) return null;
        IsExpanded = true;
        return Children.FirstOrDefault(c => c._node != null && _paths.IsSameOrUnder(c.Path, path));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Destination of "Verschieben" on a source like SFTP: the folders of the current scan as a tree, or a typed path of
/// the same server. The destination is checked on the server (a missing one can be created) before the dialog closes.
/// </summary>
public partial class RemoteTargetDialog : Window
{
    private sealed record PolicyOption(string Text, ConflictPolicy Policy)
    {
        public override string ToString() => Text;
    }

    private readonly IStorageProvider _provider;
    private readonly IStorageWriter _writer;
    private readonly string _top;
    private bool _checking;

    public TargetChoice? Result { get; private set; }

    public RemoteTargetDialog(IReadOnlyList<OpItem> items, Scanner scanner, IStorageWriter writer, string initial)
    {
        InitializeComponent();
        _provider = scanner.Provider;
        _writer = writer;
        var paths = _provider.Paths;
        var root = scanner.Root!;
        _top = RemoteFileOperations.TopOf(paths, scanner.IsMulti ? root.Snapshot().FirstOrDefault()?.FullPath ?? "" : root.FullPath);

        long size = items.Sum(i => i.Size);
        int dirs = items.Count(i => i.IsDir);
        string what = items.Count == 1 ? (items[0].IsDir ? "1 Ordner" : "1 Datei")
            : dirs == 0 ? $"{items.Count:N0} Dateien" : $"{items.Count:N0} Elemente ({dirs:N0} Ordner)";
        Summary.Text = $"{what} · {SizeFormat.Format(size)} · {_provider.DisplayName}";
        Names.Text = string.Join("\n", items.Take(40).Select(i => i.Path)) + (items.Count > 40 ? $"\n… und {items.Count - 40:N0} weitere" : "");

        var tops = scanner.IsMulti ? root.Snapshot() : [root];
        foreach (var n in tops) Folders.Items.Add(new TargetFolder(n.FullPath, n.FullPath, n, paths));

        foreach (var o in new[]
                 {
                     new PolicyOption("Nachfragen", ConflictPolicy.Ask),
                     new PolicyOption("Überspringen", ConflictPolicy.Skip),
                     new PolicyOption("Ersetzen (vorhandenes Ziel wird endgültig gelöscht)", ConflictPolicy.Replace),
                     new PolicyOption("Beide behalten (neuen Namen vergeben)", ConflictPolicy.Rename),
                 })
            PolicyBox.Items.Add(o);
        PolicyBox.SelectedIndex = 0;
        Hint.Text = writer is ICopyingWriter
            ? "S3 kennt kein Verschieben: Jedes Objekt wird auf dem Server kopiert, geprüft und danach das Original gelöscht – nur " +
              "innerhalb dieses Buckets. Objekt-ACLs werden nicht übernommen. Einen fehlenden Zielordner legt DiskTree nach Rückfrage " +
              "an (Ordner-Marker). „Ersetzen“ löscht das vorhandene Ziel (ohne Versionierung endgültig)."
            : "Verschoben wird auf dem Server (kein Kopieren). Einen Zielordner, den es noch nicht gibt, legt DiskTree nach " +
              "Rückfrage an. „Ersetzen“ löscht das vorhandene Ziel endgültig – SFTP hat keinen Papierkorb.";

        TargetBox.Text = initial;
        Loaded += (_, _) =>
        {
            ExpandTowards(initial.Length > 0 ? initial : items[0].Parent ?? "");
            TargetBox.Focus();
            TargetBox.CaretIndex = TargetBox.Text.Length;
        };
    }

    /// <summary>Opens the tree down to a folder (as far as the scan has it).</summary>
    private void ExpandTowards(string path)
    {
        var cur = Folders.Items.OfType<TargetFolder>().FirstOrDefault(f => _provider.Paths.IsSameOrUnder(f.Path, path));
        while (cur != null && !_provider.Paths.NameEquals(cur.Path, path)) cur = cur.StepTowards(path);
    }

    private void Folders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TargetFolder f) TargetBox.Text = f.Path;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        var target = RemoteFileOperations.ResolveTarget(TargetBox.Text, _provider, _top, out var error);
        if (target == null)
        {
            Status.Text = error;
            Dialogs.Info(this, error!);
            return;
        }

        var policy = (PolicyBox.SelectedItem as PolicyOption)?.Policy ?? ConflictPolicy.Ask;
        _checking = true;
        OkButton.IsEnabled = false;
        Status.Text = "Prüfe den Zielordner auf dem Server …";
        try
        {
            var info = await Task.Run(() => RemoteFileOperations.StatFolder(_writer, target, CancellationToken.None));
            if (info is { IsDirectory: true } && !info.Value.IsLink)
            {
                Finish(target, policy);
                return;
            }
            if (info != null)
            {
                Status.Text = "Das Ziel ist kein Ordner.";
                Dialogs.Info(this, $"Das Ziel ist kein Ordner:\n{target}");
                return;
            }
            if (!Dialogs.Confirm(this, $"Den Zielordner gibt es nicht:\n{target}\n\nJetzt auf dem Server anlegen?"))
            {
                Status.Text = "";
                return;
            }
            Status.Text = "Lege den Zielordner an …";
            await Task.Run(() => RemoteFileOperations.CreateFolders(_writer, _provider.Paths, target, CancellationToken.None));
            Finish(target, policy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status.Text = "Ziel nicht verfügbar.";
            Dialogs.Warn(this, "Der Zielordner ist nicht verfügbar:\n" + ex.Message);
        }
        finally
        {
            _checking = false;
            OkButton.IsEnabled = true;
        }
    }

    private void Finish(string target, ConflictPolicy policy)
    {
        Result = new TargetChoice(target, policy, false);
        DialogResult = true;
    }
}
