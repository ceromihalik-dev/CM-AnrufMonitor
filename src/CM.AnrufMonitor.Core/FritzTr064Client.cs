using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CM.AnrufMonitor.Core;

/// <summary>
/// Reads FRITZ!Box telephone data through TR-064. Only Get actions are sent.
/// HTTP Digest credentials and URL session tokens are kept in memory, never logged or persisted.
/// All network connections are pinned to private/local IPs resolved once for the configured router.
/// </summary>
public sealed class FritzTr064Client : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;
    private string identityScope = "";
    private string verifiedRouterIdentity = "";
    private const int MaximumXmlBytes = 20 * 1024 * 1024;
    private const int MaximumPhonebooks = 64;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly XNamespace SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly CultureInfo RouterCulture = CreateRouterCulture();

    public Task<FritzCallListSnapshot> GetCallListAsync(FritzAccessOptions options, CancellationToken cancellationToken = default)
        => ExecuteAsync(options, async (context, token) =>
        {
            Discovery discovery = await DiscoverAsync(context, token).ConfigureAwait(false);
            XDocument response = await SoapAsync(context, discovery, "GetCallList", null, token).ConfigureAwait(false);
            string rawUrl = ResponseValue(response, "NewCallListURL");
            if (string.IsNullOrWhiteSpace(rawUrl))
                throw new FritzTr064Exception("Die Anrufliste ist auf dieser FRITZ!Box deaktiviert oder nicht verfügbar.");
            Uri download = context.AllowedUri(rawUrl, context.Origin);
            download = WithQueryParameter(download, "max", "2000");
            download = WithQueryParameter(download, "type", "xml");
            XDocument calls = await context.GetXmlAsync(download, token).ConfigureAwait(false);
            FritzCallListSnapshot snapshot = ParseCallList(calls, discovery.RouterIdentity);
            RememberRouterIdentity(options, snapshot.RouterIdentity);
            return snapshot;
        }, cancellationToken);

    public Task<IReadOnlyList<Contact>> GetPhonebooksAsync(FritzAccessOptions options, CancellationToken cancellationToken = default)
        => ExecuteAsync<IReadOnlyList<Contact>>(options, async (context, token) =>
        {
            Discovery discovery = await DiscoverAsync(context, token).ConfigureAwait(false);
            EnsureExpectedRouter(options, discovery.RouterIdentity);
            XDocument response = await SoapAsync(context, discovery, "GetPhonebookList", null, token).ConfigureAwait(false);
            string rawIds = ResponseValue(response, "NewPhonebookList");
            if (string.IsNullOrWhiteSpace(rawIds))
            {
                RememberRouterIdentity(options, discovery.RouterIdentity);
                return Array.Empty<Contact>();
            }
            string[] ids = rawIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > MaximumPhonebooks || ids.Any(id => !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new FritzTr064Exception("Die FRITZ!Box hat eine ungültige Telefonbuchliste geliefert.");
            var contacts = new List<Contact>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids)
            {
                token.ThrowIfCancellationRequested();
                XDocument bookResponse = await SoapAsync(context, discovery, "GetPhonebook", new XElement("NewPhonebookID", id), token).ConfigureAwait(false);
                string rawUrl = ResponseValue(bookResponse, "NewPhonebookURL");
                if (string.IsNullOrWhiteSpace(rawUrl))
                    throw new FritzTr064Exception("Ein FRITZ!Box-Telefonbuch konnte nicht vollständig abgerufen werden. Die bisherigen Kontakte bleiben erhalten.");
                Uri download = context.AllowedUri(rawUrl, context.Origin);
                XDocument book = await context.GetXmlAsync(download, token).ConfigureAwait(false);
                foreach (Contact contact in ParsePhonebook(book))
                {
                    token.ThrowIfCancellationRequested();
                    string normalized = ContactBook.NormalizeNumber(contact.Number, options.CountryCode, options.AreaCode);
                    if (seen.Add(contact.Name + "\0" + normalized)) contacts.Add(contact);
                    if (contacts.Count > 50000)
                        throw new FritzTr064Exception("Die FRITZ!Box-Telefonbücher enthalten mehr als 50.000 Rufnummern. Der Abruf wurde abgebrochen.");
                }
            }
            RememberRouterIdentity(options, discovery.RouterIdentity);
            return contacts.ToArray();
        }, cancellationToken);

    private static string IdentityScope(FritzAccessOptions options) => options.Host.Trim().TrimEnd('.').ToLowerInvariant()
        + "\0" + options.Port.ToString(CultureInfo.InvariantCulture);

    private void EnsureExpectedRouter(FritzAccessOptions options, string actualIdentity)
    {
        if (identityScope == IdentityScope(options) && verifiedRouterIdentity.Length > 0
            && verifiedRouterIdentity != actualIdentity)
            throw new FritzTr064Exception("Die Gerätekennung der FRITZ!Box hat sich während des Abgleichs geändert. Die Telefonbücher wurden nicht übernommen. Bitte den Abgleich erneut starten.");
    }
    private void RememberRouterIdentity(FritzAccessOptions options, string actualIdentity)
    {
        identityScope = IdentityScope(options);
        verifiedRouterIdentity = actualIdentity;
    }

    private async Task<T> ExecuteAsync<T>(FritzAccessOptions options, Func<RouterContext, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        operation.CancelAfter(OperationTimeout);
        bool entered = false;
        try
        {
            await gate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            using RouterContext context = await RouterContext.CreateAsync(options, operation.Token).ConfigureAwait(false);
            return await action(context, operation.Token).ConfigureAwait(false);
        }
        catch (FritzTr064Exception) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed) != 0) { throw; }
        catch (OperationCanceledException)
        { throw new FritzTr064Exception("Die FRITZ!Box hat nicht rechtzeitig geantwortet. Der Abruf wurde nach 30 Sekunden beendet."); }
        catch (XmlException)
        { throw new FritzTr064Exception("Die FRITZ!Box hat keine gültigen XML-Daten geliefert. Die bisherigen Daten bleiben erhalten."); }
        catch (Exception error) when (error is HttpRequestException or SocketException or IOException)
        { throw new FritzTr064Exception("Die Telefoniedaten konnten nicht von der FRITZ!Box geladen werden. Adresse, Heimnetz und TR-064-Zugriff prüfen."); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        { throw new FritzTr064Exception("Die FRITZ!Box-Einstellungen oder die empfangenen Telefoniedaten sind ungültig."); }
        finally { if (entered) gate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) lifetime.Cancel();
        // Leave the token source undisposed until this object is collected: pending operations may still use its token.
    }

    private sealed record Discovery(string ServiceType, Uri ControlUri, string RouterIdentity);

    private static async Task<Discovery> DiscoverAsync(RouterContext context, CancellationToken token)
    {
        XDocument description = await context.GetXmlAsync(new Uri(context.Origin, "/tr64desc.xml"), token).ConfigureAwait(false);
        XElement? service = description.Descendants().FirstOrDefault(element => element.Name.LocalName == "service"
            && Regex.IsMatch(Value(element, "serviceType"), @"^urn:dslforum-org:service:X_AVM-DE_OnTel:[0-9]{1,3}$", RegexOptions.CultureInvariant));
        if (service is null)
            throw new FritzTr064Exception("Die FRITZ!Box stellt den TR-064-Telefoniedienst nicht bereit. „Zugriff für Anwendungen zulassen“ in den Heimnetz-Einstellungen prüfen.");
        string serviceType = Value(service, "serviceType");
        string controlPath = Value(service, "controlURL");
        if (string.IsNullOrWhiteSpace(controlPath)) throw new FritzTr064Exception("Die FRITZ!Box hat keine gültige Telefonie-Schnittstelle gemeldet.");
        Uri baseUri = context.Origin;
        string declaredBase = description.Root is null ? "" : Value(description.Root, "URLBase");
        if (!string.IsNullOrWhiteSpace(declaredBase)) baseUri = context.AllowedUri(declaredBase, context.Origin);
        Uri control = context.AllowedUri(controlPath, baseUri);
        XElement? device = description.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "device")
            ?? service.Ancestors().LastOrDefault(x => x.Name.LocalName == "device");
        string identity = device is null ? "" : Value(device, "UDN");
        if (string.IsNullOrWhiteSpace(identity) && device is not null) identity = Value(device, "serialNumber");
        if (string.IsNullOrWhiteSpace(identity)) identity = "host:" + context.Origin.IdnHost.ToLowerInvariant();
        if (identity.Length > 300 || identity.Any(char.IsControl)) throw new FritzTr064Exception("Die FRITZ!Box hat eine ungültige Gerätekennung geliefert.");
        return new Discovery(serviceType, control, identity);
    }

    private static async Task<XDocument> SoapAsync(RouterContext context, Discovery discovery, string action, XElement? argument, CancellationToken token)
    {
        // Only compile-time action names reach this method. XElements safely escape argument values.
        if (action is not ("GetCallList" or "GetPhonebookList" or "GetPhonebook")) throw new InvalidOperationException("Only read actions are supported.");
        XNamespace service = discovery.ServiceType;
        var envelope = new XDocument(new XElement(SoapNamespace + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNamespace),
            new XAttribute(SoapNamespace + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(SoapNamespace + "Body", new XElement(service + action, new XAttribute(XNamespace.Xmlns + "u", service), argument))));
        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.ControlUri)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + discovery.ServiceType + "#" + action + "\"");
        return await context.SendXmlAsync(request, token).ConfigureAwait(false);
    }

    private static string ResponseValue(XDocument response, string elementName)
    {
        string? value = response.Descendants().FirstOrDefault(x => x.Name.LocalName == elementName)?.Value;
        if (value is null) throw new FritzTr064Exception("Die FRITZ!Box hat eine unvollständige Antwort auf die Telefonieabfrage geliefert.");
        return value.Trim();
    }

    private static FritzCallListSnapshot ParseCallList(XDocument document, string identity)
    {
        if (document.Root?.Name.LocalName != "root") throw new FritzTr064Exception("Die FRITZ!Box hat keine gültige Anrufliste geliefert.");
        var calls = new List<FritzCallEntry>();
        foreach (XElement element in document.Root.Elements().Where(x => x.Name.LocalName == "Call"))
        {
            string id = Value(element, "Id");
            if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _) || !int.TryParse(Value(element, "Type"), NumberStyles.None, CultureInfo.InvariantCulture, out int type))
                throw new FritzTr064Exception("Die Anrufliste enthält ungültige Anrufkennungen. Der Abruf wurde nicht übernommen.");
            if (type is 9 or 11) continue; // Active calls are handled only by the live monitor.
            if (!DateTime.TryParseExact(Value(element, "Date"), ["dd.MM.yy HH:mm", "dd.MM.yy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss"], RouterCulture, DateTimeStyles.None, out DateTime start))
                throw new FritzTr064Exception("Die Anrufliste enthält ein ungültiges Datum. Der Abruf wurde nicht übernommen.");
            CallDirection direction = type is 1 or 2 or 10 ? CallDirection.Incoming : type == 3 ? CallDirection.Outgoing : CallDirection.Unknown;
            CallState state = type switch
            {
                1 => CallState.Answered, 2 => CallState.Missed, 3 => CallState.Answered,
                10 => Enum.TryParse("Rejected", out CallState rejected) ? rejected : CallState.Unknown,
                _ => CallState.Unknown
            };
            string remote = direction == CallDirection.Outgoing ? Value(element, "Called") : direction == CallDirection.Incoming ? Value(element, "Caller") : "";
            string local = direction == CallDirection.Outgoing ? Value(element, "CallerNumber") : direction == CallDirection.Incoming ? Value(element, "CalledNumber") : "";
            if (local.Length == 0) local = direction == CallDirection.Outgoing ? Value(element, "Caller") : direction == CallDirection.Incoming ? Value(element, "Called") : "";
            remote = SafeNumber(remote);
            local = SafeNumber(local);
            string name = SafeText(Value(element, "Name"), 300);
            int duration = ParseDuration(Value(element, "Duration"));
            if (type == 3 && duration == 0) state = CallState.Unknown; // Finished does not prove a zero-second call was answered.
            calls.Add(new FritzCallEntry
            {
                Id = id, StartedAt = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
                RemoteNumber = remote, LocalNumber = local, Name = name, Direction = direction,
                State = state, DurationSeconds = duration, DurationIsApproximate = true, Type = type
            });
            if (calls.Count > 2000) throw new FritzTr064Exception("Die FRITZ!Box hat mehr als 2.000 Anrufe geliefert. Der Abruf wurde begrenzt und nicht übernommen.");
        }
        string snapshot = SafeText(Value(document.Root, "timestamp"), 128);
        return new FritzCallListSnapshot { RouterIdentity = identity, SnapshotIdentity = snapshot, Calls = calls.ToArray() };
    }

    private static int ParseDuration(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return 0;
        string[] parts = duration.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || hours < 0 || minutes is < 0 or > 59 || hours > 596522)
            throw new FritzTr064Exception("Die Anrufliste enthält eine ungültige Gesprächsdauer.");
        long seconds = ((long)hours * 60 + minutes) * 60;
        if (seconds > int.MaxValue) throw new FritzTr064Exception("Die Anrufliste enthält eine ungültige Gesprächsdauer.");
        return (int)seconds;
    }

    private static IEnumerable<Contact> ParsePhonebook(XDocument document)
    {
        if (document.Root?.Name.LocalName is not ("phonebooks" or "phonebook"))
            throw new FritzTr064Exception("Die FRITZ!Box hat keine gültigen Telefonbuchdaten geliefert.");
        foreach (XElement entry in document.Descendants().Where(x => x.Name.LocalName == "contact"))
        {
            XElement? person = entry.Elements().FirstOrDefault(x => x.Name.LocalName == "person");
            string name = person is null ? "" : SafeText(Value(person, "realName"), 300);
            if (name.Length == 0) continue;
            foreach (XElement number in entry.Elements().Where(x => x.Name.LocalName == "telephony")
                .SelectMany(x => x.Elements()).Where(x => x.Name.LocalName == "number"))
            {
                string raw = SafeNumber(number.Value.Trim());
                if (ContactBook.NormalizeNumber(raw).Length > 0) yield return new Contact(name, raw);
            }
        }
    }
    private static string SafeNumber(string value)
    {
        value = SafeText(value, 256);
        if (value.StartsWith("SIP:", StringComparison.OrdinalIgnoreCase)) value = value[4..].Trim();
        // 'Called' may be a line/contact name. Do not mistake arbitrary labels for numbers.
        return ContactBook.NormalizeNumber(value).Length == 0 ? "" : value;
    }
    private static string SafeText(string value, int maximum)
    {
        string clean = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > maximum || clean.Any(c => char.IsControl(c)))
            throw new FritzTr064Exception("Die FRITZ!Box hat ungültige Telefoniedaten geliefert.");
        return clean;
    }
    private static string Value(XElement parent, string name) => parent.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
    private static CultureInfo CreateRouterCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.Calendar = new GregorianCalendar { TwoDigitYearMax = 2099 };
        return culture;
    }
    private static Uri WithQueryParameter(Uri uri, string key, string value)
    {
        string[] parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        string filtered = string.Join("&", parts.Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals(key, StringComparison.OrdinalIgnoreCase)));
        return new UriBuilder(uri) { Query = (filtered.Length == 0 ? "" : filtered + "&") + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value) }.Uri;
    }

    private sealed class RouterContext : IDisposable
    {
        private readonly HttpClient http;
        private readonly IPAddress[] addresses;
        private readonly HashSet<int> ports;
        public Uri Origin { get; }

        private RouterContext(Uri origin, IPAddress[] addresses, FritzAccessOptions options)
        {
            Origin = origin;
            this.addresses = addresses;
            ports = [options.Port, 80, 49000];
            var credentials = new CredentialCache();
            var credential = new NetworkCredential(options.Username, options.Password);
            foreach (string host in addresses.Select(x => x.ToString()).Append(origin.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (int port in ports)
                    credentials.Add(new UriBuilder("http", host, port, "/").Uri, "Digest", credential);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                Credentials = credentials,
                PreAuthenticate = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                MaxConnectionsPerServer = 1,
                MaxResponseHeadersLength = 32,
                ConnectCallback = ConnectPinnedAsync
            };
            http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public static async Task<RouterContext> CreateAsync(FritzAccessOptions options, CancellationToken token)
        {
            string host = options.Host?.Trim() ?? "";
            if (host.Length is < 1 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Any(char.IsControl))
                throw new FritzTr064Exception("Bitte die lokale FRITZ!Box-Adresse ohne http:// und ohne Port eingeben.");
            if (options.Port is < 1 or > 65535) throw new FritzTr064Exception("Der TR-064-Port ist ungültig; normalerweise wird 49000 verwendet.");
            if (string.IsNullOrWhiteSpace(options.Username) || options.Username.Length > 256 || options.Username.Any(char.IsControl)
                || string.IsNullOrEmpty(options.Password) || options.Password.Length > 1024 || options.Password.Any(c => c is '\r' or '\n'))
                throw new FritzTr064Exception("Für die Telefoniedaten bitte einen FRITZ!Box-Benutzernamen und das zugehörige Kennwort eingeben.");
            IPAddress[] resolved = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
                ? [literal] : await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            IPAddress[] local = resolved.Where(IsLocalAddress).Distinct().OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            if (local.Length == 0)
                throw new FritzTr064Exception("Für den Abruf ist eine lokale FRITZ!Box-Adresse erforderlich. Bitte ihre private IPv4-Adresse im Heimnetz verwenden.");
            return new RouterContext(new UriBuilder("http", host, options.Port, "/").Uri, local, options);
        }

        public Uri AllowedUri(string value, Uri baseUri)
        {
            if (value.Length > 8192 || value.Any(char.IsControl) || value.Contains('\\') || !Uri.TryCreate(baseUri, value, out Uri? uri))
                throw new FritzTr064Exception("Die FRITZ!Box hat eine ungültige Download-Adresse geliefert.");
            ValidateUri(uri);
            return uri;
        }
        private void ValidateUri(Uri uri)
        {
            bool configuredHost = uri.IdnHost.TrimEnd('.').Equals(Origin.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
            bool knownAddress = IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out IPAddress? address) && addresses.Contains(address);
            if (uri.Scheme != "http" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !ports.Contains(uri.Port)
                || !configuredHost && !knownAddress)
                throw new FritzTr064Exception("Die FRITZ!Box verweist auf eine fremde oder nicht unterstützte Adresse. Der Abruf wurde zum Schutz der Zugangsdaten gestoppt; bitte die lokale Box-Adresse prüfen.");
        }
        private async ValueTask<Stream> ConnectPinnedAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            Uri? requestUri = context.InitialRequestMessage.RequestUri;
            if (requestUri is null) throw new FritzTr064Exception("Die Telefonieanfrage hat keine gültige Zieladresse.");
            ValidateUri(requestUri);
            if (!ports.Contains(context.DnsEndPoint.Port)) throw new FritzTr064Exception("Die Telefonieanfrage hat einen ungültigen Zielport.");
            IEnumerable<IPAddress> candidates = IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out IPAddress? literal)
                ? addresses.Where(x => x.Equals(literal)) : addresses;
            foreach (IPAddress target in candidates)
            {
                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (OperationCanceledException) { socket.Dispose(); throw; }
                catch (SocketException) { socket.Dispose(); }
            }
            throw new IOException("Lokale FRITZ!Box nicht erreichbar.");
        }
        public Task<XDocument> GetXmlAsync(Uri uri, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            return GetAndDisposeRequestAsync(request, token);
        }
        private async Task<XDocument> GetAndDisposeRequestAsync(HttpRequestMessage request, CancellationToken token)
        {
            using (request) return await SendXmlAsync(request, token).ConfigureAwait(false);
        }
        public async Task<XDocument> SendXmlAsync(HttpRequestMessage request, CancellationToken token)
        {
            ValidateUri(request.RequestUri!);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new FritzTr064Exception("Die FRITZ!Box hat eine Weiterleitung angefordert. Der Abruf wurde gestoppt; bitte die lokale Box-Adresse und den TR-064-Port prüfen.");
            if (status is 401 or 403) throw new FritzTr064Exception("Die FRITZ!Box hat die Anmeldung abgelehnt. Benutzername, Kennwort und Telefonie-Berechtigung prüfen.");
            if (status == 503) throw new FritzTr064Exception("Die FRITZ!Box ist vorübergehend nicht bereit. Bitte die Zugangsdaten prüfen und später erneut versuchen.");
            if (status == 404) throw new FritzTr064Exception("Die TR-064-Schnittstelle ist nicht erreichbar. „Zugriff für Anwendungen zulassen“ und Port 49000 prüfen.");
            if (!response.IsSuccessStatusCode && status != 500) throw new FritzTr064Exception("Die FRITZ!Box konnte die Telefonieanfrage nicht beantworten.");
            if (response.Content.Headers.ContentLength is > MaximumXmlBytes) throw new FritzTr064Exception("Die FRITZ!Box-Antwort überschreitet die zulässige Größe von 20 MB.");
            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] block = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(block.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumXmlBytes) throw new FritzTr064Exception("Die FRITZ!Box-Antwort überschreitet die zulässige Größe von 20 MB.");
                buffer.Write(block, 0, read);
            }
            buffer.Position = 0;
            using XmlReader reader = XmlReader.Create(buffer, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumXmlBytes,
                IgnoreComments = true
            });
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement? fault = document.Descendants().FirstOrDefault(x => x.Name.LocalName is "Fault" or "sFault");
            if (fault is not null || !response.IsSuccessStatusCode)
            {
                string code = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "errorCode")?.Value.Trim() ?? "";
                throw new FritzTr064Exception(code switch
                {
                    "606" => "Dem FRITZ!Box-Benutzer fehlt die Telefonie-Berechtigung für Anruflisten und Telefonbücher.",
                    "401" or "501" => "Die FRITZ!Box unterstützt diese Telefonieabfrage nicht oder hat sie abgelehnt.",
                    "820" => "Die angeforderten Telefoniedaten sind auf dieser FRITZ!Box nicht verfügbar.",
                    "866" or "867" or "868" => "Die FRITZ!Box verlangt eine zusätzliche Bestätigung. Bitte die Benutzerberechtigung in der Box prüfen.",
                    _ => "Die FRITZ!Box hat die Telefonieabfrage abgelehnt. Telefonie-Berechtigung und TR-064-Einstellung prüfen."
                });
            }
            return document;
        }
        private static bool IsLocalAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                    || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
            return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc);
        }
        public void Dispose() => http.Dispose();
    }
}
