using System.Net.Sockets;
using System.Text;

namespace CM.AnrufMonitor.Core;

/// <summary>A passive TCP client. No passwords, HTTP calls, router mutations or application heartbeat writes.</summary>
public sealed class FritzMonitorClient
{
    private int running;
    public event Action<string>? LineReceived;
    public event Action<MonitorConnectionState, string>? StateChanged;

    public async Task RunAsync(string host, int port, CancellationToken cancellationToken)
    {
        host = host?.Trim() ?? "";
        if (host.Length == 0 || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Bitte nur einen Hostnamen oder eine IP-Adresse eingeben, ohne http:// oder Port.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Dieser Anrufmonitor läuft bereits.");
        int retrySeconds = 2;
        bool firstAttempt = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Emit(firstAttempt ? MonitorConnectionState.Connecting : MonitorConnectionState.Reconnecting,
                    firstAttempt ? "Verbindung zur FRITZ!Box wird hergestellt …" : "Verbindung wird wiederhergestellt …");
                firstAttempt = false;
                using var client = new TcpClient();
                DateTime connectedAt = DateTime.MinValue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    try { await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new IOException("Die FRITZ!Box antwortet nicht. Adresse, Netzwerk und Anrufmonitor prüfen."); }
                    EnableKeepAlive(client.Client);
                    connectedAt = DateTime.UtcNow;
                    Emit(MonitorConnectionState.Connected, "Verbunden · wartet auf Anrufe");
                    await ReadLinesAsync(client.GetStream(), cancellationToken).ConfigureAwait(false);
                    throw new IOException("Die FRITZ!Box hat die Verbindung geschlossen.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is SocketException or IOException or InvalidDataException)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    // Inform the consumer before retrying so open calls are not later marked falsely missed.
                    Emit(MonitorConnectionState.Disconnected, DescribeFailure(error));
                    if (connectedAt != DateTime.MinValue && DateTime.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) retrySeconds = 2;
                    Emit(MonitorConnectionState.Reconnecting, $"Nicht verbunden · neuer Versuch in {retrySeconds} Sekunden");
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
                    retrySeconds = Math.Min(30, retrySeconds * 2);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref running, 0);
            Emit(MonitorConnectionState.Disconnected, "Verbindung beendet");
        }
    }

    private async Task ReadLinesAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        var pending = new StringBuilder();
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            int charCount = decoder.GetChars(buffer, 0, count, chars, 0, false);
            for (int i = 0; i < charCount; i++)
            {
                char c = chars[i];
                if (c == '\n')
                {
                    string line = pending.ToString().TrimEnd('\r');
                    pending.Clear();
                    if (line.Length > 0) LineReceived?.Invoke(line);
                }
                else
                {
                    pending.Append(c);
                    if (pending.Length > MonitorEvent.MaximumLineLength)
                        throw new InvalidDataException("Ungültige Daten vom Anrufmonitor (Zeile zu lang).");
                }
            }
        }
    }

    private void Emit(MonitorConnectionState state, string message) => StateChanged?.Invoke(state, message);
    private static string DescribeFailure(Exception error) => error switch
    {
        SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused =>
            "Anrufmonitor nicht erreichbar. #96*5* am angeschlossenen Telefon wählen und Adresse prüfen.",
        SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData =>
            "FRITZ!Box-Adresse nicht gefunden. Statt fritz.box die lokale IP-Adresse eintragen.",
        SocketException => "Keine Verbindung zur FRITZ!Box. Netzwerk, Adresse und Anrufmonitor prüfen.",
        _ => error.Message
    };

    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException) { /* Older Windows versions may not support all keepalive options. */ }
        catch (PlatformNotSupportedException) { }
    }
}
