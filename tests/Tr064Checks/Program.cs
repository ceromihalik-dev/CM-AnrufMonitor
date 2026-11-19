using CM.AnrufMonitor.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

int checks = 0;
void Check(bool condition, string title)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + title);
    checks++; Console.WriteLine("PASS: " + title);
}
async Task<bool> FailsSafely(Func<Task> action, string? contains = null)
{
    try { await action(); return false; }
    catch (FritzTr064Exception e)
    {
        Check(!e.ToString().Contains("SENSITIVE") && !e.ToString().Contains(FakeFritz.Password) && !e.ToString().Contains(FakeFritz.Username), "Error excludes credential and session-token data");
        return contains is null || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase);
    }
}

using var client = new FritzTr064Client();
await using (var server = new FakeFritz())
{
    FritzAccessOptions options = server.Options with { Host = "localhost" };
    FritzCallListSnapshot snapshot = await client.GetCallListAsync(options);
    Check(snapshot.RouterIdentity == "uuid:test-router-7590" && snapshot.SnapshotIdentity == "123456", "Discovery obtains stable UDN without privileged DeviceInfo request");
    Check(snapshot.Calls.Count == 6 && snapshot.Calls.All(x => x.Type is not (9 or 11)), "Finished call-list rows imported; active9/11 omitted");
    var incoming = snapshot.Calls.Single(x => x.Id == "1");
    Check(incoming.Name == "Müller & Sohn" && incoming.RemoteNumber == "030123456" && incoming.LocalNumber == "030777", "Incoming caller/name/CalledNumber mapped correctly");
    Check(incoming.StartedAt == new DateTime(2026,11,19,14,30,0) && incoming.DurationSeconds == 120 && incoming.DurationIsApproximate, "Router minute timestamp and rounded hh:mm duration preserved");
    var outgoing = snapshot.Calls.Single(x => x.Id == "3");
    Check(outgoing.Direction == CallDirection.Outgoing && outgoing.RemoteNumber == "040123456" && outgoing.LocalNumber == "030777" && outgoing.DurationSeconds == 3660, "Outgoing Called/CallerNumber and hour duration mapped correctly");
    Check(snapshot.Calls.Single(x => x.Id == "2").RemoteNumber == "" && snapshot.Calls.Single(x => x.Id == "2").State == CallState.Missed, "Anonymous missed incoming call remains anonymous and missed");
    Check(snapshot.Calls.Single(x => x.Id == "4").State == CallState.Unknown, "Zero-minute completed outgoing call does not falsely imply answered");
    Check(snapshot.Calls.Single(x => x.Id == "5").State.ToString() == "Rejected", "Rejected type10 has its own state");
    Check(snapshot.Calls.Single(x => x.Id == "6").State == CallState.Unknown && snapshot.Calls.Single(x => x.Id == "6").Direction == CallDirection.Unknown, "Unknown call type stays unknown");
    Check(server.Requests.Any(r => r.Target.StartsWith("/calllist.lua?", StringComparison.Ordinal) && r.Target.Contains("max=2000") && r.Target.Contains("type=xml")), "Call list requests bounded XML snapshot preserving SID");
    Check(server.DigestVerified > 0 && server.Requests.All(r => !r.Authorization.StartsWith("Basic", StringComparison.OrdinalIgnoreCase)), "Real loopback challenge verifies HTTP Digest and no Basic credentials");
    Check(server.Requests.Where(r => r.Method == "POST").All(r => !r.Body.Contains(FakeFritz.Username) && !r.Body.Contains(FakeFritz.Password)), "SOAP bodies never interpolate credentials or credential XML metacharacters");
    Check(server.Requests.All(r => r.Method is "GET" or "POST") && server.SoapActions.All(x => x is "GetCallList"), "Only required read operation used for history");
    var contacts = await client.GetPhonebooksAsync(options);
    Check(contacts.Count == 3 && contacts.Any(x => x.Name == "Erika & Max") && contacts.Count(x => x.Name == "Müller & Sohn") == 2, "All advertised books and every contact number imported");
    Check(contacts.Count(x => ContactBook.NormalizeNumber(x.Number) == "+4930123456") == 1, "Normalized cross-book duplicate removed");
    Check(server.PhonebookIds.Order().SequenceEqual(new[] { "0", "1" }), "Phonebook ID list deduplicated; correct SOAP ID arguments");
    var foreignRegion = await client.GetPhonebooksAsync(options with { CountryCode = "+43" });
    Check(foreignRegion.Count == 4, "Phonebook dedup uses configured country, without collapsing foreign number into national one");
    Check(!JsonSerializer.Serialize(options).Contains(FakeFritz.Password) && !options.ToString().Contains(FakeFritz.Password), "Access options suppress password in JSON and diagnostic ToString");
    server.RouterIdentity = "uuid:different-router";
    Check(await FailsSafely(() => client.GetPhonebooksAsync(options), "Gerätekennung"), "Changed router UDN between call list and phonebooks cannot contaminate identity cache");
}

await using (var server = new FakeFritz { Authentication = "Basic" })
{
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options), "Anmeldung"), "Basic-only challenge fails instead of downgrading authentication");
    Check(server.Requests.All(x => x.Authorization.Length == 0), "Basic challenge receives no Authorization secret");
}
await using (var server = new FakeFritz())
{
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options with { Password = "wrong" }), "Anmeldung"), "Wrong Digest password produces actionable sanitized failure");
    Check(server.Requests.Count < 6, "Bad credentials do not trigger repeated authentication flood");
}

foreach (string unsafeTemplate in new[]
{
    "http://example.invalid:49000/calllist?sid=SENSITIVE",
    "http://127.0.0.2:49000/calllist?sid=SENSITIVE",
    "http://127.0.0.1:12345/calllist?sid=SENSITIVE",
    "http://user:SENSITIVE@127.0.0.1:{port}/calllist",
    "http://127.0.0.1:{port}/calllist?sid=SENSITIVE#fragment",
    "https://127.0.0.1:{port}/calllist?sid=SENSITIVE",
    "file:///tmp/SENSITIVE.xml"
})
{
    await using var server = new FakeFritz { ReturnedCallUrl = unsafeTemplate };
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options)), "Unsafe returned URL blocked before fetch: " + unsafeTemplate.Split(':')[0]);
    Check(server.Requests.All(r => !r.Target.StartsWith("/calllist", StringComparison.Ordinal)), "No download request made for rejected URL");
}
await using (var server = new FakeFritz { ReturnedCallUrl = "/calllist.lua?sid=SENSITIVE" })
{
    Check((await client.GetCallListAsync(server.Options)).Calls.Count == 6, "Relative same-router session URL supported");
}
await using (var server = new FakeFritz { RedirectDownload = true })
{
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options), "Weiterleitung"), "HTTP redirect rejected instead of following location with SID");
    Check(server.Requests.All(r => !r.Target.StartsWith("/redirected", StringComparison.Ordinal)), "Redirect target not requested");
}
await using (var server = new FakeFritz { UnsafeControl = true })
{
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options)), "Foreign SOAP controlURL rejected during discovery");
    Check(server.Requests.Count == 1, "No SOAP credentials sent to foreign control URL");
}
await using (var server = new FakeFritz { DtdCallList = true })
{
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options), "XML"), "DTD/external entities rejected in downloaded call list");
}
await using (var server = new FakeFritz { OversizeContentLength = true })
{
    Check(await FailsSafely(() => client.GetCallListAsync(server.Options), "20 MB"), "Oversized declared response rejected before buffering");
}
await using (var server = new FakeFritz { PhonebookSecondFault = true })
{
    Check(await FailsSafely(() => client.GetPhonebooksAsync(server.Options), "Berechtigung"), "Partial phonebook refresh fails atomically when second book denied");
}
await using (var server = new FakeFritz { DelayDownload = true })
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var elapsed = Stopwatch.StartNew();
    bool cancelled = false;
    try { await client.GetCallListAsync(server.Options, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(3), "Caller cancellation interrupts pending HTTP read promptly");
}
await using (var server = new FakeFritz { DelayDownload = true })
{
    using var disposing = new FritzTr064Client();
    Task task = disposing.GetCallListAsync(server.Options);
    await Task.Delay(150);
    disposing.Dispose();
    bool cancelled = false;
    try { await task; } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Disposing client cancels outstanding operation");
}
Check(await FailsSafely(() => client.GetCallListAsync(new FritzAccessOptions { Host = "8.8.8.8", Username = FakeFritz.Username, Password = FakeFritz.Password }), "lokale"), "Public destination rejected before sending credentials");
Console.WriteLine($"\n{checks} TR-064 checks passed.");

sealed record Request(string Method, string Target, Dictionary<string,string> Headers, string Body)
{
    public string Authorization => Headers.TryGetValue("Authorization", out string? value) ? value : "";
}
sealed class FakeFritz : IAsyncDisposable
{
    public const string Username = "phone<&>user";
    public const string Password = "Pass<&>word-SENSITIVE-123";
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string Realm = "FRITZ!Box Test";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> clients = [];
    public ConcurrentQueue<Request> Requests { get; } = new();
    public ConcurrentBag<string> SoapActions { get; } = [];
    public ConcurrentBag<string> PhonebookIds { get; } = [];
    public int DigestVerified;
    public string RouterIdentity { get; set; } = "uuid:test-router-7590";
    public string Authentication { get; init; } = "Digest";
    public string? ReturnedCallUrl { get; init; }
    public bool RedirectDownload { get; init; }
    public bool UnsafeControl { get; init; }
    public bool DtdCallList { get; init; }
    public bool OversizeContentLength { get; init; }
    public bool PhonebookSecondFault { get; init; }
    public bool DelayDownload { get; init; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FritzAccessOptions Options => new() { Host = "127.0.0.1", Port = Port, Username = Username, Password = Password };
    public FakeFritz() { listener.Start(); loop = AcceptLoop(); }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token);
                clients.Add(Handle(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var header = new List<byte>();
                byte[] single = new byte[1];
                while (header.Count < 65536)
                {
                    if (await stream.ReadAsync(single, stopping.Token) == 0) return;
                    header.Add(single[0]);
                    int n = header.Count;
                    if (n >= 4 && header[n-4] == 13 && header[n-3] == 10 && header[n-2] == 13 && header[n-1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon]] = line[(colon+1)..].Trim();
                }
                int length = headers.TryGetValue("Content-Length", out string? contentLength) ? int.Parse(contentLength, CultureInfo.InvariantCulture) : 0;
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, stopping.Token);
                var request = new Request(first[0], first[1], headers, Encoding.UTF8.GetString(body));
                Requests.Enqueue(request);
                if (request.Target == "/tr64desc.xml")
                {
                    string control = UnsafeControl ? "http://example.invalid:49000/control?sid=SENSITIVE" : "/upnp/control/x_contact";
                    string description = "<root xmlns=\"urn:dslforum-org:device-1-0\"><device><UDN>" + System.Security.SecurityElement.Escape(RouterIdentity) + "</UDN><serviceList><service><serviceType>urn:dslforum-org:service:X_AVM-DE_OnTel:1</serviceType><controlURL>" + System.Security.SecurityElement.Escape(control) + "</controlURL></service></serviceList></device></root>";
                    await Respond(stream, 200, description); return;
                }
                if (request.Method == "POST")
                {
                    if (Authentication == "Basic" || !ValidDigest(request))
                    {
                        string challenge = Authentication == "Basic" ? "Basic realm=\"FRITZ!Box Test\"" : "Digest realm=\"" + Realm + "\", nonce=\"" + Nonce + "\", algorithm=MD5, qop=\"auth\"";
                        await Respond(stream, 401, "", "WWW-Authenticate: " + challenge + "\r\n"); return;
                    }
                    Interlocked.Increment(ref DigestVerified);
                    XDocument document = XDocument.Parse(request.Body);
                    XElement action = document.Descendants().First(x => x.Name.LocalName == "Body").Elements().Single();
                    SoapActions.Add(action.Name.LocalName);
                    if (action.Name.LocalName == "GetCallList")
                    {
                        string url = (ReturnedCallUrl ?? "http://127.0.0.1:{port}/calllist.lua?sid=SENSITIVE").Replace("{port}", Port.ToString(CultureInfo.InvariantCulture));
                        await Respond(stream, 200, Soap("GetCallList", "NewCallListURL", url)); return;
                    }
                    if (action.Name.LocalName == "GetPhonebookList") { await Respond(stream, 200, Soap("GetPhonebookList", "NewPhonebookList", "0,1,0")); return; }
                    if (action.Name.LocalName == "GetPhonebook")
                    {
                        string id = action.Elements().Single().Value; PhonebookIds.Add(id);
                        if (PhonebookSecondFault && id == "1")
                        {
                            await Respond(stream, 500, "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorCode>606</errorCode><errorDescription>SENSITIVE</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>"); return;
                        }
                        await Respond(stream, 200, Soap("GetPhonebook", "NewPhonebookURL", "/phonebook" + id + ".xml?sid=SENSITIVE")); return;
                    }
                    await Respond(stream, 500, "<error/>"); return;
                }
                if (request.Target.StartsWith("/calllist.lua", StringComparison.Ordinal))
                {
                    if (DelayDownload) await Task.Delay(10000, stopping.Token);
                    if (RedirectDownload) { await Respond(stream, 302, "", "Location: /redirected?sid=SENSITIVE\r\n"); return; }
                    if (OversizeContentLength) { await Respond(stream, 200, "", overrideLength: 20*1024*1024+1); return; }
                    if (DtdCallList) { await Respond(stream, 200, "<!DOCTYPE root [<!ENTITY steal SYSTEM 'file:///tmp/SENSITIVE'>]><root><timestamp>&steal;</timestamp></root>"); return; }
                    await Respond(stream, 200, CallList()); return;
                }
                if (request.Target.StartsWith("/phonebook", StringComparison.Ordinal))
                {
                    string contacts = request.Target.StartsWith("/phonebook0", StringComparison.Ordinal)
                        ? "<contact><person><realName>Müller &amp; Sohn</realName></person><telephony><number>030123456</number><number>+4915112345678</number></telephony></contact>"
                        : "<contact><person><realName>Müller &amp; Sohn</realName></person><telephony><number>+49 (0)30 123456</number></telephony></contact><contact><person><realName>Erika &amp; Max</realName></person><telephony><number>040999</number></telephony></contact>";
                    await Respond(stream, 200, "<phonebooks><phonebook>" + contacts + "</phonebook></phonebooks>"); return;
                }
                await Respond(stream, 404, "");
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }
    private static string Soap(string action, string field, string value)
    {
        XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace u = "urn:dslforum-org:service:X_AVM-DE_OnTel:1";
        return new XDocument(new XElement(s+"Envelope", new XElement(s+"Body", new XElement(u+(action+"Response"), new XElement(field,value))))).ToString();
    }
    private static string CallList()
    {
        string Call(int id,int type,string caller,string called,string callerNumber,string calledNumber,string duration,string name="") =>
            new XElement("Call",new XElement("Id",id),new XElement("Type",type),new XElement("Caller",caller),new XElement("Called",called),new XElement("CallerNumber",callerNumber),new XElement("CalledNumber",calledNumber),new XElement("Name",name),new XElement("Date","19.11.26 14:30"),new XElement("Duration",duration)).ToString();
        return "<root><timestamp>123456</timestamp>" +
            Call(1,1,"030123456","Büro","","030777","0:02","Müller & Sohn") +
            Call(2,2,"","SIP: 030777","","","0:00") +
            Call(3,3,"SIP: 030777","040123456","030777","","1:01") +
            Call(4,3,"SIP: 030777","040999","030777","","0:00") +
            Call(5,10,"030222","030777","","030777","0:00") +
            Call(6,99,"030444","030777","","030777","0:00") +
            Call(7,9,"030555","030777","","030777","0:02") +
            Call(8,11,"030777","030666","030777","","0:03") + "</root>";
    }
    private static bool ValidDigest(Request request)
    {
        if (!request.Authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(request.Authorization[7..], "([A-Za-z0-9_-]+)=(?:\"((?:\\\\.|[^\"])*)\"|([^, ]+))"))
            values[match.Groups[1].Value] = match.Groups[2].Success ? Regex.Unescape(match.Groups[2].Value) : match.Groups[3].Value;
        string V(string key) => values.TryGetValue(key,out string? value)?value:"";
        string H(string input) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        string expected = H(H(Username+":"+Realm+":"+Password)+":"+Nonce+":"+V("nc")+":"+V("cnonce")+":auth:"+H(request.Method+":"+V("uri")));
        return V("username")==Username && V("nonce")==Nonce && V("qop")=="auth" && V("uri")==request.Target && V("response")==expected;
    }
    private async Task Respond(NetworkStream stream,int status,string body,string extraHeaders="",int? overrideLength=null)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(body);
        string reason=status switch{200=>"OK",401=>"Unauthorized",302=>"Found",404=>"Not Found",_=>"Internal Server Error"};
        byte[] headers=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: {overrideLength??bytes.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");
        await stream.WriteAsync(headers,stopping.Token);
        await stream.WriteAsync(bytes,stopping.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel(); listener.Stop();
        await loop;
        await Task.WhenAll(clients);
        stopping.Dispose();
    }
}
