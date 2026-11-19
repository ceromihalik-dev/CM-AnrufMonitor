using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
        if (!HeroName.Text.Contains("Demo", StringComparison.Ordinal) || _tracker.Snapshot().Count != 0)
            throw new InvalidOperationException("Demo leaked into stored history or was not labeled.");
        checks.Add("Clearly labeled demo isolated from history");

        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            CaptureSmokeImage(Path.Combine(outputDirectory, "CM-AnrufMonitor-full-demo.png"));
        }
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
