using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskTree.Core.Storage;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>An entry of the path box list: a drive, or a saved source (<see cref="ProfileId"/>).</summary>
public sealed record PathChoice(string Path, string Display, string? ProfileId = null)
{
    // The editable box shows ToString() as its text when an entry is picked.
    public override string ToString() => Path;
}

/// <summary>
/// Saved sources ("Quellen"): the toolbar button that opens their dialog, the entries in the path box list (drives
/// first, then the sources) and the profile the user picked last (<see cref="SourceProfiles.PreferredId"/>).
/// </summary>
public sealed class SourcesController
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ComboBox _pathBox;
    private readonly Action _save;
    private readonly Func<string, Task> _scan;

    public SourcesController(Window window, AppSettings settings, ComboBox pathBox, Button sourcesButton, Action save,
        Func<string, Task> scan)
    {
        _window = window;
        _settings = settings;
        _pathBox = pathBox;
        _save = save;
        _scan = scan;

        SourceProfiles.Set(settings.Sources);
        FillPathBox();
        sourcesButton.Click += (_, _) => OpenDialog();
        pathBox.SelectionChanged += (_, _) =>
        {
            if (pathBox.SelectedItem is PathChoice c)
                SourceProfiles.PreferredId = c.ProfileId; // a drive: no preference
        };
    }

    /// <summary>Drives (ready ones), then the saved sources.</summary>
    public void FillPathBox()
    {
        var text = _pathBox.Text;
        _pathBox.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady) _pathBox.Items.Add(new PathChoice(d.Name, d.Name)); } catch { }
        }
        foreach (var p in _settings.Sources)
        {
            var kind = SourceKinds.Get(p.Kind);
            if (kind == null) continue;
            var path = kind.PathOf(p);
            _pathBox.Items.Add(new PathChoice(path, $"☁  {p.Name}  –  {path}", p.Id));
        }
        _pathBox.Text = text; // refilling must not change what the user typed
    }

    /// <summary>The first entry (a drive), for the box of a fresh start.</summary>
    public string FirstPath => _pathBox.Items.Count > 0 && _pathBox.Items[0] is PathChoice c ? c.Path : "";

    /// <summary>
    /// "Quelle hinzufügen", filled in from a path the user typed ("sftp://user@host/ordner"). True once the source
    /// was saved; the path box then holds a path of the new source.
    /// </summary>
    public bool CreateFor(ISourceKind kind, string path)
    {
        var dlg = new SourceDialog(null, kind.ProfileFor(path)) { Owner = _window };
        if (dlg.ShowDialog() != true || dlg.Result is not { } p) return false;
        _settings.Sources.Add(p);
        SourceProfiles.Set(_settings.Sources);
        _save();
        FillPathBox();
        SourceProfiles.PreferredId = p.Id;
        // The user may have changed server or user in the dialog: then scan the new source's own path.
        if (!StorageProviders.TryResolve(_pathBox.Text, out var loc) || loc.Provider.Profile?.Id != p.Id)
            _pathBox.Text = kind.PathOf(p);
        return true;
    }

    /// <summary>
    /// The user trusted the host key of a saved source while connecting: it is stored with the source (not secret), so
    /// the next connection does not ask again and a different key is refused.
    /// </summary>
    public void RememberHostKey(string profileId, string fingerprint)
    {
        var p = _settings.Sources.FirstOrDefault(x => x.Id == profileId);
        if (p == null || p.HostKeySha256 == fingerprint) return;
        p.HostKeySha256 = fingerprint;
        SourceProfiles.Set(_settings.Sources);
        _save();
    }

    private void OpenDialog()
    {
        var dlg = new SourcesDialog(_settings, _save) { Owner = _window };
        bool scan = dlg.ShowDialog() == true;
        FillPathBox();
        if (!scan || dlg.ScanPath is not { } path) return;
        SourceProfiles.PreferredId = dlg.ScanProfile?.Id;
        _scan(path).Forget("ScanPathAsync");
    }
}
