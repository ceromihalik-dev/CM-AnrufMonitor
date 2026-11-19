using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CM.AnrufMonitor.Core;

namespace CM.AnrufMonitor.App;

public partial class MainWindow
{
    private void Support_Click(object sender, RoutedEventArgs e) => OpenExternal(AppLinks.Support);

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Über CM AnrufMonitor", Icon = Icon,
            Width = 455, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)FindResource(typeof(Window)), ShowInTaskbar = false
        };
        var body = new StackPanel { Margin = new Thickness(25) };
        body.Children.Add(new TextBlock { Text = "CM AnrufMonitor", FontSize = 25, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Version " + AppVersion.Display + " · Ersteller: C. Mihalik", Margin = new Thickness(0, 9, 0, 19) });
        body.Children.Add(new TextBlock { Text = "Live-Anrufanzeige, Anruflistenabgleich und Kontaktnamen für deine FRITZ!Box. Eigenständige Windows-Anwendung; kein offizielles FRITZ!-Produkt.", TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        var links = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        var project = new Button { Content = "Projekt auf GitHub", Margin = new Thickness(0, 0, 8, 8) };
        project.Click += (_, _) => OpenExternal(AppLinks.Project);
        links.Children.Add(project);
        var support = new Button { Content = "Unterstützen", Margin = new Thickness(0, 0, 0, 8) };
        support.Click += (_, _) => OpenExternal(AppLinks.Support);
        links.Children.Add(support);
        body.Children.Add(links);
        body.Children.Add(new TextBlock { Text = "Die Links öffnen deinen Standardbrowser. Eine Unterstützung ist freiwillig.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 15) });
        var close = new Button { Content = "Schließen", HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, MinWidth = 90 };
        close.Click += (_, _) => dialog.Close();
        body.Children.Add(close);
        dialog.Content = body;
        dialog.ShowDialog();
    }

    private string? ExternalLookupNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('*') || raw.TrimStart().StartsWith('#')) return null;
        string normalized = ContactBook.NormalizeNumber(raw, _settings.CountryCode, _settings.AreaCode);
        if (!normalized.StartsWith("+49", StringComparison.Ordinal) || normalized.Length is < 6 or > 17) return null;
        return "0" + normalized[3..];
    }

    private void ReverseHero_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedCall is { IsDemo: false } call) OpenReverseLookup(call.RemoteNumber);
    }

    private void ReverseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is CallRow row) OpenReverseLookup(row.Call.RemoteNumber);
    }

    private void OpenReverseLookup(string number)
    {
        string? external = ExternalLookupNumber(number);
        if (external is null)
        {
            ShowWarning("Das Örtliche durchsucht deutsche Rufnummern. Dafür wird eine vollständige externe Rufnummer mit deutscher Vorwahl benötigt. Bei lokalen Nummern zuerst unter Einrichtung die Ortsvorwahl ergänzen.");
            return;
        }
        OpenExternal(AppLinks.ReverseLookupTemplate + Uri.EscapeDataString(external));
    }

    private void OpenExternal(string url)
    {
        if (_smokeMode) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception error) { ShowWarning("Der Browser konnte nicht geöffnet werden: " + error.Message); }
    }
}
