using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTree.Core.Storage;
using DiskTree.Infrastructure;
using Microsoft.Win32;

namespace DiskTree.Ui;

/// <summary>
/// "Quelle hinzufügen / bearbeiten": the fields of one saved connection, shown as its kind asks for them
/// (<see cref="ISourceKind.Label"/>), "Verbindung testen", and on "Speichern" the secrets into the Windows Credential
/// Manager (<see cref="SourceSecrets"/>). The profile itself (no secrets) is handed back in <see cref="Result"/>.
/// </summary>
public partial class SourceDialog : Window
{
    private readonly SourceProfile _profile;
    private readonly bool _isNew;
    private readonly Dictionary<SourceField, (FrameworkElement Row, TextBlock? Label)> _rows;
    private CancellationTokenSource? _testCts;
    private bool _filling;

    /// <summary>The saved profile after "Speichern" (secrets are already in the Credential Manager).</summary>
    public SourceProfile? Result { get; private set; }

    public SourceDialog(SourceProfile? existing)
    {
        InitializeComponent();
        _isNew = existing == null;
        _profile = existing?.Clone() ?? (SourceKinds.All.FirstOrDefault()?.NewProfile() ?? new SourceProfile());

        _rows = new()
        {
            [SourceField.Endpoint] = (RowEndpoint, LabelEndpoint),
            [SourceField.Port] = (RowPort, LabelPort),
            [SourceField.Region] = (RowRegion, LabelRegion),
            [SourceField.PathStyle] = (RowPathStyle, null),
            [SourceField.AwsProfile] = (RowAwsProfile, LabelAwsProfile),
            [SourceField.UserName] = (RowUserName, LabelUserName),
            [SourceField.Secret] = (RowSecret, LabelSecret),
            [SourceField.KeyFile] = (RowKeyFile, LabelKeyFile),
            [SourceField.Passphrase] = (RowPassphrase, LabelPassphrase),
            [SourceField.Path] = (RowPath, LabelPath),
            [SourceField.Listing] = (RowListing, LabelListing),
        };

        Title = _isNew ? "Quelle hinzufügen" : "Quelle bearbeiten";
        Heading.Text = _isNew ? "Neue Quelle" : $"Quelle „{_profile.Name}“";

        _filling = true;
        foreach (var k in SourceKinds.All) KindBox.Items.Add(new ComboBoxItem { Content = k.DisplayName, Tag = k });
        KindBox.SelectedIndex = Math.Max(0, SourceKinds.All.ToList().FindIndex(k => k.Id == _profile.Kind));
        KindBox.IsEnabled = _isNew; // another type would need other fields and secrets: a new source instead
        _filling = false;

        NameBox.Text = _profile.Name;
        EndpointBox.Text = _profile.Endpoint;
        PortBox.Text = _profile.Port > 0 ? _profile.Port.ToString() : "";
        RegionBox.Text = _profile.Region;
        PathStyleBox.IsChecked = _profile.PathStyle;
        AwsProfileBox.Text = _profile.AwsProfile;
        UserNameBox.Text = _profile.UserName;
        KeyFileBox.Text = _profile.KeyFile;
        PathBox.Text = _profile.Path;
        ListingBox.SelectedIndex = (int)_profile.Listing;

        FillAuth();
        UpdateHint();
        Closed += (_, _) => _testCts?.Cancel();
        Loaded += (_, _) => NameBox.Focus();
    }

    private ISourceKind? Kind => (KindBox.SelectedItem as ComboBoxItem)?.Tag as ISourceKind;
    private SourceAuth Auth => (AuthBox.SelectedItem as ComboBoxItem)?.Tag is SourceAuth a ? a : SourceAuth.Default;

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Kind is not { } kind) return;
        // A new source of another type: take that type's defaults for what is still untouched.
        var fresh = kind.NewProfile();
        _profile.Kind = kind.Id;
        _profile.Auth = fresh.Auth;
        FillAuth();
    }

    private void FillAuth()
    {
        var kind = Kind;
        _filling = true;
        AuthBox.Items.Clear();
        if (kind != null)
            foreach (var a in kind.AuthModes) AuthBox.Items.Add(new ComboBoxItem { Content = kind.AuthName(a), Tag = a });
        int at = kind?.AuthModes.ToList().IndexOf(_profile.Auth) ?? -1;
        AuthBox.SelectedIndex = Math.Max(0, at);
        _filling = false;
        UpdateFields();
    }

    private void Auth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        UpdateFields();
    }

    /// <summary>Shows the fields the kind uses with this sign-in mode, with its labels and hints.</summary>
    private void UpdateFields()
    {
        var kind = Kind;
        var auth = Auth;
        foreach (var (field, (row, label)) in _rows)
        {
            var text = kind?.Label(field, auth);
            row.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text == null) continue;
            var hint = kind!.Hint(field);
            if (label != null)
            {
                label.Text = text + StoredMark(field);
                label.ToolTip = hint;
            }
            else if (field == SourceField.PathStyle)
            {
                PathStyleBox.Content = text;
                PathStyleBox.ToolTip = hint;
            }
            if (row is DockPanel dp && dp.Children.Count > 1) ((FrameworkElement)dp.Children[^1]).ToolTip = hint;
        }
    }

    /// <summary>" (gespeichert)" behind a secret field that already has a stored value (left empty, it stays).</summary>
    private string StoredMark(SourceField field)
    {
        if (_isNew) return "";
        var slot = field switch { SourceField.Secret => SecretSlot.Secret, SourceField.Passphrase => SecretSlot.Passphrase, _ => (SecretSlot?)null };
        if (slot == null) return "";
        try { return SourceSecrets.Has(_profile, slot.Value) ? " (gespeichert)" : ""; }
        catch { return ""; }
    }

    private void UpdateHint()
    {
        var text = "Secret-Keys, Passwörter und Passphrasen speichert DiskTree nur im Windows-Anmeldeinformationsspeicher " +
                   "dieses Benutzers (Eintrag „DiskTree:…“), nie in settings.json, Scandateien, Exporten oder Protokollen. " +
                   "Ein leeres Feld behält ein gespeichertes Geheimnis.";
        if (Portable.IsPortable)
            text += "\n\nPortable-Modus: Die Verbindung selbst liegt in der settings.json neben der exe und wandert mit; " +
                    "das Geheimnis bleibt im Anmeldeinformationsspeicher dieses Rechners und muss auf einem anderen Rechner neu eingegeben werden.";
        SecretHint.Text = text;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Privater Schlüssel", Filter = "Alle Dateien (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) KeyFileBox.Text = dlg.FileName;
    }

    /// <summary>The profile as the fields say (fields the kind does not show are cleared).</summary>
    private SourceProfile Collect()
    {
        var kind = Kind;
        var auth = Auth;
        bool Shown(SourceField f) => kind?.Label(f, auth) != null;
        var p = _profile.Clone();
        p.Kind = kind?.Id ?? p.Kind;
        p.Name = NameBox.Text.Trim();
        p.Auth = auth;
        p.Endpoint = Shown(SourceField.Endpoint) ? EndpointBox.Text.Trim() : "";
        p.Port = Shown(SourceField.Port) && int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0;
        p.Region = Shown(SourceField.Region) ? RegionBox.Text.Trim() : "";
        p.PathStyle = Shown(SourceField.PathStyle) && PathStyleBox.IsChecked == true;
        p.AwsProfile = Shown(SourceField.AwsProfile) ? AwsProfileBox.Text.Trim() : "";
        p.UserName = Shown(SourceField.UserName) ? UserNameBox.Text.Trim() : "";
        p.KeyFile = Shown(SourceField.KeyFile) ? KeyFileBox.Text.Trim().Trim('"') : "";
        p.Path = Shown(SourceField.Path) ? PathBox.Text.Trim().Trim('"') : "";
        p.Listing = Shown(SourceField.Listing) && ListingBox.SelectedIndex == 1 ? SourceListing.Flat : SourceListing.PerFolder;
        return p;
    }

    private static string? Typed(PasswordBox box, bool shown) => shown && box.Password.Length > 0 ? box.Password : null;

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Kind is not { } kind) return;
        if (_testCts != null) { _testCts.Cancel(); return; }

        var p = Collect();
        var auth = p.Auth;
        if (string.IsNullOrEmpty(p.Name)) p.Name = "Test";
        if (kind.Validate(p) is { } invalid) { ShowResult(invalid, ok: false); return; }

        using var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        TestButton.Content = "Abbrechen";
        ShowResult("Verbinde …", ok: null);
        try
        {
            var secret = Typed(SecretBox, kind.Label(SourceField.Secret, auth) != null);
            var passphrase = Typed(PassphraseBox, kind.Label(SourceField.Passphrase, auth) != null);
            var text = await kind.TestAsync(p, secret, passphrase, cts.Token);
            ShowResult(text, ok: true);
        }
        catch (OperationCanceledException)
        {
            ShowResult(cts.IsCancellationRequested && !IsLoaded ? "" : "Test abgebrochen (oder keine Antwort innerhalb von 45 s).", ok: false);
        }
        catch (Exception ex)
        {
            // The kinds turn their errors into sentences without secrets; nothing of this is logged.
            ShowResult(ex.Message, ok: false);
        }
        finally
        {
            _testCts = null;
            TestButton.Content = "Verbindung testen";
        }
    }

    private void ShowResult(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.Foreground = ok switch
        {
            true => new SolidColorBrush(Theme.IsDark ? Color.FromRgb(0x6F, 0xCF, 0x8E) : Color.FromRgb(0x1E, 0x7E, 0x3E)),
            false => new SolidColorBrush(Theme.IsDark ? Color.FromRgb(0xFF, 0x8A, 0x80) : Color.FromRgb(0xB3, 0x26, 0x1E)),
            null => (Brush)FindResource("Muted"),
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Kind is not { } kind) return;
        var p = Collect();
        if (p.Name.Length == 0)
        {
            Dialogs.Info(this, "Bitte einen Namen für die Quelle angeben.");
            NameBox.Focus();
            return;
        }
        if (kind.Validate(p) is { } invalid)
        {
            Dialogs.Info(this, invalid);
            return;
        }

        try
        {
            SaveSecret(p, SecretSlot.Secret, SecretBox, kind.Label(SourceField.Secret, p.Auth) != null);
            SaveSecret(p, SecretSlot.Passphrase, PassphraseBox, kind.Label(SourceField.Passphrase, p.Auth) != null);
        }
        catch (Exception ex)
        {
            Dialogs.Warn(this, "Die Zugangsdaten konnten nicht im Windows-Anmeldeinformationsspeicher abgelegt werden:\n\n" + ex.Message);
            return;
        }
        Result = p;
        DialogResult = true;
    }

    /// <summary>A typed secret replaces the stored one; a secret the sign-in mode no longer uses is removed.</summary>
    private static void SaveSecret(SourceProfile p, SecretSlot slot, PasswordBox box, bool shown)
    {
        if (!shown) SourceSecrets.Delete(p, slot);
        else if (box.Password.Length > 0) SourceSecrets.Write(p, slot, box.Password);
        box.Clear();
    }
}
