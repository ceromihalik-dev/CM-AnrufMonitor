using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CM.AnrufMonitor.Core;

namespace CM.AnrufMonitor.App;

public partial class MainWindow
{
    private readonly ObservableCollection<LookupProblemRow> _lookupProblems = new();
    private readonly HashSet<string> _lookupProblemNumbers = new(StringComparer.Ordinal);

    private void ResetNameLookupProblems()
    {
        NameLookupProblems.IsExpanded = false;
        _lookupProblems.Clear();
        _lookupProblemNumbers.Clear();
        UpdateLookupProblemHeader();
    }

    private void AddLookupProblem(ReverseLookupResult result)
    {
        if (result.Status is ReverseLookupStatus.Found or ReverseLookupStatus.NotFound
            || !_lookupProblemNumbers.Add(result.NormalizedNumber)) return;
        _lookupProblems.Add(new LookupProblemRow(result.NormalizedNumber, result.Status,
            LookupProblemLabel(result.Status), LookupProblemReason(result)));
        UpdateLookupProblemHeader();
    }

    private void SetLookupProblems(IEnumerable<ReverseLookupResult> results)
    {
        string? selectedNumber = (LookupProblemsGrid.SelectedItem as LookupProblemRow)?.Number;
        _lookupProblems.Clear();
        _lookupProblemNumbers.Clear();
        foreach (ReverseLookupResult result in results) AddLookupProblem(result);
        UpdateLookupProblemHeader();
        if (selectedNumber is not null)
            LookupProblemsGrid.SelectedItem = _lookupProblems.FirstOrDefault(row => row.Number == selectedNumber);
    }

    private void UpdateLookupProblemHeader()
    {
        NameLookupProblems.Header = "Probleme und übersprungene Nummern (" + _lookupProblems.Count + ")";
        NameLookupProblems.Visibility = _lookupProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_lookupProblems.Count == 0) NameLookupProblems.IsExpanded = false;
    }

    private static string LookupProblemLabel(ReverseLookupStatus status) => status switch
    {
        ReverseLookupStatus.UnrecognizedResponse => "Nicht auswertbar",
        ReverseLookupStatus.Ambiguous => "Mehrdeutiger Treffer",
        ReverseLookupStatus.TemporaryError => "Vorübergehender Fehler",
        ReverseLookupStatus.Blocked => "Abruf angehalten",
        ReverseLookupStatus.RateLimited => "Abfragelimit erreicht",
        ReverseLookupStatus.AuthRequired => "Anmeldung erforderlich",
        ReverseLookupStatus.Unsupported => "Nicht unterstützt",
        _ => "Nicht geklärt"
    };

    private static string LookupProblemReason(ReverseLookupResult result)
    {
        // Core supplies bounded explanations, never page HTML. Render only plain text here.
        string message = new(result.Message.Select(character => char.IsControl(character) ? ' ' : character).Take(500).ToArray());
        if (!string.IsNullOrWhiteSpace(message)) return message.Trim();
        return result.Status switch
        {
            ReverseLookupStatus.UnrecognizedResponse => "Die Anbieterantwort war nicht auswertbar. Ein fehlender Treffer ist damit nicht bestätigt.",
            ReverseLookupStatus.Ambiguous => "Mehrere Einträge passen zur Rufnummer. Es wurde kein Name automatisch übernommen.",
            ReverseLookupStatus.TemporaryError => "Die Abfrage ist vorübergehend fehlgeschlagen. Ein vorhandener Name bleibt erhalten.",
            ReverseLookupStatus.Blocked => "Der Abruf konnte nicht sicher fortgesetzt werden. Der Suchlauf wurde angehalten.",
            ReverseLookupStatus.RateLimited => "Der Anbieter lässt derzeit keine weiteren Abfragen zu. Der Suchlauf wurde angehalten.",
            ReverseLookupStatus.AuthRequired => "Die Antwort erfordert eine Anmeldung. Der Suchlauf wurde angehalten.",
            ReverseLookupStatus.Unsupported => "Diese Rufnummer wird von Das Örtliche nicht unterstützt. Es wurde keine Internetabfrage gesendet.",
            _ => "Für diese Rufnummer liegt kein eindeutiges Ergebnis vor."
        };
    }

    private void LookupProblem_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LookupProblemBrowserButton is null || LookupProblemContactButton is null) return;
        string? number = (LookupProblemsGrid.SelectedItem as LookupProblemRow)?.Number;
        LookupProblemBrowserButton.IsEnabled = ExternalLookupNumber(number) is not null;
        LookupProblemContactButton.IsEnabled = !string.IsNullOrWhiteSpace(number);
    }

    private void LookupProblemBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (LookupProblemsGrid.SelectedItem is LookupProblemRow row) OpenReverseLookup(row.Number);
    }

    private void LookupProblemContact_Click(object sender, RoutedEventArgs e)
    {
        if (LookupProblemsGrid.SelectedItem is LookupProblemRow row) PrepareContactFromNumber(row.Number);
    }

    private void LookupHistoryArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LookupProblemContent is null) return;
        // The details use a bounded part of the existing history area and scroll independently.
        LookupProblemContent.Height = Math.Clamp((e.NewSize.Height - 32) / 2, 80, 190);
    }

    private void NameLookupProblems_ExpandedChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateHistorySpace();
    }

    private void UpdateHistorySpace()
    {
        bool detailsOpen = NameLookupProblems.IsExpanded && NameLookupProblems.Visibility == Visibility.Visible;
        SummaryMetrics.Visibility = !_settings.CompactMode && detailsOpen && ActualHeight is > 0 and < 660
            ? Visibility.Collapsed : Visibility.Visible;
        HeroCard.Visibility = !_settings.CompactMode && (ActualHeight is > 0 and < 660 || detailsOpen)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed record LookupProblemRow(string Number, ReverseLookupStatus Status, string StatusText, string Reason);
}
