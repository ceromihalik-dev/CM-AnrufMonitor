using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace CM.AnrufMonitor.Core;

/// <summary>
/// Reads the public German caller-identification page after an explicit user request.
/// No router credentials, browser cookies, script execution or CAPTCHA solving are used.
/// The caller owns batching, consent and request pacing.
/// </summary>
public sealed class DasOertlicheReverseLookupProvider : IReverseLookupProvider, IDisposable
{
    private const int MaximumBodyBytes = 2 * 1024 * 1024;
    private const string ProviderHost = "www.dasoertliche.de";
    private const string ProjectUrl = "https://github.com/ceromihalik-dev/CM-AnrufMonitor";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient client;
    private bool disposed;

    public DasOertlicheReverseLookupProvider() : this(CreateHandler()) { }

    /// <summary>Allows an in-memory HTTP handler for deterministic, offline verification.</summary>
    public DasOertlicheReverseLookupProvider(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Id => "dasoertliche";

    public bool SupportsNumber(string normalizedNumber) => normalizedNumber is { Length: >= 8 and <= 16 }
        && normalizedNumber.StartsWith("+49", StringComparison.Ordinal)
        && normalizedNumber[3] is >= '1' and <= '9'
        && normalizedNumber.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    public async Task<ReverseLookupResult> LookupAsync(string normalizedNumber, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsNumber(normalizedNumber))
            return Result(ReverseLookupStatus.Unsupported, normalizedNumber,
                "Das Örtliche unterstützt hier nur vollständige deutsche Rufnummern.");

        string nationalNumber = "0" + normalizedNumber[3..];
        var address = new Uri($"https://{ProviderHost}/rueckwaertssuche/?ph={nationalNumber}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await SendFollowingVerifiedRedirectsAsync(address, normalizedNumber,
                timeout.Token).ConfigureAwait(false);

            // Positive searches currently redirect to the root's search_inv or numeric detail form.
            // Only those same-origin routes are followed; any remaining redirect stops the batch.
            if ((int)response.StatusCode is >= 300 and < 400)
                return Result(ReverseLookupStatus.Blocked, normalizedNumber,
                    "Das Örtliche leitet die Anfrage um. Die Internetsuche wurde angehalten.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Result(ReverseLookupStatus.RateLimited, normalizedNumber,
                    "Das Örtliche begrenzt weitere Anfragen. Bitte später erneut versuchen.")
                    with { RetryAfter = ReadRetryAfter(response.Headers.RetryAfter) };
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Result(ReverseLookupStatus.AuthRequired, normalizedNumber,
                    "Das Örtliche verlangt eine Anmeldung. Die automatische Suche wurde angehalten.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                return Result(ReverseLookupStatus.Blocked, normalizedNumber,
                    "Die Suchseite von Das Örtliche ist für diese Anfrage nicht verfügbar. Die Suche wurde angehalten.");
            // The current public search uses HTTP 404 for a legitimate, explicitly marked no-hit page.
            bool needsVerifiedNoHit = response.StatusCode == HttpStatusCode.NotFound;
            if (!response.IsSuccessStatusCode && !needsVerifiedNoHit)
                return Result(ReverseLookupStatus.TemporaryError, normalizedNumber,
                    "Das Örtliche ist derzeit nicht erreichbar. Es wurde kein Suchergebnis gespeichert.");

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                return Result(ReverseLookupStatus.Blocked, normalizedNumber,
                    "Das Örtliche liefert ein unbekanntes Antwortformat. Die Suche wurde angehalten.");
            byte[] bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            string html = DecodeBody(bytes, response.Content.Headers.ContentType?.CharSet);
            DasOertlicheParsedResult parsed = DasOertlicheHtmlParser.Parse(html, normalizedNumber, timeout.Token);
            if (needsVerifiedNoHit && parsed.Status != ReverseLookupStatus.NotFound)
                return Result(ReverseLookupStatus.Blocked, normalizedNumber,
                    "Die Suchseite von Das Örtliche ist nicht verfügbar. Es liegt kein bestätigtes Suchergebnis vor.");
            return Result(parsed.Status, normalizedNumber, parsed.Message) with
            {
                Name = parsed.Name,
                SourceUrl = parsed.Status is ReverseLookupStatus.Found or ReverseLookupStatus.NotFound
                    or ReverseLookupStatus.Ambiguous ? address.AbsoluteUri : ""
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result(ReverseLookupStatus.TemporaryError, normalizedNumber,
                "Das Örtliche hat nicht rechtzeitig geantwortet. Es wurde kein Suchergebnis gespeichert.");
        }
        catch (InvalidDataException)
        {
            return Result(ReverseLookupStatus.Blocked, normalizedNumber,
                "Die Antwort von Das Örtliche ist zu groß oder nicht sicher lesbar. Die Suche wurde angehalten.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or DecoderFallbackException)
        {
            return Result(ReverseLookupStatus.TemporaryError, normalizedNumber,
                "Die Verbindung zu Das Örtliche ist fehlgeschlagen. Es wurde kein Suchergebnis gespeichert.");
        }
    }

    private ReverseLookupResult Result(ReverseLookupStatus status, string number, string message) => new()
    {
        Status = status,
        NormalizedNumber = number,
        Provider = Id,
        Message = message,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };

    private async Task<HttpResponseMessage> SendFollowingVerifiedRedirectsAsync(Uri initialAddress, string number,
        CancellationToken cancellationToken)
    {
        Uri address = initialAddress;
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            string version = typeof(DasOertlicheReverseLookupProvider).Assembly.GetName().Version?.ToString(3) ?? "0.3.0";
            request.Headers.UserAgent.ParseAdd($"CM-AnrufMonitor/{version} (+{ProjectUrl})");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.AcceptLanguage.ParseAdd("de-DE,de;q=0.9");
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || redirects >= 2
                || !TryRedirect(address, response.Headers.Location, number, out Uri? next)) return response;
            response.Dispose();
            address = next!;
        }
    }

    private static bool TryRedirect(Uri current, Uri? location, string number, out Uri? next)
    {
        next = null;
        if (location is null || location.OriginalString.Length > 2048 || !Uri.TryCreate(current, location, out Uri? candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !candidate.Host.Equals(ProviderHost, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != 443 || candidate.UserInfo.Length > 0 || candidate.Fragment.Length > 0
            || candidate.AbsolutePath is not ("/" or "/rueckwaertssuche/") || candidate == current) return false;

        string? phone = null, form = null, entryId = null;
        foreach (string part in candidate.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] field = part.Split('=', 2);
            string key = Uri.UnescapeDataString(field[0].Replace('+', ' '));
            string value = field.Length == 2 ? Uri.UnescapeDataString(field[1].Replace('+', ' ')) : "";
            if (key == "ph" && phone is null) phone = value;
            else if (key == "form_name" && form is null) form = value;
            else if (key == "id" && entryId is null) entryId = value;
            else return false;
        }
        // An observed unique result uses a public numeric entry ID instead of repeating ph.
        // It is still the same HTTPS origin, and the final page must independently match the full phone.
        if (candidate.AbsolutePath == "/" && form == "detail" && phone is null && entryId is { Length: > 0 and <= 32 }
            && entryId.All(char.IsAsciiDigit))
        {
            next = candidate;
            return true;
        }
        if (ReverseLookupNumbers.Normalize(phone, "+49", "") != number || (form is not null && form != "search_inv")
            || (candidate.AbsolutePath == "/" && form != "search_inv") || entryId is not null) return false;
        next = candidate;
        return true;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // Public HTTPS follows the OS proxy configuration. Local-router clients deliberately do not.
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 32,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 1
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumBodyBytes)
            throw new InvalidDataException("Response size limit.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumBodyBytes)
                throw new InvalidDataException("Response size limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static string DecodeBody(byte[] bytes, string? charset)
    {
        string name = (charset ?? "utf-8").Trim(' ', '\t', '\'', '"').ToLowerInvariant();
        if (name is "iso-8859-1" or "latin1" or "latin-1")
            return Encoding.Latin1.GetString(bytes);
        if (name is not ("utf-8" or "utf8" or "us-ascii"))
            throw new InvalidDataException("Unsupported page encoding.");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? value)
    {
        TimeSpan? delay = value?.Delta ?? (value?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null) return null;
        return delay.Value < TimeSpan.Zero ? TimeSpan.Zero
            : delay.Value > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay.Value;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
    }
}
