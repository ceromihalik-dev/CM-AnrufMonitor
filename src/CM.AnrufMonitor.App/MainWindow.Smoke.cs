using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CM.AnrufMonitor.Core;

namespace CM.AnrufMonitor.App;

public partial class MainWindow
{
    internal async Task<IReadOnlyList<string>> RunSmokeChecksAsync(string? outputDirectory)
    {
        if (!_smokeMode) throw new InvalidOperationException("Smoke checks require an isolated smoke-test instance.");
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) throw new InvalidOperationException("Window did not load.");
        if (Background is not LinearGradientBrush background || background.GradientStops.Count < 2
            || background.GradientStops[0].Color != Color.FromRgb(0x27, 0x73, 0xCB)
            || background.GradientStops[1].Color != Color.FromRgb(0x10, 0x42, 0x81))
            throw new InvalidOperationException("The requested blue background was not applied.");
        if (_settings.AutoConnect || _settings.FritzSyncEnabled || _monitorTask is not null || _routerSyncTimer.IsEnabled)
            throw new InvalidOperationException("Smoke-test instance attempted to enable external networking.");

        var checks = new List<string> { "Window loaded", "Blue gradient applied", "Network disabled" };
        const string synthetic = "CM-SMOKE-ONLY-FAKE-CREDENTIAL-2026";
        _credentialVault.Save("smoke.invalid", 49000, "test-user", synthetic);
        if (_credentialVault.Load("smoke.invalid", 49000, "test-user") != synthetic)
            throw new InvalidOperationException("DPAPI round trip failed.");
        bool wrongScopeRejected = false;
        try { _credentialVault.Load("different.invalid", 49000, "test-user"); }
        catch (InvalidDataException) { wrongScopeRejected = true; }
        if (!wrongScopeRejected) throw new InvalidOperationException("Credential scope was not enforced.");
        if (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dataDirectory, "Credentials.bin"))).Contains(synthetic, StringComparison.Ordinal))
            throw new InvalidOperationException("Credential bytes contained plaintext.");
        _credentialVault.Delete();
        checks.Add("DPAPI round trip, encrypted bytes, and scope rejection");

        _demoStarted = DateTime.Now;
        MainTabs.SelectedIndex = 0;
        UpdateHero();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        foreach (TabItem tab in MainTabs.Items)
        {
            if (tab.Template.FindName("HeaderContentPresenter", tab) is not ContentPresenter header)
                throw new InvalidOperationException("A tab header was not rendered.");
            var text = new FormattedText(tab.Header?.ToString() ?? "", CultureInfo.CurrentUICulture, tab.FlowDirection,
                new Typeface(tab.FontFamily, tab.FontStyle, tab.FontWeight, tab.FontStretch), tab.FontSize,
                tab.Foreground, VisualTreeHelper.GetDpi(tab).PixelsPerDip);
            if (header.ActualWidth + 1 < text.WidthIncludingTrailingWhitespace)
                throw new InvalidOperationException("Tab header is clipped: " + tab.Header);
        }
        checks.Add("All tab headers fit their rendered content");
        if (!HeroName.Text.Contains("Demo", StringComparison.Ordinal) || _tracker.Snapshot().Count != 0)
            throw new InvalidOperationException("Demo leaked into stored history or was not labeled.");
        checks.Add("Clearly labeled demo isolated from history");

        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-full-demo.png"));
        }
        double originalHeight = Height;
        await RunNameLookupSmokeAsync(checks);
        await RunNameLookupRecoverySmokeAsync(checks);
        Height = 600;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (HeroCard.Visibility != Visibility.Collapsed || HistoryGrid.ActualHeight < 75)
            throw new InvalidOperationException("Name lookup controls leave no usable history area at small window height.");
        checks.Add("History remains usable at small window height");
        NameLookupProblems.IsExpanded = true;
        LookupProblemsGrid.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (SummaryMetrics.Visibility != Visibility.Collapsed || HeroCard.Visibility != Visibility.Collapsed
            || HistoryGrid.ActualHeight < 80 || LookupProblemsGrid.ActualHeight < 60
            || !LookupProblemBrowserButton.IsEnabled || !LookupProblemContactButton.IsEnabled)
            throw new InvalidOperationException("Expanded problem details hide history rows or their actions at 600-pixel window height.");
        checks.Add("Expanded problem details, explicit actions, and history rows fit at 600-pixel window height");
        if (HistoryGrid.SelectedItem is not null) HistoryGrid.ScrollIntoView(HistoryGrid.SelectedItem);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (outputDirectory is not null)
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-lookup-recovery-600-demo.png"));
        NameLookupProblems.IsExpanded = false;
        Height = Math.Max(Height, 820);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (outputDirectory is not null)
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-history-demo.png"));
        NameLookupProblems.IsExpanded = true;
        LookupProblemsGrid.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (HeroCard.Visibility != Visibility.Collapsed || HistoryGrid.ActualHeight < 80 || LookupProblemsGrid.ActualHeight < 60)
            throw new InvalidOperationException("Expanded lookup problems hide the history or their explanation.");
        if (!LookupProblemBrowserButton.IsEnabled || !LookupProblemContactButton.IsEnabled)
            throw new InvalidOperationException("The problem selection does not offer explicit browser and manual-contact actions.");
        if (HistoryGrid.SelectedItem is not CallRow selectedRecoveryRow || selectedRecoveryRow.Name != "DEMO · Nach dem Fehler gefunden")
            throw new InvalidOperationException("The recovery screenshot does not select the later successful call.");
        HistoryGrid.ScrollIntoView(HistoryGrid.SelectedItem);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        checks.Add("Bounded problem details and later successful history row remain visible together");
        if (outputDirectory is not null)
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-lookup-recovery-demo.png"));
        NameLookupProblems.IsExpanded = false;
        Height = Math.Max(Height, 1000);
        MainTabs.SelectedIndex = 2;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        FritzSyncCard.BringIntoView();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (!FritzSyncCard.IsVisible || FritzUsernameBox.ActualHeight < 25 || FritzPasswordBox.ActualHeight < 25)
            throw new InvalidOperationException("FRITZ!Box setup controls did not render.");
        checks.Add("FRITZ!Box setup form rendered");
        if (outputDirectory is not null)
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-setup-demo.png"));
        MainTabs.SelectedIndex = 0;
        Height = originalHeight;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        SetCompact(true);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        if (CompactContent.Visibility != Visibility.Visible || FullContent.Visibility != Visibility.Collapsed || ActualWidth < 350)
            throw new InvalidOperationException("Compact mode did not render.");
        checks.Add("Compact mode rendered");
        if (outputDirectory is not null)
        {
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-widget-demo.png"));
        }
        return checks;
    }

    private async Task RunNameLookupSmokeAsync(List<string> checks)
    {
        if (!_smokeMode || _reverseStore is null) throw new InvalidOperationException("Internet-name smoke checks require isolated storage.");
        DateTime local = DateTime.Now;
        const string repeatedNumber = "+493012345678";
        const string manualNumber = "+493098760000";
        const string internetName = "DEMO · Sanitärbetrieb Nord";
        const string ownName = "DEMO · Eigener Kontakt";
        DateTimeOffset utc = DateTimeOffset.UtcNow;
        _reverseStore.Save(new[]
        {
            new ReverseLookupResult
            {
                Status = ReverseLookupStatus.Found, NormalizedNumber = manualNumber, Name = "DEMO · Nachrangiger Internetname",
                Provider = InternetNameProvider, CheckedAtUtc = utc, ExpiresAtUtc = utc.AddDays(30)
            }
        });
        ReloadInternetNames();
        _contacts.Add(new Contact(ownName, manualNumber));
        _contactsDirty = true;
        var syntheticCalls = new[]
        {
            new CallRecord { Id = "smoke-history-a", StartedAt = local.AddMinutes(-3), ConnectedAt = local.AddMinutes(-3).AddSeconds(5), EndedAt = local.AddMinutes(-2), RemoteNumber = "030 12345678", LocalNumber = "030 99990000", Direction = CallDirection.Incoming, State = CallState.Answered, DurationSeconds = 55 },
            new CallRecord { Id = "smoke-history-b", StartedAt = local.AddMinutes(-5), EndedAt = local.AddMinutes(-4), RemoteNumber = repeatedNumber, LocalNumber = "030 99990000", Direction = CallDirection.Incoming, State = CallState.Missed },
            new CallRecord { Id = "smoke-history-c", StartedAt = local.AddMinutes(-8), ConnectedAt = local.AddMinutes(-8).AddSeconds(10), EndedAt = local.AddMinutes(-6), RemoteNumber = manualNumber, LocalNumber = "030 99990000", Direction = CallDirection.Outgoing, State = CallState.Answered, DurationSeconds = 110 }
        };
        // These records exist only in the private smoke-test profile. The production profile is never opened here.
        _tracker.LoadHistory(syntheticCalls);
        _historyDirty = true;
        _demoStarted = null;
        _smokeHistoryDemo = true;
        BrandSubtitle.Text = "DEMO · Synthetische Anrufe, keine echten Telefonate";
        FooterStatus.Text = "DEMO · Namenssuche mit lokalem Testanbieter · Keine Internetverbindung";
        RefreshContacts();
        SearchBox.Text = "Eigener Kontakt";
        var fake = new SmokeNameProvider(repeatedNumber, internetName);
        await LookupAllHistoryAsync(fake);
        if (fake.Requested.Count != 1 || fake.Requested[0] != repeatedNumber)
            throw new InvalidOperationException("Whole-history lookup ignored hidden rows, repeated a number, or queried the named contact.");
        SearchBox.Clear();
        RefreshHistory();
        if (_rows.Count != 3 || _rows.Count(row => row.Name == internetName) != 2)
            throw new InvalidOperationException("The Internet name did not update both matching history rows.");
        if (_rows.Single(row => row.Call.Id == "smoke-history-c").Name != ownName)
            throw new InvalidOperationException("The cached Internet name overrode a manual contact.");
        if (!_rows.Where(row => row.Name == internetName).All(row => row.NameSource.StartsWith("Internet · Das Örtliche", StringComparison.Ordinal)))
            throw new InvalidOperationException("Internet name provenance was not displayed.");
        if (!new ReverseLookupStore(_dataDirectory).Load().Any(result => result.NormalizedNumber == repeatedNumber && result.Status == ReverseLookupStatus.Found && result.Name == internetName))
            throw new InvalidOperationException("The found name was not persisted in the isolated cache.");
        _internetNames.Clear();
        ReloadInternetNames();
        RefreshHistory();
        if (_rows.Count(row => row.Name == internetName) != 2 || _rows.Single(row => row.Call.Id == "smoke-history-c").Name != ownName)
            throw new InvalidOperationException("Reloading persisted names changed row matching or contact priority.");
        var future = new CallRecord { RemoteNumber = repeatedNumber };
        if (DisplayName(future) != internetName) throw new InvalidOperationException("A later call did not use the cached name.");
        SearchBox.Text = "Sanitärbetrieb";
        if (_rows.Count != 2) throw new InvalidOperationException("Searching by the found name did not match both rows.");
        SearchBox.Clear();
        _selectedCallId = "smoke-history-a";
        RefreshHistory();
        if (HeroName.Text != internetName || CompactName.Text != internetName)
            throw new InvalidOperationException("The main call card or widget did not use the cached name.");
        checks.Add("Explicit whole-history name lookup ignores view filter and deduplicates numbers");
        checks.Add("Both matching rows, later calls, name search, call card, and widget use cached name");
        checks.Add("Internet provenance, persistence, and manual-contact priority verified with a local fake provider");
    }

    private sealed class SmokeNameProvider(string expectedNumber, string name) : IReverseLookupProvider
    {
        public string Id => InternetNameProvider;
        public List<string> Requested { get; } = new();
        public bool SupportsNumber(string number) => number.StartsWith("+49", StringComparison.Ordinal);
        public Task<ReverseLookupResult> LookupAsync(string number, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.Add(number);
            if (number != expectedNumber) throw new InvalidOperationException("Unexpected synthetic lookup number.");
            return Task.FromResult(new ReverseLookupResult { Status = ReverseLookupStatus.Found, NormalizedNumber = number, Provider = Id, Name = name });
        }
    }

    private async Task RunNameLookupRecoverySmokeAsync(List<string> checks)
    {
        if (!_smokeMode || _reverseStore is null) throw new InvalidOperationException("Recovery checks require isolated smoke storage.");
        const string unclearNumber = "+493011119999";
        const string foundNumber = "+493022228888";
        const string noHitNumber = "+493033337777";
        const string oldName = "DEMO · Vorher gespeicherter Name";
        const string foundName = "DEMO · Nach dem Fehler gefunden";
        DateTime local = DateTime.Now;
        DateTimeOffset utc = DateTimeOffset.UtcNow;
        _reverseStore.Save(_reverseStore.Load().Concat(new[]
        {
            new ReverseLookupResult
            {
                Provider = InternetNameProvider, Status = ReverseLookupStatus.Found,
                NormalizedNumber = unclearNumber, Name = oldName,
                CheckedAtUtc = utc.AddDays(-40), ExpiresAtUtc = utc.AddDays(-10)
            }
        }));
        ReloadInternetNames();
        _tracker.LoadHistory(_tracker.Snapshot().Concat(new[]
        {
            new CallRecord { Id = "smoke-recovery-unclear", StartedAt = local.AddSeconds(-10), EndedAt = local.AddSeconds(-5), RemoteNumber = unclearNumber, LocalNumber = "030 99990000", Direction = CallDirection.Incoming, State = CallState.Missed },
            new CallRecord { Id = "smoke-recovery-found-a", StartedAt = local.AddSeconds(-20), EndedAt = local.AddSeconds(-15), RemoteNumber = foundNumber, LocalNumber = "030 99990000", Direction = CallDirection.Incoming, State = CallState.Missed },
            new CallRecord { Id = "smoke-recovery-nohit", StartedAt = local.AddSeconds(-30), EndedAt = local.AddSeconds(-25), RemoteNumber = noHitNumber, LocalNumber = "030 99990000", Direction = CallDirection.Outgoing, State = CallState.NotAnswered },
            new CallRecord { Id = "smoke-recovery-found-b", StartedAt = local.AddSeconds(-40), EndedAt = local.AddSeconds(-35), RemoteNumber = "030 22228888", LocalNumber = "030 99990000", Direction = CallDirection.Incoming, State = CallState.Missed }
        }).ToArray());
        _historyDirty = true;
        SearchBox.Text = "Eigener Kontakt";
        var fake = new SmokeRecoveryNameProvider(unclearNumber, foundNumber, noHitNumber, foundName);
        await LookupAllHistoryAsync(fake);
        if (!fake.Requested.SequenceEqual(new[] { unclearNumber, foundNumber, noHitNumber }))
            throw new InvalidOperationException("The full-history run stopped after an unclear response or repeated an eligible number.");
        if (!NameLookupStatus.Text.StartsWith("Abgeschlossen mit Hinweisen.", StringComparison.Ordinal)
            || !NameLookupStatus.Text.Contains("1 Antwort nicht auswertbar", StringComparison.Ordinal)
            || !NameLookupStatus.Text.Contains("1 bestätigter Nichttreffer", StringComparison.Ordinal)
            || !NameLookupStatus.Text.Contains("0 Nummern noch nicht bearbeitet", StringComparison.Ordinal)
            || NameLookupProgress.Value != NameLookupProgress.Maximum
            || _nameLookupRunning || CancelNameLookupButton.Visibility != Visibility.Collapsed || !LookupAllNamesButton.IsEnabled)
            throw new InvalidOperationException("Completed recovery lookup has unclear counts, remaining work, or busy state.");
        if (_lookupProblems.Count != 1 || _lookupProblems[0].Number != unclearNumber
            || _lookupProblems[0].Status != ReverseLookupStatus.UnrecognizedResponse
            || !_lookupProblems[0].Reason.Contains("Synthetische Anbieterantwort", StringComparison.Ordinal))
            throw new InvalidOperationException("The unclear number was omitted from details or mislabeled as a confirmed no-hit.");
        _internetNames.Clear();
        ReloadInternetNames();
        SearchBox.Clear();
        _selectedCallId = "smoke-recovery-found-a";
        RefreshHistory();
        if (_rows.Count(row => row.Name == foundName) != 2
            || _rows.Single(row => row.Call.Id == "smoke-recovery-unclear").Name != oldName
            || !_reverseStore.Load().Any(result => result.NormalizedNumber == noHitNumber && result.Status == ReverseLookupStatus.NotFound))
            throw new InvalidOperationException("The later found name, retained older name, or confirmed no-hit did not survive reload.");
        checks.Add("Unclear response is skipped and later full-history candidates still receive names");
        checks.Add("Completed-with-notes state separates one unclear response from a confirmed no-hit and reports zero remaining");
        checks.Add("Problem number and safe reason are visible; older cached name survives unclear response and reload");
    }

    private sealed class SmokeRecoveryNameProvider(string unclearNumber, string foundNumber, string noHitNumber, string foundName) : IReverseLookupProvider
    {
        public string Id => InternetNameProvider;
        public List<string> Requested { get; } = new();
        public bool SupportsNumber(string number) => number.StartsWith("+49", StringComparison.Ordinal);
        public Task<ReverseLookupResult> LookupAsync(string number, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.Add(number);
            ReverseLookupResult result = number == unclearNumber
                ? new() { Status = ReverseLookupStatus.UnrecognizedResponse, Message = "Synthetische Anbieterantwort konnte nicht ausgewertet werden. Kein bestätigter Nichttreffer." }
                : number == foundNumber ? new() { Status = ReverseLookupStatus.Found, Name = foundName }
                : number == noHitNumber ? new() { Status = ReverseLookupStatus.NotFound }
                : throw new InvalidOperationException("Unexpected synthetic recovery number.");
            return Task.FromResult(result with { NormalizedNumber = number, Provider = Id });
        }
    }

    private void CaptureSmokeImage(string path)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(ActualWidth)), Math.Max(1, (int)Math.Ceiling(ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
