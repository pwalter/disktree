using System.Windows;
using System.Windows.Input;
using DiskTree.Core.Storage;
using DiskTree.Infrastructure;

namespace DiskTree.Ui;

/// <summary>
/// "Quellen": the saved connections (add, edit, delete, scan) and the request threshold of paid sources. Every change
/// is saved right away (<see cref="Save"/>): the secrets of a profile go to the Credential Manager as soon as it is
/// saved, so the list and the stored secrets never disagree.
/// </summary>
public partial class SourcesDialog : Window
{
    private readonly AppSettings _settings;
    private readonly Action _save;

    private sealed record Row(SourceProfile Profile, string Title, string Detail);

    /// <summary>The path to scan after "Scannen" (null = just closed).</summary>
    public string? ScanPath { get; private set; }

    /// <summary>The profile "Scannen" was used on.</summary>
    public SourceProfile? ScanProfile { get; private set; }

    public SourcesDialog(AppSettings settings, Action save)
    {
        InitializeComponent();
        _settings = settings;
        _save = save;
        ThresholdBox.Text = settings.RequestWarnThreshold.ToString("N0");
        StoreHint.Text = Portable.IsPortable
            ? "Portable-Modus: Die Verbindungen stehen in der settings.json neben der exe, die Geheimnisse im Windows-Anmeldeinformationsspeicher dieses Rechners."
            : "Geheimnisse liegen im Windows-Anmeldeinformationsspeicher, nicht in settings.json.";
        Fill();
        Closing += (_, _) => TakeThreshold();
    }

    private void Fill(SourceProfile? select = null)
    {
        List.Items.Clear();
        foreach (var p in _settings.Sources)
        {
            var kind = SourceKinds.Get(p.Kind);
            string where = kind?.PathOf(p) ?? p.Path;
            string server = string.IsNullOrEmpty(p.Endpoint) ? "" : " · " + p.Endpoint;
            List.Items.Add(new Row(p, p.Name, $"{kind?.DisplayName ?? p.Kind}{server} · {where} · {(kind?.AuthName(p.Auth) ?? "")}"));
        }
        EmptyHint.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (select != null)
            List.SelectedItem = List.Items.Cast<Row>().FirstOrDefault(r => r.Profile.Id == select.Id);
        else if (List.Items.Count > 0)
            List.SelectedIndex = 0;
        UpdateButtons();
    }

    private Row? Selected => List.SelectedItem as Row;

    private void UpdateButtons()
    {
        bool any = Selected != null;
        EditButton.IsEnabled = DeleteButton.IsEnabled = ScanButton.IsEnabled = any;
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SourceDialog(null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } p) return;
        _settings.Sources.Add(p);
        Save();
        Fill(p);
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void EditSelected()
    {
        if (Selected is not { } row) return;
        var dlg = new SourceDialog(row.Profile) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } p) return;
        int i = _settings.Sources.FindIndex(x => x.Id == p.Id);
        if (i >= 0) _settings.Sources[i] = p; else _settings.Sources.Add(p);
        SourceKinds.Get(p.Kind)?.Forget(p.Id);
        Save();
        Fill(p);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) DeleteSelected();
        else if (e.Key == Key.Enter) EditSelected();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (Selected is not { } row) return;
        if (!Dialogs.Confirm(this, $"Quelle „{row.Profile.Name}“ löschen?\n\nIhre Zugangsdaten im Windows-Anmeldeinformationsspeicher werden ebenfalls entfernt."))
            return;
        try
        {
            SourceSecrets.DeleteAll(row.Profile);
        }
        catch (Exception ex)
        {
            Dialogs.Warn(this, "Die Zugangsdaten konnten nicht aus dem Windows-Anmeldeinformationsspeicher entfernt werden:\n\n" + ex.Message);
            return;
        }
        _settings.Sources.RemoveAll(p => p.Id == row.Profile.Id);
        SourceKinds.Get(row.Profile.Kind)?.Forget(row.Profile.Id);
        if (SourceProfiles.PreferredId == row.Profile.Id) SourceProfiles.PreferredId = null;
        Save();
        Fill();
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var path = SourceKinds.Get(row.Profile.Kind)?.PathOf(row.Profile) ?? row.Profile.Path;
        ScanProfile = row.Profile;
        ScanPath = path;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TakeThreshold()
    {
        var text = ThresholdBox.Text.Replace(".", "").Replace(",", "").Replace(" ", "").Replace(" ", "").Trim();
        if (long.TryParse(text, out var n) && n >= 0 && n != _settings.RequestWarnThreshold)
        {
            _settings.RequestWarnThreshold = n;
            Save();
        }
    }

    private void Save()
    {
        SourceProfiles.Set(_settings.Sources);
        _save();
    }
}
