using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CM.AnrufMonitor.Core;

namespace CM.AnrufMonitor.App;

public partial class MainWindow
{
    private const string InternetNameProvider = "dasoertliche";
    private readonly Dictionary<string, ReverseLookupResult> _internetNames = new(StringComparer.Ordinal);
    private ReverseLookupStore? _reverseStore;
    private CancellationTokenSource? _nameLookupCancellation;
    private Task? _nameLookupTask;
    private bool _nameLookupRunning;
    private bool _nameLookupContextChanged;
    private bool _waitingForLookupExit;
    private bool _reverseCacheReady;
    private bool _smokeHistoryDemo;
    private int _nameLookupGeneration;
    private DateTimeOffset? _nextInternetReviewTime;

    private void InitializeNameLookup()
    {
        LookupProblemsGrid.ItemsSource = _lookupProblems;
        try
        {
            _reverseStore = new ReverseLookupStore(_dataDirectory);
            _reverseStore.Warning += message =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() => { if (!_savedOnExit) ShowWarning(message); });
            };
            ReloadInternetNames();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _reverseCacheReady = false;
            ShowWarning("Gespeicherte Internetnamen sind gerade nicht verfügbar: " + error.Message + " Anrufanzeige und eigene Kontakte bleiben verfügbar.");
        }
        SetNameLookupBusy(false);
    }

    private void ReloadInternetNames()
    {
        if (_reverseStore is null) return;
        IReadOnlyList<ReverseLookupResult> loaded = _reverseStore.Load();
        _internetNames.Clear();
        foreach (ReverseLookupResult result in loaded.Where(item => item.Provider == InternetNameProvider).OrderBy(item => item.CheckedAtUtc))
            _internetNames[result.NormalizedNumber] = result;
        _reverseCacheReady = true;
        UpdateInternetCacheInfo();
    }

    private ReverseLookupResult? InternetName(CallRecord call)
    {
        string number = ReverseLookupNumbers.Normalize(call.RemoteNumber, _settings.CountryCode, _settings.AreaCode);
        return number.Length > 0 && _internetNames.TryGetValue(number, out ReverseLookupResult? result)
            && result.Status == ReverseLookupStatus.Found && !string.IsNullOrWhiteSpace(result.Name) ? result : null;
    }

    private string NameSource(CallRecord call)
    {
        if (call.IsDemo) return "Simulation · keine echten Anrufdaten";
        string key = ContactBook.NormalizeNumber(call.RemoteNumber, _settings.CountryCode, _settings.AreaCode);
        if (_contactLookup.ContainsKey(key)) return "Eigener Kontakt";
        if (_verifiedRouterIdentity.Length > 0 && call.RouterIdentity == _verifiedRouterIdentity && _routerContactLookup.ContainsKey(key)) return "FRITZ!Box-Telefonbuch";
        if (!string.IsNullOrWhiteSpace(call.RouterName)) return "FRITZ!Box-Anrufliste";
        ReverseLookupResult? result = InternetName(call);
        if (result is null) return "Kein zugeordneter Kontaktname";
        return "Internet · Das Örtliche · geprüft " + result.CheckedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            + (result.ExpiresAtUtc <= DateTimeOffset.UtcNow ? " · älterer Stand, Aktualisierung beim nächsten Suchlauf" : "");
    }

    private void UpdateInternetCacheInfo()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReverseLookupResult[] found = _internetNames.Values.Where(item => item.Status == ReverseLookupStatus.Found).ToArray();
        int stale = found.Count(item => item.ExpiresAtUtc <= now);
        InternetCacheInfo.Text = found.Length + " Internetnamen gespeichert" + (stale > 0 ? ", davon " + stale + " mit älterem Prüfstand" : "")
            + ". Die Suche startet nur per Klick. Treffer gelten für alle passenden Anrufe; eigene und FRITZ!Box-Namen haben Vorrang.";
        _nextInternetReviewTime = found.Where(item => item.ExpiresAtUtc > now).Select(item => (DateTimeOffset?)item.ExpiresAtUtc).Min();
    }

    private async void LookupAllNames_Click(object sender, RoutedEventArgs e) => await LookupAllHistoryAsync();

    private Task LookupAllHistoryAsync(IReverseLookupProvider? testProvider = null)
    {
        // Deliberately do not use _rows: search/filter state must never narrow the user's whole-history request.
        IReadOnlyList<CallRecord> snapshot = _tracker.Snapshot().ToArray();
        return BeginNameLookupAsync(snapshot, "Gesamter Verlauf", testProvider);
    }

    private async void LookupSelectedName_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is CallRow row)
            await BeginNameLookupAsync(new[] { row.Call }, "Ausgewählte Rufnummer (erneute Online-Prüfung)", refreshCached: true);
    }

    private async Task BeginNameLookupAsync(IReadOnlyList<CallRecord> snapshot, string scope, IReverseLookupProvider? testProvider = null, bool refreshCached = false)
    {
        if (_nameLookupRunning || _savedOnExit) return;
        if (_smokeMode && testProvider is null) return;
        if (!_smokeMode && testProvider is not null) throw new InvalidOperationException("Testanbieter sind nur im isolierten Smoke-Test zulässig.");
        if (!_reverseCacheReady || _reverseStore is null)
        {
            ShowWarning("Der Speicher für Internetnamen konnte nicht geöffnet werden. Bitte die Zugriffsrechte im Datenordner prüfen und die App neu starten.");
            return;
        }
        _nameLookupCancellation?.Dispose();
        _nameLookupCancellation = new CancellationTokenSource();
        int generation = ++_nameLookupGeneration;
        _nameLookupContextChanged = false;
        ResetNameLookupProblems();
        SetNameLookupBusy(true);
        _nameLookupTask = RunNameLookupAsync(snapshot, scope, generation, _nameLookupCancellation.Token, testProvider, refreshCached);
        await _nameLookupTask;
    }

    private async Task RunNameLookupAsync(IReadOnlyList<CallRecord> snapshot, string scope, int generation,
        CancellationToken token, IReverseLookupProvider? testProvider, bool refreshCached)
    {
        IReverseLookupProvider? provider = null;
        try
        {
            provider = testProvider ?? new DasOertlicheReverseLookupProvider();
            var service = new ReverseLookupBatchService(provider, _reverseStore!);
            var options = new ReverseLookupBatchOptions
            {
                CountryCode = _settings.CountryCode, AreaCode = _settings.AreaCode,
                RequestInterval = _smokeMode ? TimeSpan.Zero : TimeSpan.FromSeconds(2), RefreshCached = refreshCached
            };
            ReverseLookupBatchPlan plan = service.CreatePlan(snapshot, call => PreferredName(call) is not null, options);
            NameLookupProgress.Maximum = Math.Max(1, plan.TotalCount);
            NameLookupProgress.Value = 0;
            ShowNameLookupStatus(scope + ": " + plan.SourceRecordCount + " Anrufe · " + plan.TotalCount + " eindeutige Rufnummern ohne Kontakt · "
                + plan.RequestCount + " Abfragen · " + plan.CachedCount + " gespeicherte Ergebnisse"
                + (plan.UnsupportedCount > 0 ? " · " + plan.UnsupportedCount + " nicht unterstützt" : ""));
            var progress = new Progress<ReverseLookupProgress>(update =>
            {
                if (generation != _nameLookupGeneration || !_nameLookupRunning || _savedOnExit) return;
                NameLookupProgress.Value = Math.Min(update.CompletedCount, NameLookupProgress.Maximum);
                ShowNameLookupStatus((update.IsStopped ? "Suche wird angehalten. " : "Suche läuft. ")
                    + LookupCounts(update.CompletedCount, update.TotalCount, update.FoundCount, update.NotFoundCount,
                        update.AmbiguousCount, update.UnrecognizedCount, update.ErrorCount, update.UnsupportedCount));
                ReverseLookupResult? result = update.Result;
                if (result is not null)
                {
                    AddLookupProblem(result);
                    if (result.Status is ReverseLookupStatus.Found or ReverseLookupStatus.NotFound)
                    {
                        _internetNames[result.NormalizedNumber] = result;
                        UpdateInternetCacheInfo();
                        RefreshHistory();
                    }
                    else if (result.Status == ReverseLookupStatus.Ambiguous && _internetNames.Remove(result.NormalizedNumber))
                    {
                        UpdateInternetCacheInfo();
                        RefreshHistory();
                    }
                    // Unclear or failed responses preserve the old name without rebuilding the rows.
                }
            });
            ReverseLookupBatchSummary summary = await service.RunAsync(plan, progress, token);
            if (_savedOnExit) return;
            // Results are keyed by full international number. Reload completed writes even after a
            // context change so a completed no-hit cannot leave an outdated name on screen.
            ReloadInternetNames();
            RefreshHistory();
            if (generation == _nameLookupGeneration || !_nameLookupContextChanged)
            {
                NameLookupProgress.Value = Math.Min(summary.CompletedCount, NameLookupProgress.Maximum);
                SetLookupProblems(summary.UnresolvedResults);
                ShowNameLookupStatus(LookupSummaryText(summary), error: summary.StopStatus.HasValue || summary.ErrorCount > 0);
                NameLookupStatus.ToolTip = summary.Message.Length > 0 ? summary.Message : null;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_savedOnExit && (generation == _nameLookupGeneration || !_nameLookupContextChanged)) ShowNameLookupStatus("Suche abgebrochen. Bereits gespeicherte Ergebnisse bleiben erhalten.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_savedOnExit)
            {
                try { ReloadInternetNames(); RefreshHistory(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (generation == _nameLookupGeneration || !_nameLookupContextChanged)
                    ShowNameLookupStatus("Die Namenssuche konnte nicht abgeschlossen werden: " + error.Message, error: true);
            }
        }
        finally
        {
            if (testProvider is null && provider is IDisposable disposable) disposable.Dispose();
            SetNameLookupBusy(false);
        }
    }

    private static string LookupCounts(int completed, int total, int found, int notFound, int ambiguous, int unrecognized, int errors, int unsupported)
        => completed + "/" + total + " Nummern bearbeitet · " + found + " gefunden · "
            + notFound + (notFound == 1 ? " bestätigter Nichttreffer" : " bestätigte Nichttreffer")
            + (ambiguous > 0 ? " · " + ambiguous + " mehrdeutig" : "")
            + (unrecognized > 0 ? " · " + unrecognized + (unrecognized == 1 ? " Antwort nicht auswertbar" : " Antworten nicht auswertbar") : "")
            + (errors > 0 ? " · " + errors + " Abfragefehler" : "") + (unsupported > 0 ? " · " + unsupported + " nicht unterstützt" : "");

    private static string LookupSummaryText(ReverseLookupBatchSummary summary)
    {
        if (summary.TotalCount == 0)
            return "Keine abfragbaren Rufnummern ohne Kontakt. Vorhandene Kontakte, eigene und interne Nummern werden nicht abgefragt."
                + (summary.UnsupportedCount > 0 ? " " + summary.UnsupportedCount + " Rufnummern werden vom Anbieter nicht unterstützt; siehe Problemliste." : "");
        string ending = summary.Cancelled ? "Abgebrochen." : summary.StopStatus switch
        {
            ReverseLookupStatus.UnrecognizedResponse => "Angehalten: Mehrere Anbieterantworten waren nicht auswertbar.",
            ReverseLookupStatus.Blocked => "Angehalten: Der Abruf konnte nicht sicher fortgesetzt werden.",
            ReverseLookupStatus.RateLimited => "Angehalten: Das Abfragelimit des Anbieters ist erreicht.",
            ReverseLookupStatus.AuthRequired => "Angehalten: Der Anbieter verlangt eine Anmeldung.",
            null when summary.UnresolvedResults.Count > 0 => "Abgeschlossen mit Hinweisen.",
            null => "Abgeschlossen.",
            _ => "Die Suche wurde angehalten."
        };
        int remaining = summary.RemainingNumbers.Count;
        string text = ending + " " + LookupCounts(summary.CompletedCount, summary.TotalCount, summary.FoundCount,
            summary.NotFoundCount, summary.AmbiguousCount, summary.UnrecognizedCount, summary.ErrorCount, summary.UnsupportedCount)
            + ".\n" + remaining + (remaining == 1 ? " Nummer noch nicht bearbeitet" : " Nummern noch nicht bearbeitet")
            + " · " + summary.CachedCount + " Ergebnisse aus dem lokalen Speicher."
            + (summary.UnresolvedResults.Count > 0 ? " Unklare Antworten und Fehler stehen in der Problemliste." : "");
        if (summary.StopStatus.HasValue && !string.IsNullOrWhiteSpace(summary.Message)) text += "\n" + summary.Message;
        return text;
    }

    private void ShowNameLookupStatus(string text, bool error = false)
    {
        NameLookupStatus.Text = text;
        NameLookupStatus.ToolTip = null;
        NameLookupStatus.Foreground = BrushOf(error ? "#FFB3B8" : "#F5F9FF");
        NameLookupStatus.Visibility = Visibility.Visible;
    }

    private void SetNameLookupBusy(bool busy)
    {
        _nameLookupRunning = busy;
        LookupAllNamesButton.IsEnabled = !busy && _reverseCacheReady;
        CancelNameLookupButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelNameLookupButton.IsEnabled = busy;
        ClearLookupCacheButton.IsEnabled = !busy && _reverseCacheReady;
        NameLookupProgress.Visibility = busy || NameLookupStatus.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelNameLookup_Click(object sender, RoutedEventArgs e) => CancelNameLookup(contextChanged: false);

    private void CancelNameLookup(bool contextChanged)
    {
        if (!_nameLookupRunning) return;
        _nameLookupContextChanged |= contextChanged;
        ++_nameLookupGeneration;
        _nameLookupCancellation?.Cancel();
        CancelNameLookupButton.IsEnabled = false;
        ShowNameLookupStatus(contextChanged ? "Suche wegen geänderter Verbindung oder Rufnummerneinstellungen angehalten. Gespeicherte Treffer bleiben erhalten."
            : "Suche wird abgebrochen. Bereits gespeicherte Treffer bleiben erhalten.");
    }

    private bool DeferExitForNameLookup(CancelEventArgs e)
    {
        if (!_nameLookupRunning) return false;
        CancelNameLookup(contextChanged: false);
        if (_sessionEnding || (!_exitRequested && _tray is not null)) return false;
        e.Cancel = true;
        if (!_waitingForLookupExit)
        {
            _waitingForLookupExit = true;
            _ = FinishNameLookupAndCloseAsync();
        }
        return true;
    }

    private async Task FinishNameLookupAndCloseAsync()
    {
        try { if (_nameLookupTask is not null) await _nameLookupTask; }
        catch (Exception) { /* The lookup itself reports the failure; closing still preserves already saved results. */ }
        finally
        {
            _waitingForLookupExit = false;
            if (!_savedOnExit) RequestExit();
        }
    }

    private void ClearLookupCache_Click(object sender, RoutedEventArgs e)
    {
        if (_nameLookupRunning || !_reverseCacheReady || _reverseStore is null) return;
        if (MessageBox.Show(this, "Die lokal gespeicherten Internetnamen und Suchergebnisse löschen?\n\nEigene Kontakte, FRITZ!Box-Telefonbücher und der Anrufverlauf bleiben erhalten.",
            "Suchergebnisse löschen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            ++_nameLookupGeneration;
            _reverseStore.Save(Array.Empty<ReverseLookupResult>());
            ResetNameLookupProblems();
            ReloadInternetNames();
            RefreshHistory();
            NameLookupProgress.Value = 0;
            NameLookupProgress.Visibility = Visibility.Collapsed;
            ShowNameLookupStatus("Gespeicherte Internet-Suchergebnisse gelöscht.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { ShowWarning("Die Suchergebnisse konnten nicht gelöscht werden: " + error.Message); }
    }
}
