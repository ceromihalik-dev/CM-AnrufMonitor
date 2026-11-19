using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CM.AnrufMonitor.Core;

namespace CM.AnrufMonitor.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly DispatcherTimer _routerSyncTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _routerSyncDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<Contact> _routerContacts = new();
    private readonly Dictionary<string, string> _routerContactLookup = new(StringComparer.Ordinal);
    private CredentialVault _credentialVault = null!;
    private CancellationTokenSource _syncCancellation = new();
    private int _syncGeneration;
    private bool _pendingPhonebookRefresh;
    private bool _pendingManualSync;
    private bool _routerPhonebookLoaded;
    private bool _routerCacheWritable = true;
    private string _verifiedRouterIdentity = "";

    private void InitializeFritzSync()
    {
        _credentialVault = new CredentialVault(_dataDirectory);
        FritzSyncEnabledBox.IsChecked = _settings.FritzSyncEnabled;
        FritzUsernameBox.Text = _settings.FritzUsername;
        FritzPortBox.Text = _settings.FritzPort.ToString(CultureInfo.InvariantCulture);
        SyncPhonebooksBox.IsChecked = _settings.SyncPhonebooks;
        UpdateCredentialStatus();
        UpdateLastSyncLabel();
        FritzSyncStatus.Text = _settings.FritzSyncEnabled ? "Abgleich wird nach dem Start vorbereitet." : "Automatischer Abgleich ist ausgeschaltet.";
        _routerSyncTimer.Tick += (_, _) => RequestRouterSync();
        _routerSyncDebounce.Tick += async (_, _) => { _routerSyncDebounce.Stop(); await RunRouterSyncAsync(); };
    }

    private void StartRouterSync()
    {
        if (_smokeMode) return;
        _routerSyncTimer.Start();
        RequestRouterSync(forcePhonebooks: true);
    }

    private void StopRouterSync()
    {
        _routerSyncTimer.Stop();
        _routerSyncDebounce.Stop();
        _pendingPhonebookRefresh = false;
        _pendingManualSync = false;
        CancelRouterOperation();
    }

    private void CancelRouterOperation()
    {
        _routerSyncDebounce.Stop();
        _pendingPhonebookRefresh = false;
        _pendingManualSync = false;
        ++_syncGeneration;
        _syncCancellation.Cancel();
        _syncCancellation.Dispose();
        _syncCancellation = new CancellationTokenSource();
    }

    private void ChangeRouterScope()
    {
        CancelRouterOperation();
        _verifiedRouterIdentity = "";
        _routerPhonebookLoaded = false;
        _routerCacheWritable = true;
        _routerContacts.Clear();
        _routerContactLookup.Clear();
        _tracker.SetRouterIdentity("");
        _settings.LastSuccessfulSync = null;
        _settings.LastPhonebookSync = null;
        if (_ready) { RefreshContacts(); UpdateLastSyncLabel(); }
    }

    private void RequestRouterSync(bool forcePhonebooks = false, int delayMilliseconds = 200, bool manual = false)
    {
        if (_smokeMode || _savedOnExit || (!_settings.FritzSyncEnabled && !manual)) return;
        _pendingPhonebookRefresh |= forcePhonebooks;
        _pendingManualSync |= manual;
        _routerSyncDebounce.Stop();
        _routerSyncDebounce.Interval = TimeSpan.FromMilliseconds(Math.Max(50, delayMilliseconds));
        _routerSyncDebounce.Start();
    }

    private FritzAccessOptions StoredFritzOptions()
    {
        if (string.IsNullOrWhiteSpace(_settings.FritzUsername))
            throw new InvalidOperationException("Bitte unter Einrichtung den FRITZ!Box-Benutzer und sein Kennwort über „Zugang speichern“ hinterlegen.");
        string? password = _credentialVault.Load(_settings.Host, _settings.FritzPort, _settings.FritzUsername);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Für den Listenabgleich fehlt das gespeicherte Kennwort. Bitte unter Einrichtung eingeben und „Zugang speichern“ wählen.");
        return new FritzAccessOptions { Host = _settings.Host, Port = _settings.FritzPort, Username = _settings.FritzUsername, Password = password, CountryCode = _settings.CountryCode, AreaCode = _settings.AreaCode };
    }

    private bool TryReadFritzControls(out string host, out int port, out string username)
    {
        host = HostBox.Text.Trim();
        username = FritzUsernameBox.Text.Trim();
        port = 0;
        if (host.Length is < 1 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        { SetSyncStatus("Bitte oben eine gültige FRITZ!Box-Adresse ohne http:// oder Port eingeben.", error: true); return false; }
        if (!int.TryParse(FritzPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        { SetSyncStatus("Bitte einen gültigen TR-064-Port eingeben. Standard: 49000.", error: true); return false; }
        if (username.Length is < 1 or > 256 || username.Any(char.IsControl))
        { SetSyncStatus("Bitte den Namen eines FRITZ!Box-Benutzers mit Telefonie-/Anruflistenrechten eingeben.", error: true); return false; }
        return true;
    }

    private async void TestFritz_Click(object sender, RoutedEventArgs e)
    {
        if (_smokeMode || !TryReadFritzControls(out string host, out int port, out string username)) return;
        if (!await _syncGate.WaitAsync(0)) { SetSyncStatus("Ein Abgleich läuft bereits. Bitte kurz warten."); return; }
        int generation = _syncGeneration;
        CancellationToken token = _syncCancellation.Token;
        SetSyncButtonsBusy(true);
        try
        {
            string password = FritzPasswordBox.Password;
            if (password.Length == 0) password = _credentialVault.Load(host, port, username) ?? "";
            if (password.Length == 0) throw new InvalidOperationException("Bitte ein Kennwort eintragen. Beim Test wird es nicht gespeichert.");
            SetSyncStatus("Prüft den Zugriff auf die FRITZ!Box-Anrufliste …");
            using var client = new FritzTr064Client();
            FritzCallListSnapshot snapshot = await client.GetCallListAsync(new FritzAccessOptions { Host = host, Port = port, Username = username, Password = password, CountryCode = _settings.CountryCode, AreaCode = _settings.AreaCode }, token);
            if (generation != _syncGeneration || _savedOnExit) return;
            SetSyncStatus("Verbindung erfolgreich. " + snapshot.Calls.Count + " Anruflisteneinträge lesbar. Zum Übernehmen „Zugang speichern“ und anschließend „Jetzt abgleichen“ wählen.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _syncGeneration && !_savedOnExit) SetSyncStatus(error.Message, error: true);
        }
        finally { _syncGate.Release(); if (!_savedOnExit) SetSyncButtonsBusy(false); }
    }

    private void SaveFritzAccess_Click(object sender, RoutedEventArgs e)
    {
        if (_smokeMode || !TryReadFritzControls(out string host, out int port, out string username)) return;
        if (_writeBlocked.Contains("settings")) { SetSyncStatus("Die Einstellungen sind wegen eines Lesefehlers gesperrt. Bitte den Datenordner prüfen und die App neu starten.", error: true); return; }
        try
        {
            string newPassword = FritzPasswordBox.Password;
            bool enabled = FritzSyncEnabledBox.IsChecked == true;
            if (newPassword.Length > 0) _credentialVault.Save(host, port, username, newPassword);
            else if (enabled && string.IsNullOrEmpty(_credentialVault.Load(host, port, username)))
                throw new InvalidOperationException("Zum Aktivieren bitte das FRITZ!Box-Kennwort eingeben.");
            bool scopeChanged = !host.Equals(_settings.Host, StringComparison.OrdinalIgnoreCase) || port != _settings.FritzPort || username != _settings.FritzUsername;
            if (scopeChanged) ChangeRouterScope(); else CancelRouterOperation();
            _settings.Host = host;
            _settings.FritzPort = port;
            _settings.FritzUsername = username;
            _settings.FritzSyncEnabled = enabled;
            _settings.SyncPhonebooks = SyncPhonebooksBox.IsChecked == true;
            if (!_settings.SyncPhonebooks)
            {
                _routerContacts.Clear();
                _routerContactLookup.Clear();
                _routerPhonebookLoaded = false;
            }
            FritzPasswordBox.Clear();
            _settingsDirty = true;
            bool saved = SavePending();
            UpdateCredentialStatus();
            RefreshContacts();
            SetSyncStatus(saved ? enabled ? "Zugang gespeichert. Abgleich wird gestartet …" : "Zugang gespeichert. Automatischer Abgleich bleibt ausgeschaltet." : "Die Zugangsdaten sind geschützt hinterlegt, aber die Einstellungen konnten noch nicht gespeichert werden.", error: !saved);
            _routerSyncTimer.Start();
            if (enabled) RequestRouterSync(forcePhonebooks: true);
        }
        catch (Exception error) { SetSyncStatus(error.Message, error: true); }
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e) => RequestRouterSync(forcePhonebooks: true, manual: true);

    private void ForgetFritzPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_smokeMode) return;
        try
        {
            CancelRouterOperation();
            _credentialVault.Delete();
            FritzPasswordBox.Clear();
            _settings.FritzSyncEnabled = false;
            FritzSyncEnabledBox.IsChecked = false;
            _settingsDirty = true;
            SavePending();
            UpdateCredentialStatus();
            SetSyncStatus("Gespeichertes Kennwort gelöscht. Der automatische Listenabgleich ist ausgeschaltet. Der Live-Anrufmonitor läuft unabhängig weiter.");
        }
        catch (Exception error) { SetSyncStatus("Das gespeicherte Kennwort konnte nicht gelöscht werden: " + error.Message, error: true); }
    }

    private async Task RunRouterSyncAsync()
    {
        if (_smokeMode || _savedOnExit || (!_settings.FritzSyncEnabled && !_pendingManualSync)) return;
        if (!await _syncGate.WaitAsync(0))
        {
            _routerSyncDebounce.Interval = TimeSpan.FromMilliseconds(500);
            _routerSyncDebounce.Start();
            return;
        }
        bool forcePhonebooks = _pendingPhonebookRefresh;
        _pendingPhonebookRefresh = false;
        _pendingManualSync = false;
        int generation = _syncGeneration;
        CancellationToken token = _syncCancellation.Token;
        SetSyncButtonsBusy(true);
        try
        {
            FritzAccessOptions access = StoredFritzOptions();
            using var client = new FritzTr064Client();
            SetSyncStatus("Anrufliste der FRITZ!Box wird abgeglichen …");
            FritzCallListSnapshot snapshot = await client.GetCallListAsync(access, token);
            if (generation != _syncGeneration || _savedOnExit) return;
            bool identityChanged = !string.Equals(_verifiedRouterIdentity, snapshot.RouterIdentity, StringComparison.Ordinal);
            if (identityChanged)
            {
                if (_verifiedRouterIdentity.Length > 0) _settings.LastPhonebookSync = null;
                _verifiedRouterIdentity = snapshot.RouterIdentity;
                _routerContacts.Clear();
                _routerContactLookup.Clear();
                _routerPhonebookLoaded = false;
                _routerCacheWritable = true;
                if (_settings.SyncPhonebooks)
                {
                    try
                    {
                        _routerContacts.AddRange(_store.LoadRouterContacts(snapshot.RouterIdentity));
                        _routerPhonebookLoaded = _routerContacts.Count > 0;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        _routerCacheWritable = false;
                        ShowWarning("Der gespeicherte FRITZ!Box-Telefonbuchbestand konnte nicht gelesen werden. Die Datei wird nicht überschrieben: " + error.Message);
                    }
                }
            }
            HistoryMergeResult merged = _tracker.MergeRouterCalls(snapshot, _settings.CountryCode, _settings.AreaCode);
            _settings.LastSuccessfulSync = DateTime.Now;
            _settingsDirty = true;
            _historyDirty = true;
            RefreshContacts();
            RefreshHistory();
            SavePending();
            string outcome = "Anrufliste abgeglichen: " + merged.Added + " neu, " + merged.Updated + " aktualisiert.";
            if (merged.Ambiguous > 0) outcome += " " + merged.Ambiguous + " Einträge waren nicht eindeutig zuzuordnen und wurden separat erhalten.";
            if (merged.Deferred > 0) outcome += " Laufende oder noch unklare Gespräche werden später erneut geprüft.";
            bool refreshBooks = _settings.SyncPhonebooks && (forcePhonebooks || !_routerPhonebookLoaded || !_settings.LastPhonebookSync.HasValue || DateTime.Now - _settings.LastPhonebookSync.Value > TimeSpan.FromHours(6));
            if (refreshBooks)
            {
                SetSyncStatus(outcome + " Telefonbücher werden geladen …");
                try
                {
                    IReadOnlyList<Contact> contacts = await client.GetPhonebooksAsync(access, token);
                    if (generation != _syncGeneration || _savedOnExit) return;
                    _routerContacts.Clear();
                    _routerContacts.AddRange(contacts);
                    _routerPhonebookLoaded = true;
                    _settings.LastPhonebookSync = DateTime.Now;
                    _settingsDirty = true;
                    if (_routerCacheWritable)
                    {
                        try { _store.SaveRouterContacts(snapshot.RouterIdentity, contacts); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        { ShowWarning("Die FRITZ!Box-Kontakte sind für diese Sitzung geladen, konnten aber nicht gespeichert werden: " + error.Message); }
                    }
                    RefreshContacts();
                    SavePending();
                    outcome += " " + contacts.Count + " FRITZ!Box-Rufnummern geladen.";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    if (generation != _syncGeneration || _savedOnExit) return;
                    SetSyncStatus(outcome + " Telefonbuch nicht aktualisiert: " + error.Message + " Vorhandene Kontakte bleiben erhalten.", error: true);
                    UpdateLastSyncLabel();
                    return;
                }
            }
            if (generation == _syncGeneration && !_savedOnExit) { SetSyncStatus(outcome); UpdateLastSyncLabel(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _syncGeneration && !_savedOnExit)
                SetSyncStatus("Listenabgleich nicht möglich: " + error.Message + " Die Live-Anrufanzeige arbeitet unabhängig weiter.", error: true);
        }
        finally { _syncGate.Release(); if (!_savedOnExit) SetSyncButtonsBusy(false); }
    }

    private void UpdateCredentialStatus()
    {
        CredentialStatus.Text = _credentialVault.Exists
            ? "Ein Kennwort ist Windows-geschützt gespeichert. Leer lassen, um es für denselben Zugang beizubehalten."
            : "Noch kein Kennwort gespeichert. Für die reine Live-Anzeige ist keines nötig.";
    }

    private void UpdateLastSyncLabel()
    {
        LastSyncText.Text = _settings.LastSuccessfulSync.HasValue
            ? "Anrufliste: " + _settings.LastSuccessfulSync.Value.ToString("dd.MM.yyyy HH:mm:ss")
            : "Noch kein erfolgreicher Listenabgleich.";
        if (_settings.LastPhonebookSync.HasValue) LastSyncText.Text += " · Telefonbücher: " + _settings.LastPhonebookSync.Value.ToString("dd.MM.yyyy HH:mm");
    }

    private void SetSyncStatus(string text, bool error = false)
    {
        FritzSyncStatus.Text = text;
        FritzSyncStatus.Foreground = BrushOf(error ? "#FFB3B8" : "#F5F9FF");
    }

    private void SetSyncButtonsBusy(bool busy)
    {
        TestFritzButton.IsEnabled = !busy;
        SyncNowButton.IsEnabled = !busy;
    }
}
