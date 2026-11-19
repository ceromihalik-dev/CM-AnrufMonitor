using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CM.AnrufMonitor.Core;

// Real sockets on loopback only. No router, credentials, packages or public network access.
var cases = new (string Name, Func<Task> Run)[]
{
    ("Fragmented TCP frames reconstruct exactly one line", FragmentedLines),
    ("Coalesced lines preserve order and concurrent call state", MultipleLines),
    ("Idle connection remains connected and produces no calls", IdleConnection),
    ("Peer disconnect announces loss and reconnects", PeerDisconnect),
    ("Cancellation interrupts a pending read promptly", CancelPendingRead),
    ("Oversize input is bounded and cannot inject a valid suffix", OversizeLine)
};
int failures = 0;
foreach (var test in cases)
{
    var elapsed = Stopwatch.StartNew();
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(18));
        Console.WriteLine($"PASS | {test.Name} | {elapsed.ElapsedMilliseconds} ms");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL | {test.Name} | {error.GetType().Name}: {error.Message}");
    }
}
Console.WriteLine($"NETWORK_CHECKS: {cases.Length - failures}/{cases.Length} passed; loopback only.");
return failures == 0 ? 0 : 1;

static async Task FragmentedLines()
{
    await using var test = await SocketFixture.Start();
    const string first = "19.11.26 14:30:00;RING;7;0300000000;0300000001;SIP0;";
    // Explicitly split content and CRLF across different sends.
    await test.Send(first[..9]);
    await Task.Delay(90);
    Require(test.Lines.IsEmpty, "A partial prefix became an event.");
    await test.Send(first[9..] + "\r");
    await Task.Delay(90);
    Require(test.Lines.IsEmpty, "A CR without LF prematurely completed an event.");
    await test.Send("\n");
    await Until(() => test.Lines.Count == 1, "Completed fragmented line was lost.");
    Require(test.Lines.Single() == first, "Line was corrupted or still contains its delimiter.");
    Require(test.Calls().Count == 1 && test.Calls()[0].State == CallState.Ringing,
        "A fragmented ring did not create exactly one ringing call.");
}

static async Task MultipleLines()
{
    await using var test = await SocketFixture.Start();
    string[] lines =
    [
        "19.11.26 14:30:00;RING;7;0300000000;0300000001;SIP0;",
        "19.11.26 14:30:01;CALL;8;12;0300000001;0300000002;SIP1;",
        "19.11.26 14:30:02;CONNECT;7;11;0300000000;",
        "19.11.26 14:30:02;DISCONNECT;7;0;",
        "19.11.26 14:30:04;DISCONNECT;8;0;"
    ];
    await test.Send(string.Join('\n', lines) + "\n");
    await Until(() => test.Lines.Count == lines.Length, "Coalesced event batch was not delivered completely.");
    Require(test.Lines.SequenceEqual(lines), "Coalesced event order/content changed.");
    var calls = test.Calls();
    Require(calls.Count == 2, "Concurrent calls were merged or phantom calls appeared.");
    Require(calls.Single(x => x.ConnectionId == "7").State == CallState.Answered,
        "A connected zero-second call was incorrectly marked missed.");
    Require(calls.Single(x => x.ConnectionId == "8").State == CallState.NotAnswered,
        "Unanswered outgoing call lost its direction or status.");
}

static async Task IdleConnection()
{
    await using var test = await SocketFixture.Start();
    int observedStates = test.States.Count;
    await Task.Delay(500);
    Require(!test.RunTask.IsCompleted, "The client stopped during an idle stream.");
    Require(test.States.Last() == MonitorConnectionState.Connected, "Idle stream was treated as disconnected.");
    Require(test.States.Count == observedStates, "Idle stream emitted unnecessary connection transitions.");
    Require(test.Lines.IsEmpty && test.Calls().Count == 0, "Idle stream produced phantom call data.");
}

static async Task PeerDisconnect()
{
    await using var test = await SocketFixture.Start();
    await test.Send("19.11.26 14:30:00;RING;7;0300000000;0300000001;SIP0;\n");
    await Until(() => test.Lines.Count == 1, "Initial ring missing.");
    test.Peer.Dispose();
    await Until(() => test.States.Last() != MonitorConnectionState.Connected,
        "Peer close was not reported.");
    Require(test.Calls().Single().State == CallState.Interrupted,
        "Connection loss should preserve the call as incomplete, not missed.");
    using var acceptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    test.Peer = await test.Listener.AcceptTcpClientAsync(acceptDeadline.Token);
    await Until(() => test.States.Count(x => x == MonitorConnectionState.Connected) == 2,
        "Reconnected peer was not reported as connected.");
    await test.Send("19.11.26 14:30:10;RING;7;0300000002;0300000001;SIP0;\n");
    await Until(() => test.Lines.Count == 2, "A line after reconnect was lost.");
    var calls = test.Calls();
    Require(calls.Count == 2 && calls.Count(x => x.State == CallState.Ringing) == 1,
        "Reused connection ID after reconnect damaged the previous call.");
    Require(!calls.Any(x => x.State == CallState.Missed), "Connection loss created a phantom missed call.");
}

static async Task CancelPendingRead()
{
    await using var test = await SocketFixture.Start();
    await Task.Delay(60);
    var elapsed = Stopwatch.StartNew();
    test.Lifetime.Cancel();
    await test.ExpectStop(TimeSpan.FromSeconds(2));
    Require(elapsed.Elapsed < TimeSpan.FromSeconds(2), "Cancellation exceeded two seconds.");
    Require(test.States.Last() == MonitorConnectionState.Disconnected,
        "Cancellation did not leave a disconnected state.");
    Require(test.Calls().Count == 0 && test.Lines.IsEmpty, "Cancellation created phantom calls.");
}

static async Task OversizeLine()
{
    await using var test = await SocketFixture.Start();
    string valid = "19.11.26 14:30:00;RING;7;0300000000;0300000001;SIP0;";
    // A line must never be truncated in a way that interprets its suffix as a fresh event.
    await test.Send(new string('X', MonitorEvent.MaximumLineLength + 100) + valid + "\n");
    await Until(() => test.States.Last() != MonitorConnectionState.Connected,
        "An overlong line did not close the unsafe stream.");
    Require(test.Lines.IsEmpty && test.Calls().Count == 0,
        "Overlong input or its suffix was delivered as a real call.");
    using var acceptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    test.Peer.Dispose();
    test.Peer = await test.Listener.AcceptTcpClientAsync(acceptDeadline.Token);
    await Until(() => test.States.Count(x => x == MonitorConnectionState.Connected) == 2,
        "Client did not recover after rejecting the overlong stream.");
    await test.Send(valid + "\n");
    await Until(() => test.Lines.Count == 1, "Fresh valid input after overlong stream was lost.");
    Require(test.Lines.Single() == valid && test.Calls().Count == 1,
        "Recovery produced corrupted or duplicate calls.");
}

static async Task Until(Func<bool> condition, string failure)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException(failure);
        await Task.Delay(10);
    }
}

static void Require(bool condition, string error)
{
    if (!condition) throw new InvalidOperationException(error);
}

sealed class SocketFixture : IAsyncDisposable
{
    private readonly CallTracker tracker = new();
    private readonly object gate = new();
    public TcpListener Listener { get; } = new(IPAddress.Loopback, 0);
    public TcpClient Peer { get; set; } = null!;
    public FritzMonitorClient Client { get; } = new();
    public CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromSeconds(14));
    public Task RunTask { get; private set; } = Task.CompletedTask;
    public ConcurrentQueue<string> Lines { get; } = new();
    public ConcurrentQueue<MonitorConnectionState> States { get; } = new();

    private SocketFixture()
    {
        Client.LineReceived += line =>
        {
            lock (gate) tracker.Apply(line);
            Lines.Enqueue(line);
        };
        Client.StateChanged += (state, _) =>
        {
            lock (gate)
                if (state is MonitorConnectionState.Disconnected or MonitorConnectionState.Reconnecting)
                    tracker.MarkInterrupted();
            States.Enqueue(state);
        };
    }

    public static async Task<SocketFixture> Start()
    {
        var fixture = new SocketFixture();
        try
        {
            fixture.Listener.Start();
            int port = ((IPEndPoint)fixture.Listener.LocalEndpoint).Port;
            fixture.RunTask = fixture.Client.RunAsync(IPAddress.Loopback.ToString(), port, fixture.Lifetime.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            fixture.Peer = await fixture.Listener.AcceptTcpClientAsync(deadline.Token);
            var timer = Stopwatch.StartNew();
            while (!fixture.States.Contains(MonitorConnectionState.Connected))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Initial connected event missing.");
                await Task.Delay(10);
            }
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public Task Send(string value) => Peer.GetStream().WriteAsync(Encoding.UTF8.GetBytes(value), Lifetime.Token).AsTask();
    public IReadOnlyList<CallRecord> Calls() { lock (gate) return tracker.Snapshot(); }
    public async Task ExpectStop(TimeSpan limit)
    {
        try { await RunTask.WaitAsync(limit); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        Lifetime.Cancel();
        Peer?.Dispose();
        Listener.Stop();
        try { await ExpectStop(TimeSpan.FromSeconds(2)); }
        finally { Lifetime.Dispose(); }
    }
}
