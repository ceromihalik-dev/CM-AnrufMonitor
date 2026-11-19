using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CM.AnrufMonitor.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CM.AnrufMonitor.App;

public partial class MainWindow : Window
{
    private readonly CallTracker _tracker = new();
    private readonly ObservableCollection<CallRow> _rows = new();
    private readonly ObservableCollection<Contact> _contactRows = new();
    private readonly HashSet<string> _writeBlocked = new(StringComparer.Ordinal);
    private readonly List<Contact> _contacts = new();
    private readonly Dictionary<string, string> _contactLookup = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly string _dataDirectory;
    private readonly bool _smokeMode;
    private readonly AppStore _store;
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private int _connectionGeneration;
    private bool _ready;
    private bool _exitRequested;
    private bool _savedOnExit;
    private bool _sessionEnding;
    private bool _hiddenHintShown;
    private bool _refreshingRows;
    private bool _historyDirty;
    private bool _settingsDirty;
    private bool _contactsDirty;
    private bool _manualConnection;
    private bool _updatingLayout;
    private MonitorConnectionState _connectionState;
    private string _lastConnectionError = "";
    private string? _selectedCallId;
    private CallRecord? _displayedCall;
    private DateTime? _demoStarted;
    private DateTime _lastRefreshDate = DateTime.Today;
    private double _fullWidth = 980;
    private double _fullHeight = 790;
    private double _compactWidth = 390;
    private double _compactHeight = 440;
    private Contact? _editingContact;

    public MainWindow(string? dataDirectory = null, bool smokeMode = false)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CM", "AnrufMonitor");
        _smokeMode = smokeMode;
        InitializeComponent();
        AboutVersionText.Text = "CM AnrufMonitor · Version " + AppVersion.Display;
        _store = new AppStore(_dataDirectory);
        _settings = LoadSafely("settings", _store.LoadSettings, new AppSettings());
        _tracker.LoadHistory(LoadSafely<IReadOnlyList<CallRecord>>("history", _store.LoadHistory, Array.Empty<CallRecord>()));
        _contacts.AddRange(LoadSafely<IReadOnlyList<Contact>>("contacts", _store.LoadContacts, Array.Empty<Contact>()));
        _store.Warning += message => Dispatcher.BeginInvoke(() => ShowWarning(message));
        HistoryGrid.ItemsSource = _rows;
        ContactsGrid.ItemsSource = _contactRows;
        LoadSettingsControls();
        StorageInfo.Text = "Einstellungen, Kontakte, Internetnamen und bis zu 2.000 Anrufe werden lokal gespeichert. Zugangsdaten liegen Windows-geschützt in Credentials.bin. Der automatische Abgleich kommuniziert nur mit deiner FRITZ!Box. Die Namenssuche für den Verlauf oder eine einzelne Nummer wird ausschließlich per Klick gestartet und übermittelt die betreffenden unbekannten Rufnummern einzeln an Das Örtliche.\n\nDatenordner: " + _dataDirectory;
        _clock.Tick += Clock_Tick;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePending(); };
        _ready = true;
        InitializeFritzSync();
        InitializeNameLookup();
        RestoreWindow();
        RefreshContacts();
        RefreshHistory();
        if (_store.Warnings.Count > 0) ShowWarning(string.Join("\n", _store.Warnings));
        if (_writeBlocked.Count > 0) ShowWarning("Mindestens eine Datendatei konnte nicht gelesen oder gesichert werden. Für diesen Bereich wird nichts überschrieben. Prüfe die Zugriffsrechte im Datenordner und starte die App danach neu.");
    }

    private T LoadSafely<T>(string area, Func<T> loader, T fallback)
    {
        try { return loader(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _writeBlocked.Add(area);
            ShowWarning("Die Daten für „" + area + "“ konnten nicht gelesen werden: " + error.Message + " Dieser Bereich wird zum Schutz der vorhandenen Datei nicht überschrieben.");
            return fallback;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_smokeMode) CreateTray();
        _clock.Start();
        _ = RefreshWindowChrome();
        if (_settings.AutoConnect && !_smokeMode) StartMonitor(manual: false);
        else if (_tracker.Snapshot().Count == 0 && !_settings.CompactMode) MainTabs.SelectedIndex = 2;
        StartRouterSync();
    }

    private void CreateTray()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("CM AnrufMonitor öffnen", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
            menu.Items.Add("Desktop-Widget", null, (_, _) => Dispatcher.BeginInvoke(() => { SetCompact(true); RestoreFromTray(); }));
            menu.Items.Add("Einrichtung", null, (_, _) => Dispatcher.BeginInvoke(() => { SetCompact(false); MainTabs.SelectedIndex = 2; RestoreFromTray(); }));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Beenden", null, (_, _) => Dispatcher.BeginInvoke(RequestExit));
            _tray = new Forms.NotifyIcon
            {
                Text = "CM AnrufMonitor · nicht verbunden",
                ContextMenuStrip = menu,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Information,
                Visible = true
            };
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
            _tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        }
        catch (Exception error)
        {
            _tray?.Dispose();
            _tray = null;
            ShowWarning("Das Symbol im Infobereich konnte nicht erstellt werden. Beim Schließen wird die App deshalb beendet. " + error.Message);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        EnsureVisible();
        Activate();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }
        if (e.ClickCount == 2 && !_settings.CompactMode) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();
    public void AllowSessionExit() { _sessionEnding = true; _exitRequested = true; }
    private void RequestExit() { _exitRequested = true; Close(); }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (DeferExitForNameLookup(e)) return;
        if (!_exitRequested && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            CaptureWindow();
            SavePending();
            if (!_hiddenHintShown)
            {
                _hiddenHintShown = true;
                _tray.ShowBalloonTip(3500, "CM AnrufMonitor läuft weiter", "Zum Öffnen auf dieses Symbol doppelklicken. Zum Beenden: Rechtsklick → Beenden.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        if (_savedOnExit) return;
        _clock.Stop();
        _saveTimer.Stop();
        StopRouterSync();
        ++_connectionGeneration;
        _monitorCancellation?.Cancel();
        _monitorTask = null;
        _tracker.MarkInterrupted();
        _historyDirty = true;
        CaptureWindow();
        bool saved = SavePending();
        if (!saved && !_sessionEnding && MessageBox.Show(this,
            "Einige Änderungen konnten nicht gespeichert werden. Beim Beenden können sie verloren gehen.\n\nTrotzdem beenden? Wähle „Nein“, um die Daten weiter anzuzeigen oder den Verlauf zu exportieren.",
            "Nicht gespeicherte Änderungen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _exitRequested = false;
            _clock.Start();
            StartRouterSync();
            ConnectButton.Content = "Verbinden";
            HostBox.IsEnabled = true;
            PortBox.IsEnabled = true;
            OnConnectionState(MonitorConnectionState.Disconnected, "Überwachung angehalten. Änderungen konnten noch nicht gespeichert werden.");
            RestoreFromTray();
            return;
        }
        _savedOnExit = true;
        _tray?.Dispose();
        _tray = null;
    }

    private void LoadSettingsControls()
    {
        HostBox.Text = _settings.Host;
        PortBox.Text = _settings.Port.ToString(CultureInfo.InvariantCulture);
        CountryCodeBox.Text = _settings.CountryCode;
        AreaCodeBox.Text = _settings.AreaCode;
        AlwaysOnTopBox.IsChecked = _settings.AlwaysOnTop;
        NotificationsBox.IsChecked = _settings.Notifications;
        try
        {
            if (!_smokeMode)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                _settings.StartWithWindows = key?.GetValue("CMAnrufMonitor") is string value && !string.IsNullOrWhiteSpace(value);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { ShowWarning("Die Windows-Autostart-Einstellung konnte nicht geprüft werden: " + error.Message); }
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        AutoConnectBox.IsChecked = _settings.AutoConnect;
        OpacitySlider.Value = Math.Clamp(_settings.Opacity * 100, 70, 100);
        OpacityText.Text = Math.Round(OpacitySlider.Value).ToString(CultureInfo.InvariantCulture) + " %";
        Topmost = _settings.AlwaysOnTop;
        Opacity = Math.Clamp(_settings.Opacity, .7, 1);
        ConnectionHost.Text = _settings.Host + " · Port " + _settings.Port;
    }

    private bool ReadConnectionControls()
    {
        string host = HostBox.Text.Trim();
        if (host.Length is < 1 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowWarning("Bitte die FRITZ!Box-Adresse als Hostnamen oder IP-Adresse eingeben, zum Beispiel fritz.box oder 192.168.178.1. Kein http:// und keinen Port an die Adresse anhängen.");
            HostBox.Focus();
            return false;
        }
        if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            ShowWarning("Bitte einen gültigen TCP-Port eingeben. Der FRITZ!Box-Anrufmonitor verwendet normalerweise 1012.");
            PortBox.Focus();
            return false;
        }
        if (!host.Equals(_settings.Host, StringComparison.OrdinalIgnoreCase)) ChangeRouterScope();
        _settings.Host = host;
        _settings.Port = port;
        ConnectionHost.Text = host + " · Port " + port;
        return true;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadConnectionControls()) return;
        string country = CountryCodeBox.Text.Trim();
        if (country.Length is < 2 or > 4 || country[0] != '+' || country[1] == '0' || !country[1..].All(char.IsAsciiDigit))
        {
            ShowWarning("Bitte die Landesvorwahl mit + eingeben, zum Beispiel +49. Die Ortsvorwahl kann leer bleiben.");
            CountryCodeBox.Focus();
            return;
        }
        string area = AreaCodeBox.Text.Trim();
        if (area.Length > 20 || area.Any(c => !char.IsAsciiDigit(c) && c != '+' && c != ' ') || area.Count(c => c == '+') > 1 || area.Contains('+') && !area.StartsWith('+'))
        {
            ShowWarning("Die Ortsvorwahl darf nur Ziffern, Leerzeichen und gegebenenfalls ein führendes + enthalten, zum Beispiel 030.");
            AreaCodeBox.Focus();
            return;
        }
        bool startWithWindows = StartWithWindowsBox.IsChecked == true;
        // Explicitly saving settings also repairs an autostart path after moving the portable app.
        try { if (!_smokeMode) SetAutostart(startWithWindows); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
            ShowWarning("Der Windows-Autostart konnte nicht geändert werden: " + error.Message);
            return;
        }
        bool regionChanged = country != _settings.CountryCode || area != _settings.AreaCode;
        if (regionChanged) { CancelRouterOperation(); CancelNameLookup(contextChanged: true); }
        _settings.StartWithWindows = startWithWindows;
        _settings.CountryCode = country;
        _settings.AreaCode = area;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settings.Notifications = NotificationsBox.IsChecked == true;
        _settings.AutoConnect = AutoConnectBox.IsChecked == true;
        _settings.Opacity = Math.Clamp(OpacitySlider.Value / 100, .7, 1);
        Topmost = _settings.AlwaysOnTop;
        Opacity = _settings.Opacity;
        _settingsDirty = true;
        bool saved = SavePending();
        SettingsSavedText.Text = saved ? "Gespeichert" : "Speichern nicht möglich";
        RebuildContactLookup();
        RefreshHistory();
        if (saved) RequestRouterSync(forcePhonebooks: true);
        if (_monitorTask is { IsCompleted: false }) ConnectionDetail.Text += "\nEine geänderte Box-Adresse wird nach erneutem Verbinden verwendet.";
    }

    private static void SetAutostart(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("Der Autostart-Schlüssel ist nicht zugänglich.");
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Der Programmpfad ist unbekannt.");
            if (!File.Exists(executable) || executable.Contains('"')) throw new InvalidOperationException("Ungültiger Programmpfad.");
            key.SetValue("CMAnrufMonitor", "\"" + executable + "\"", RegistryValueKind.String);
        }
        else key.DeleteValue("CMAnrufMonitor", false);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText is not null) OpacityText.Text = Math.Round(e.NewValue).ToString(CultureInfo.InvariantCulture) + " %";
        if (_ready) SettingsSavedText.Text = "Zum Übernehmen speichern";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorTask is { IsCompleted: false }) StopMonitor();
        else if (ReadConnectionControls()) StartMonitor(manual: true);
    }

    private void StartMonitor(bool manual)
    {
        if (_smokeMode) return;
        if (_monitorTask is { IsCompleted: false }) return;
        _demoStarted = null;
        _lastConnectionError = "";
        _manualConnection = manual;
        int generation = ++_connectionGeneration;
        _monitorCancellation?.Dispose();
        _monitorCancellation = new CancellationTokenSource();
        var client = new FritzMonitorClient();
        client.LineReceived += line => Dispatcher.BeginInvoke(() => { if (generation == _connectionGeneration && !_savedOnExit) OnMonitorLine(line); });
        client.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() => { if (generation == _connectionGeneration && !_savedOnExit) OnConnectionState(state, detail); });
        ConnectButton.Content = "Trennen";
        HostBox.IsEnabled = false;
        PortBox.IsEnabled = false;
        _settingsDirty = true;
        ScheduleSave();
        _monitorTask = RunMonitorAsync(client, _settings.Host, _settings.Port, _monitorCancellation.Token, generation);
    }

    private async Task RunMonitorAsync(FritzMonitorClient client, string host, int port, CancellationToken token, int generation)
    {
        try { await client.RunAsync(host, port, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _connectionGeneration) ShowWarning("Die Verbindung konnte nicht gestartet werden: " + error.Message);
        }
        finally
        {
            if (generation == _connectionGeneration && !_savedOnExit)
            {
                ConnectButton.Content = "Verbinden";
                HostBox.IsEnabled = true;
                PortBox.IsEnabled = true;
            }
        }
    }

    private void StopMonitor()
    {
        ++_connectionGeneration;
        _monitorCancellation?.Cancel();
        _monitorTask = null;
        _tracker.MarkInterrupted();
        _historyDirty = true;
        _settings.AutoConnect = false;
        AutoConnectBox.IsChecked = false;
        _settingsDirty = true;
        _lastConnectionError = "";
        ConnectButton.Content = "Verbinden";
        HostBox.IsEnabled = true;
        PortBox.IsEnabled = true;
        OnConnectionState(MonitorConnectionState.Disconnected, "Überwachung angehalten. Zum Fortsetzen „Verbinden“ wählen.");
        RefreshHistory();
        ScheduleSave();
    }

    private void OnConnectionState(MonitorConnectionState state, string detail)
    {
        MonitorConnectionState previous = _connectionState;
        _connectionState = state;
        if (state == MonitorConnectionState.Disconnected && previous == MonitorConnectionState.Connected)
        {
            _tracker.MarkInterrupted();
            _historyDirty = true;
            RefreshHistory();
            ScheduleSave();
        }
        if (state == MonitorConnectionState.Disconnected) _lastConnectionError = detail;
        if (state == MonitorConnectionState.Connected)
        {
            _tracker.SetRouterIdentity("");
            _lastConnectionError = "";
            if (_manualConnection)
            {
                _manualConnection = false;
                _settings.AutoConnect = AutoConnectBox.IsChecked == true;
                _settingsDirty = true;
                ScheduleSave();
            }
            RequestRouterSync();
        }
        string title = state switch
        {
            MonitorConnectionState.Connected => "FRITZ!Box verbunden",
            MonitorConnectionState.Connecting => "Verbindung wird aufgebaut",
            MonitorConnectionState.Reconnecting => "Wiederverbindung läuft",
            _ => "Nicht verbunden"
        };
        Brush color = state == MonitorConnectionState.Connected ? BrushOf("#9BEAC5") : state == MonitorConnectionState.Disconnected ? BrushOf("#FFAFB3") : BrushOf("#FFE23A");
        ConnectionTitle.Text = title;
        ConnectionDot.Fill = color;
        CompactConnection.Text = "● " + title;
        CompactConnection.Foreground = color;
        ConnectionDetail.Text = state == MonitorConnectionState.Reconnecting && _lastConnectionError.Length > 0 ? _lastConnectionError + "\n" + detail : detail;
        FooterStatus.Text = state == MonitorConnectionState.Connected ? "Live-Verbindung aktiv · Verlauf wird lokal gespeichert" : detail;
        if (_tray is not null) _tray.Text = ("CM AnrufMonitor · " + title)[..Math.Min(63, ("CM AnrufMonitor · " + title).Length)];
        UpdateHero();
    }

    private void OnMonitorLine(string line)
    {
        CallRecord? changed = _tracker.Apply(line);
        if (changed is null) return;
        _demoStarted = null;
        _historyDirty = true;
        if (changed.State is CallState.Ringing or CallState.Dialing or CallState.Connected) _selectedCallId = null;
        RefreshHistory();
        ScheduleSave();
        if (line.Contains(";DISCONNECT;", StringComparison.Ordinal)) RequestRouterSync(delayMilliseconds: 3000);
        if (changed.State == CallState.Ringing && _settings.Notifications && _tray is not null)
        {
            string name = DisplayName(changed);
            string number = DisplayNumber(changed.RemoteNumber);
            string body = name + "\n" + number + (string.IsNullOrWhiteSpace(changed.LocalNumber) ? "" : "\nAn: " + changed.LocalNumber);
            _tray.ShowBalloonTip(6000, "Eingehender Anruf", body[..Math.Min(body.Length, 255)], Forms.ToolTipIcon.Info);
        }
    }

    private void Clock_Tick(object? sender, EventArgs e)
    {
        if (_nextInternetReviewTime.HasValue && _nextInternetReviewTime.Value <= DateTimeOffset.UtcNow)
        {
            UpdateInternetCacheInfo();
            RefreshHistory();
        }
        CompactTime.Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (_demoStarted.HasValue && DateTime.Now - _demoStarted.Value > TimeSpan.FromSeconds(16))
        {
            _demoStarted = null;
            FooterStatus.Text = _connectionState == MonitorConnectionState.Connected ? "Live-Verbindung aktiv · Verlauf wird lokal gespeichert" : "Demo beendet · Noch nicht verbunden";
        }
        if (_lastRefreshDate != DateTime.Today)
        {
            _lastRefreshDate = DateTime.Today;
            RefreshHistory();
        }
        else UpdateHero();
    }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker.Snapshot().Any(call => call.IsActive))
        {
            ShowWarning("Gerade wird ein echtes Gespräch angezeigt. Die Demo ist verfügbar, sobald kein Gespräch mehr aktiv ist.");
            return;
        }
        _demoStarted = DateTime.Now;
        _selectedCallId = null;
        UpdateHero();
        FooterStatus.Text = "DEMO · Simulierter Anruf für 16 Sekunden. Kein Eintrag im echten Anrufverlauf.";
        if (!_settings.CompactMode) MainTabs.SelectedIndex = 0;
    }

    private void RefreshHistory()
    {
        if (!_ready) return;
        IReadOnlyList<CallRecord> snapshot = _tracker.Snapshot();
        CallRecord[] today = snapshot.Where(call => call.StartedAt.Date == DateTime.Today && !call.IsDemo).ToArray();
        IncomingCount.Text = today.Count(call => call.Direction == CallDirection.Incoming).ToString(CultureInfo.CurrentCulture);
        OutgoingCount.Text = today.Count(call => call.Direction == CallDirection.Outgoing).ToString(CultureInfo.CurrentCulture);
        int missed = today.Count(call => call.State == CallState.Missed);
        MissedCount.Text = missed.ToString(CultureInfo.CurrentCulture);
        CompactMissed.Text = "Heute " + missed + " verpasst";
        string search = SearchBox.Text.Trim();
        int filter = FilterBox.SelectedIndex;
        IEnumerable<CallRecord> visible = snapshot.Where(call => !call.IsDemo);
        visible = filter switch
        {
            1 => visible.Where(call => call.State == CallState.Missed),
            2 => visible.Where(call => call.Direction == CallDirection.Incoming),
            3 => visible.Where(call => call.Direction == CallDirection.Outgoing),
            _ => visible
        };
        IEnumerable<CallRow> mapped = visible.OrderByDescending(call => call.StartedAt).Select(call => new CallRow(call, DisplayName(call), NameSource(call)));
        if (search.Length > 0)
            mapped = mapped.Where(row => row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.Number.Contains(search, StringComparison.OrdinalIgnoreCase) || row.LocalNumber.Contains(search, StringComparison.OrdinalIgnoreCase));
        _refreshingRows = true;
        _rows.Clear();
        foreach (CallRow row in mapped) _rows.Add(row);
        if (_selectedCallId is not null) HistoryGrid.SelectedItem = _rows.FirstOrDefault(row => row.Call.Id == _selectedCallId);
        _refreshingRows = false;
        EmptyHistory.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHistoryTitle.Text = snapshot.Count == 0 ? "Hier erscheinen deine Anrufe" : "Keine passenden Anrufe";
        EmptyHistorySubtitle.Text = snapshot.Count == 0 ? "Anrufe erscheinen über die Live-Verbindung oder den FRITZ!Box-Abgleich." : "Suchbegriff oder Filter anpassen.";
        HistoryFooter.Text = _rows.Count + " von " + snapshot.Count + " Anrufen · Suche nach Name oder Rufnummer · Doppelklick für Details";
        UpdateHero();
    }

    private void UpdateHero()
    {
        if (!_ready) return;
        IReadOnlyList<CallRecord> snapshot = _tracker.Snapshot();
        CallRecord[] active = snapshot.Where(call => call.IsActive).OrderBy(call => call.State == CallState.Ringing ? 0 : 1).ThenByDescending(call => call.StartedAt).ToArray();
        CallRecord? selected = _selectedCallId is null ? null : snapshot.FirstOrDefault(call => call.Id == _selectedCallId);
        CallRecord? call = active.FirstOrDefault() ?? selected ?? snapshot.OrderByDescending(item => item.StartedAt).FirstOrDefault();
        bool demo = _demoStarted.HasValue && active.Length == 0;
        if (demo)
        {
            double seconds = (DateTime.Now - _demoStarted!.Value).TotalSeconds;
            call = new CallRecord
            {
                Id = "demo", ConnectionId = "demo", StartedAt = _demoStarted.Value,
                ConnectedAt = seconds >= 5 ? _demoStarted.Value.AddSeconds(5) : null,
                RemoteNumber = "030 12345678", LocalNumber = "030 98765432", Direction = CallDirection.Incoming,
                State = seconds >= 5 ? CallState.Connected : CallState.Ringing, IsDemo = true
            };
        }
        _displayedCall = call;
        if (call is null)
        {
            bool connected = _connectionState == MonitorConnectionState.Connected;
            HeroState.Text = connected ? "LIVE-ANZEIGE AKTIV" : "BEREIT FÜR DEN ERSTEN ANRUF";
            HeroState.Foreground = BrushOf("#FFE23A");
            HeroName.Text = connected ? "Wartet auf Anrufe" : "Verbinde deine FRITZ!Box";
            HeroName.ToolTip = null;
            CompactName.ToolTip = null;
            HeroNumber.Text = connected ? "Der nächste Anruf erscheint hier automatisch." : "Anrufmonitor am Telefon mit #96*5* einschalten.";
            HeroDetails.Text = connected ? "Das Fenster kann als kompaktes Widget auf dem Desktop bleiben." : "Anschließend unter Einrichtung auf „Verbinden“ klicken.";
            HeroDuration.Text = "";
            ActiveCountText.Text = "";
            CopyHeroButton.IsEnabled = false;
            CompactCopyButton.IsEnabled = false;
            ReverseHeroButton.IsEnabled = false;
            CompactState.Text = HeroState.Text;
            CompactName.Text = connected ? "Wartet auf Anrufe" : "Bereit zur Einrichtung";
            CompactNumber.Text = connected ? "FRITZ!Box-Anrufmonitor ist aktiv" : "#96*5* am Telefon wählen";
            CompactDuration.Text = "";
            return;
        }
        string state = StateLabel(call.State);
        string prefix = demo ? "DEMO · " : _smokeMode && _smokeHistoryDemo ? "DEMO-DATEN · " : call.IsActive ? "" : selected is not null ? "AUSGEWÄHLT · " : "LETZTER ANRUF · ";
        HeroState.Text = prefix + state.ToUpperInvariant();
        HeroState.Foreground = StateColor(call.State);
        HeroName.Text = demo ? "Max Mustermann · Demo" : DisplayName(call);
        HeroName.ToolTip = NameSource(call);
        HeroNumber.Text = DisplayNumber(call.RemoteNumber);
        string ownNumber = string.IsNullOrWhiteSpace(call.LocalNumber) ? "Eigene Nummer unbekannt" : "Eigene Nummer: " + call.LocalNumber;
        HeroDetails.Text = demo ? "Simulation. Es wird kein Anruf getätigt und nichts gespeichert." : DirectionLabel(call.Direction) + " · " + call.StartedAt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture) + " · " + ownNumber;
        if (!demo && InternetName(call) is not null && PreferredName(call) is null) HeroDetails.Text += " · " + NameSource(call);
        HeroDuration.Text = call.State == CallState.Connected && call.ConnectedAt.HasValue ? FormatDuration(Math.Max(0, (int)(DateTime.Now - call.ConnectedAt.Value).TotalSeconds)) : call.State is CallState.Ringing or CallState.Dialing ? "" : call.ConnectedAt.HasValue || call.DurationSeconds > 0 ? FormatDuration(call.DurationSeconds) : "";
        ActiveCountText.Text = active.Length > 1 ? active.Length + " aktive Gespräche" : "";
        bool canCopy = !string.IsNullOrWhiteSpace(call.RemoteNumber);
        CopyHeroButton.IsEnabled = canCopy;
        CompactCopyButton.IsEnabled = canCopy;
        ReverseHeroButton.IsEnabled = !call.IsDemo && ExternalLookupNumber(call.RemoteNumber) is not null;
        if (call.DurationIsApproximate && HeroDuration.Text.Length > 0) HeroDuration.Text = "≈ " + HeroDuration.Text;
        CompactState.Text = HeroState.Text;
        CompactState.Foreground = HeroState.Foreground;
        CompactName.Text = HeroName.Text;
        CompactName.ToolTip = HeroName.ToolTip;
        CompactNumber.Text = HeroNumber.Text;
        CompactDuration.Text = HeroDuration.Text;
    }

    private string DisplayName(CallRecord call)
        => PreferredName(call) ?? InternetName(call)?.Name ?? (string.IsNullOrWhiteSpace(call.RemoteNumber) ? "Rufnummer unbekannt" : "Unbekannter Kontakt");

    private string? PreferredName(CallRecord call)
    {
        if (call.IsDemo) return "Max Mustermann · Demo";
        string key = ContactBook.NormalizeNumber(call.RemoteNumber, _settings.CountryCode, _settings.AreaCode);
        _contactLookup.TryGetValue(key, out string? name);
        if (name is not null) return name;
        if (_verifiedRouterIdentity.Length > 0 && call.RouterIdentity == _verifiedRouterIdentity) _routerContactLookup.TryGetValue(key, out name);
        if (name is not null) return name;
        if (!string.IsNullOrWhiteSpace(call.RouterName)) return call.RouterName;
        return null;
    }

    private static string DisplayNumber(string? number) => string.IsNullOrWhiteSpace(number) ? "Unterdrückt oder nicht übermittelt" : number;
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (_ready) RefreshHistory(); }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingRows) return;
        _selectedCallId = (HistoryGrid.SelectedItem as CallRow)?.Call.Id;
        UpdateHero();
    }
    private void History_DoubleClick(object sender, MouseButtonEventArgs e) => ShowSelectedDetails();
    private void ShowDetails_Click(object sender, RoutedEventArgs e) => ShowSelectedDetails();
    private void ShowSelectedDetails()
    {
        if (HistoryGrid.SelectedItem is not CallRow row) return;
        CallRecord call = row.Call;
        string details = row.Name + "\n" + row.Number + "\nQuelle des Namens: " + row.NameSource + "\n\n" + DirectionLabel(call.Direction) + " · " + StateLabel(call.State) +
            "\nBeginn: " + call.StartedAt.ToString("dd.MM.yyyy HH:mm:ss") +
            (call.ConnectedAt.HasValue ? "\nAngenommen: " + call.ConnectedAt.Value.ToString("dd.MM.yyyy HH:mm:ss") : "") +
            (call.EndedAt.HasValue ? "\nEnde: " + call.EndedAt.Value.ToString("dd.MM.yyyy HH:mm:ss") : "") +
            "\nGesprächsdauer: " + row.Duration + (call.DurationIsApproximate ? " (aus der FRITZ!Box, auf Minuten gerundet)" : "") + "\nEigene Nummer: " + row.LocalNumber +
            (call.Extension.Length > 0 ? "\nNebenstelle: " + call.Extension : "") +
            (call.Line.Length > 0 ? "\nLeitung: " + call.Line : "") +
            (call.State == CallState.Interrupted ? "\n\nDie Verbindung zur FRITZ!Box oder die Überwachung wurde unterbrochen. Der endgültige Gesprächsstatus ist unbekannt." : "");
        MessageBox.Show(this, details, "Anrufdetails", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyHero_Click(object sender, RoutedEventArgs e) => CopyNumber(_displayedCall?.RemoteNumber);
    private void CopySelected_Click(object sender, RoutedEventArgs e) => CopyNumber((HistoryGrid.SelectedItem as CallRow)?.Call.RemoteNumber);
    private void CopyNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return;
        try { Clipboard.SetText(number); FooterStatus.Text = "Rufnummer kopiert: " + number; }
        catch (ExternalException) { ShowWarning("Die Zwischenablage ist gerade belegt. Bitte erneut versuchen."); }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker.Snapshot().Count == 0) return;
        if (_writeBlocked.Contains("history")) { ShowWarning("Der Verlauf ist wegen eines Lesefehlers gesperrt. Bitte den Datenordner prüfen und die App neu starten."); return; }
        if (MessageBox.Show(this, "Den gesamten lokal gespeicherten Anrufverlauf löschen? Laufende Gespräche bleiben erhalten. Die Anrufliste in der FRITZ!Box wird nicht verändert.\n\nBei aktiviertem Listenabgleich lädt der nächste Abgleich die vorhandenen FRITZ!Box-Einträge erneut.", "Anrufverlauf leeren", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        CancelNameLookup(contextChanged: true);
        _tracker.ClearHistory();
        _selectedCallId = null;
        _historyDirty = true;
        RefreshHistory();
        SavePending();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { ShowWarning("Für den aktuellen Suchfilter gibt es keine Anrufe zum Exportieren."); return; }
        var dialog = new SaveFileDialog { Title = "Angezeigten Anrufverlauf exportieren", Filter = "CSV-Datei (*.csv)|*.csv", FileName = "CM-AnrufMonitor_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            writer.NewLine = "\r\n";
            writer.WriteLine("Beginn;Richtung;Status;Name;Namensquelle;Rufnummer;Eigene Nummer;Dauer Sekunden;Dauerart;Ende");
            foreach (CallRow row in _rows)
            {
                CallRecord call = row.Call;
                writer.WriteLine(string.Join(";", new[] { call.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DirectionLabel(call.Direction), StateLabel(call.State), row.Name, row.NameSource, call.RemoteNumber, call.LocalNumber, call.DurationSeconds.ToString(CultureInfo.InvariantCulture), call.DurationSeconds == 0 && call.ConnectedAt is null ? "Keine Gesprächsdauer übermittelt" : call.DurationIsApproximate ? "Auf Minuten gerundet" : "Live in Sekunden", call.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "" }.Select(SafeCsv)));
            }
            FooterStatus.Text = _rows.Count + " Anrufe exportiert: " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowWarning("Der Export konnte nicht gespeichert werden: " + error.Message); }
    }

    internal static string SafeCsv(string value)
    {
        string trimmed = value.TrimStart(' ', '\t', '\r', '\n');
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void RefreshContacts()
    {
        RebuildContactLookup();
        _contactRows.Clear();
        foreach (Contact contact in _contacts.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Number)) _contactRows.Add(contact);
        ContactsCount.Text = _contacts.Count + " Rufnummern";
        ContactSourceInfo.Text = "Eigene Kontakte haben Vorrang. Zusätzlich sind " + _routerContacts.Count + " Rufnummern aus FRITZ!Box-Telefonbüchern für die automatische Namenserkennung geladen.";
        RefreshHistory();
    }

    private void RebuildContactLookup()
    {
        FillContactLookup(_contacts, _contactLookup);
        FillContactLookup(_routerContacts, _routerContactLookup);
    }

    private void FillContactLookup(IEnumerable<Contact> contacts, Dictionary<string, string> destination)
    {
        destination.Clear();
        var namesByNumber = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Contact contact in contacts)
        {
            string normalized = ContactBook.NormalizeNumber(contact.Number, _settings.CountryCode, _settings.AreaCode);
            if (normalized.Length == 0 || string.IsNullOrWhiteSpace(contact.Name)) continue;
            if (!namesByNumber.TryGetValue(normalized, out List<string>? names)) namesByNumber[normalized] = names = new List<string>();
            string name = contact.Name.Trim();
            if (names.Count < 4 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        foreach (var pair in namesByNumber) destination[pair.Key] = string.Join(" / ", pair.Value);
    }

    private void SaveContact_Click(object sender, RoutedEventArgs e)
    {
        if (_writeBlocked.Contains("contacts")) { ShowWarning("Kontakte sind wegen eines Lesefehlers gesperrt. Bitte den Datenordner prüfen und die App neu starten."); return; }
        string name = string.Join(" ", ContactNameBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string number = ContactNumberBox.Text.Trim();
        if (name.Length is < 1 or > 300 || ContactBook.NormalizeNumber(number, _settings.CountryCode, _settings.AreaCode).Length == 0)
        {
            ShowWarning("Bitte einen Namen und eine gültige Rufnummer eingeben. Bei externen Nummern möglichst die vollständige Vorwahl verwenden.");
            return;
        }
        var replacement = new Contact(name, number);
        if (_editingContact is null && !_contacts.Contains(replacement) && _contacts.Count >= 50000)
        {
            ShowWarning("Die lokale Kontaktliste enthält bereits 50.000 Rufnummern. Bitte zuerst nicht mehr benötigte Einträge entfernen.");
            return;
        }
        if (_editingContact is not null) _contacts.Remove(_editingContact);
        if (!_contacts.Contains(replacement)) _contacts.Add(replacement);
        _editingContact = null;
        SaveContactButton.Content = "Kontakt hinzufügen";
        ContactNameBox.Clear();
        ContactNumberBox.Clear();
        _contactsDirty = true;
        RefreshContacts();
        SavePending();
    }

    private void Contact_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactsGrid.SelectedItem is not Contact contact) return;
        _editingContact = contact;
        SaveContactButton.Content = "Änderungen speichern";
        ContactNameBox.Text = contact.Name;
        ContactNumberBox.Text = contact.Number;
    }

    private void RemoveContact_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsGrid.SelectedItem is not Contact contact) return;
        if (_writeBlocked.Contains("contacts")) { ShowWarning("Kontakte sind wegen eines Lesefehlers gesperrt."); return; }
        if (MessageBox.Show(this, "„" + contact.Name + "“ mit der Nummer " + contact.Number + " aus den lokalen Kontakten entfernen?", "Kontakt entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _contacts.Remove(contact);
        _editingContact = null;
        SaveContactButton.Content = "Kontakt hinzufügen";
        ContactNameBox.Clear();
        ContactNumberBox.Clear();
        _contactsDirty = true;
        RefreshContacts();
        SavePending();
    }

    private void NewContact_Click(object sender, RoutedEventArgs e)
    {
        ContactsGrid.SelectedItem = null;
        _editingContact = null;
        ContactNameBox.Clear();
        ContactNumberBox.Clear();
        SaveContactButton.Content = "Kontakt hinzufügen";
        ContactNameBox.Focus();
    }

    private void ContactFromCall_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not CallRow row || string.IsNullOrWhiteSpace(row.Call.RemoteNumber)) return;
        MainTabs.SelectedIndex = 1;
        ContactsGrid.SelectedItem = null;
        _editingContact = null;
        SaveContactButton.Content = "Kontakt hinzufügen";
        ContactNameBox.Clear();
        ContactNumberBox.Text = row.Call.RemoteNumber;
        ContactNameBox.Focus();
    }

    private void ImportFritz_Click(object sender, RoutedEventArgs e) => ImportContacts(true);
    private void ImportCsv_Click(object sender, RoutedEventArgs e) => ImportContacts(false);
    private void ImportContacts(bool fritzXml)
    {
        if (_writeBlocked.Contains("contacts")) { ShowWarning("Kontakte sind wegen eines Lesefehlers gesperrt. Bitte den Datenordner prüfen und die App neu starten."); return; }
        var dialog = new OpenFileDialog { Title = fritzXml ? "Gesichertes FRITZ!Box-Telefonbuch auswählen" : "Kontakte importieren (Name;Nummer)", Filter = fritzXml ? "FRITZ!Box-Telefonbuch (*.xml)|*.xml" : "CSV-Datei (*.csv)|*.csv", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            IReadOnlyList<Contact> imported = fritzXml ? ContactBook.ImportFritzXml(dialog.FileName) : ContactBook.ImportCsv(dialog.FileName);
            string Key(Contact item) => item.Name.Trim() + "\0" + ContactBook.NormalizeNumber(item.Number, _settings.CountryCode, _settings.AreaCode);
            var known = new HashSet<string>(_contacts.Select(Key), StringComparer.OrdinalIgnoreCase);
            var newContacts = imported.Where(item => known.Add(Key(item))).ToArray();
            if (_contacts.Count + newContacts.Length > 50000) throw new InvalidDataException("Die Kontaktliste darf höchstens 50.000 Rufnummern enthalten.");
            _contacts.AddRange(newContacts);
            _contactsDirty = true;
            RefreshContacts();
            bool saved = SavePending();
            FooterStatus.Text = newContacts.Length + " Rufnummern importiert. " + (imported.Count - newContacts.Length) + " vorhandene Einträge ausgelassen.";
            MessageBox.Show(this, newContacts.Length + " neue Rufnummern übernommen.\n" + (imported.Count - newContacts.Length) + " bereits vorhandene Einträge ausgelassen.\n\n" + (saved ? "Die Kontakte sind lokal gespeichert." : "Die Kontakte sind sichtbar, konnten aber noch nicht gespeichert werden. Beachte den Hinweis im Fenster."), "Telefonbuch importiert", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or ArgumentException)
        {
            ShowWarning("Das Telefonbuch konnte nicht importiert werden: " + error.Message);
        }
    }

    private void ToggleCompact_Click(object sender, RoutedEventArgs e) => SetCompact(!_settings.CompactMode);
    private void OpenSettings_Click(object sender, RoutedEventArgs e) { SetCompact(false); MainTabs.SelectedIndex = 2; }
    private void OpenHistory_Click(object sender, RoutedEventArgs e) { SetCompact(false); MainTabs.SelectedIndex = 0; }

    private void RestoreWindow()
    {
        _updatingLayout = true;
        bool compact = _settings.CompactMode;
        if (compact)
        {
            _compactWidth = Math.Clamp(_settings.WindowWidth, 360, 900);
            _compactHeight = Math.Clamp(_settings.WindowHeight, 410, 1000);
        }
        else
        {
            _fullWidth = Math.Clamp(_settings.WindowWidth, 800, 2500);
            _fullHeight = Math.Clamp(_settings.WindowHeight, 550, 1600);
        }
        Rect workArea = SystemParameters.WorkArea;
        if (!compact && (workArea.Width < 800 || workArea.Height < 550)) { compact = true; _settings.CompactMode = true; }
        _fullWidth = Math.Min(_fullWidth, Math.Max(800, workArea.Width - 12));
        _fullHeight = Math.Min(_fullHeight, Math.Max(550, workArea.Height - 12));
        _compactWidth = Math.Min(_compactWidth, Math.Max(360, workArea.Width - 12));
        _compactHeight = Math.Min(_compactHeight, Math.Max(410, workArea.Height - 12));
        ApplyCompactLayout(compact);
        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft.Value;
            Top = _settings.WindowTop.Value;
        }
        EnsureVisible();
        _updatingLayout = false;
    }

    private void SetCompact(bool compact)
    {
        if (!_ready || _settings.CompactMode == compact) return;
        _updatingLayout = true;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        if (_settings.CompactMode) { _compactWidth = Width; _compactHeight = Height; }
        else { _fullWidth = Width; _fullHeight = Height; }
        _settings.CompactMode = compact;
        ApplyCompactLayout(compact);
        EnsureVisible();
        _updatingLayout = false;
        CaptureWindow();
        ScheduleSave();
    }

    private void ApplyCompactLayout(bool compact)
    {
        MinWidth = compact ? 360 : 800;
        MinHeight = compact ? 410 : 550;
        Width = compact ? _compactWidth : _fullWidth;
        Height = compact ? _compactHeight : _fullHeight;
        RootGrid.Margin = compact ? new Thickness(16, 15, 16, 15) : new Thickness(24, 18, 24, 12);
        FullContent.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactContent.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        HeaderWidgetButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MinimizeButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MaximizeButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandTitle.FontSize = compact ? 19 : 24;
        BrandTitle.Text = compact ? "CM AnrufMonitor" : "CM AnrufMonitor";
        BrandMark.Width = compact ? 34 : 45;
        BrandMark.Height = compact ? 34 : 45;
        BrandMark.Margin = new Thickness(0, 0, compact ? 10 : 13, 0);
    }

    private void EnsureVisible()
    {
        Rect desktop = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
        if (Left + 80 < desktop.Left || Left > desktop.Right - 80 || Top + 40 < desktop.Top || Top > desktop.Bottom - 40)
        {
            Rect primary = SystemParameters.WorkArea;
            Left = primary.Left + Math.Max(0, (primary.Width - Width) / 2);
            Top = primary.Top + Math.Max(0, (primary.Height - Height) / 2);
        }
    }

    private void Window_BoundsChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        // Preserve usable history space on small screens without changing the normal view or widget.
        HeroCard.Visibility = !_settings.CompactMode && ActualHeight is > 0 and < 660 ? Visibility.Collapsed : Visibility.Visible;
        if (_updatingLayout || WindowState != WindowState.Normal) return;
        CaptureWindow();
        ScheduleSave();
    }

    private void CaptureWindow()
    {
        if (!_ready) return;
        if (WindowState == WindowState.Normal && double.IsFinite(Left) && double.IsFinite(Top))
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
            _settings.WindowHeight = Math.Max(MinHeight, ActualHeight > 0 ? ActualHeight : Height);
        }
        _settingsDirty = true;
    }

    private void ScheduleSave()
    {
        if (_savedOnExit) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool SavePending()
    {
        bool success = true;
        void Attempt(string area, Action save, Action complete)
        {
            if (_writeBlocked.Contains(area)) { success = false; return; }
            try { save(); complete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
            {
                success = false;
                ShowWarning("Die Daten konnten nicht gespeichert werden (" + area + "): " + error.Message + " Die Änderungen bleiben in dieser Sitzung sichtbar.");
            }
        }
        if (_settingsDirty) Attempt("settings", () => _store.SaveSettings(_settings), () => _settingsDirty = false);
        if (_historyDirty) Attempt("history", () => _store.SaveHistory(_tracker.Snapshot()), () => _historyDirty = false);
        if (_contactsDirty) Attempt("contacts", () => _store.SaveContacts(_contacts), () => _contactsDirty = false);
        return success;
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningBanner.Visibility = Visibility.Visible;
    }
    private void DismissWarning_Click(object sender, RoutedEventArgs e) => WarningBanner.Visibility = Visibility.Collapsed;
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = _dataDirectory, UseShellExecute = true }); }
        catch (Exception error) { ShowWarning("Der Datenordner konnte nicht geöffnet werden: " + error.Message); }
    }

    private bool RefreshWindowChrome()
    {
        try
        {
            int preference = 2;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref preference, sizeof(int));
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static Brush BrushOf(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(color)!;
        brush.Freeze();
        return brush;
    }
    internal static Brush StateColor(CallState state) => BrushOf(state switch
    {
        CallState.Missed => "#FFB3B8",
        CallState.Rejected => "#FFB3B8",
        CallState.Interrupted => "#FFCD9E",
        CallState.Ringing => "#FFE23A",
        CallState.Dialing => "#B8DBFF",
        CallState.Connected => "#A4F0CE",
        CallState.Answered => "#B7F0D4",
        _ => "#D5E7FB"
    });
    internal static string StateLabel(CallState state) => state switch
    {
        CallState.Ringing => "Klingelt",
        CallState.Dialing => "Wählt",
        CallState.Connected => "Im Gespräch",
        CallState.Answered => "Angenommen",
        CallState.Missed => "Verpasst",
        CallState.NotAnswered => "Nicht erreicht",
        CallState.Rejected => "Abgewiesen",
        CallState.Interrupted => "Unvollständig",
        _ => "Status unbekannt"
    };
    internal static string DirectionLabel(CallDirection direction) => direction switch { CallDirection.Incoming => "Eingehend", CallDirection.Outgoing => "Ausgehend", _ => "Richtung unbekannt" };
    internal static string FormatDuration(int seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return seconds >= 3600 ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + time.ToString(@"mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}

public sealed class CallRow
{
    public CallRecord Call { get; }
    public string Name { get; }
    public string NameSource { get; }
    public string Number => string.IsNullOrWhiteSpace(Call.RemoteNumber) ? "Unbekannt" : Call.RemoteNumber;
    public string LocalNumber => string.IsNullOrWhiteSpace(Call.LocalNumber) ? "Unbekannt" : Call.LocalNumber;
    public string StateText => (Call.Direction == CallDirection.Incoming ? "↙ " : Call.Direction == CallDirection.Outgoing ? "↗ " : "· ") + MainWindow.StateLabel(Call.State);
    public Brush StateBrush => MainWindow.StateColor(Call.State);
    public string TimeText => Call.StartedAt.ToString("dd.MM.yy  HH:mm", CultureInfo.CurrentCulture);
    public string Duration => Call.State is CallState.Ringing or CallState.Dialing ? "" : Call.State == CallState.Connected ? "läuft" : Call.ConnectedAt.HasValue || Call.DurationSeconds > 0 ? (Call.DurationIsApproximate ? "≈ " : "") + MainWindow.FormatDuration(Call.DurationSeconds) : "";
    public CallRow(CallRecord call, string name, string nameSource = "") { Call = call; Name = name; NameSource = nameSource; }
}
