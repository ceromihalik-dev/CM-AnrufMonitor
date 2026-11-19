using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CM.AnrufMonitor.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _smokeMode;
    private string? _smokeDirectory;
    private readonly SmokeBindingListener _smokeBindings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _smokeMode = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        if (!_smokeMode)
        {
            _instanceMutex = new Mutex(true, "Local\\CM.AnrufMonitor.Desktop.0_1", out bool firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("CM AnrufMonitor läuft bereits. Öffne das Programm über sein Symbol im Infobereich neben der Windows-Uhr (gegebenenfalls unter dem Pfeil ↑).", "CM AnrufMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        else
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _smokeDirectory = Path.Combine(Path.GetTempPath(), "CM-AnrufMonitor-Smoke-" + Guid.NewGuid().ToString("N"));
            PresentationTraceSources.DataBindingSource.Listeners.Add(_smokeBindings);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (_smokeMode) { Console.Error.WriteLine("SMOKE FAILED: " + args.Exception.GetType().Name + ": " + args.Exception.Message); Shutdown(1); }
            else MessageBox.Show("Die Aktion konnte nicht abgeschlossen werden.\n\n" + args.Exception.Message, "CM AnrufMonitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        SessionEnding += (_, _) => (MainWindow as MainWindow)?.AllowSessionExit();
        try
        {
            MainWindow = new MainWindow(_smokeDirectory, _smokeMode);
            MainWindow.Show();
            if (_smokeMode)
            {
                int outputIndex = Array.FindIndex(e.Args, item => item.Equals("--smoke-output", StringComparison.OrdinalIgnoreCase));
                string? outputDirectory = outputIndex >= 0 && outputIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[outputIndex + 1]) : null;
                _ = RunSmokeAsync((MainWindow)MainWindow, outputDirectory);
            }
        }
        catch (Exception error)
        {
            if (_smokeMode) Console.Error.WriteLine("SMOKE FAILED: " + error.GetType().Name + ": " + error.Message);
            else MessageBox.Show("CM AnrufMonitor konnte nicht gestartet werden.\n\n" + error.Message, "CM AnrufMonitor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunSmokeAsync(MainWindow window, string? outputDirectory)
    {
        try
        {
            await Task.Delay(700);
            var checks = await window.RunSmokeChecksAsync(outputDirectory);
            if (_smokeBindings.HasErrors) throw new InvalidOperationException("WPF reported a binding error during the smoke test.");
            if (outputDirectory is not null)
                File.WriteAllText(Path.Combine(outputDirectory, "smoke-result.json"), JsonSerializer.Serialize(new { passed = true, version = "0.2.0", checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("SMOKE PASS: WPF loaded, gradient and layouts rendered, demo isolated, DPAPI round trip verified.");
            window.AllowSessionExit();
            window.Close();
            Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("SMOKE FAILED: " + error.GetType().Name + ": " + error.Message);
            window.AllowSessionExit();
            window.Close();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        if (_smokeMode)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_smokeBindings);
            if (_smokeDirectory is not null)
            {
                try { Directory.Delete(_smokeDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        base.OnExit(e);
    }
}

internal sealed class SmokeBindingListener : TraceListener
{
    private readonly StringBuilder _messages = new();
    public bool HasErrors => _messages.ToString().Contains("Error", StringComparison.OrdinalIgnoreCase);
    public override void Write(string? message) { if (message is not null) _messages.Append(message); }
    public override void WriteLine(string? message) { if (message is not null) _messages.AppendLine(message); }
}
