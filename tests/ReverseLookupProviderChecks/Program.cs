using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CM.AnrufMonitor.Core;

const string Number = "+4930399760"; // Public FRITZ company switchboard. Tests never contact it or a live provider.
int passed = 0, failed = 0;
string noHit = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "no-hit-current.html"));
string currentResult = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "result-current.html"));
string currentDetail = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "detail-current.html"));
string unrecognizedResponse = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "unrecognized-response.html"));

// A deliberately explicit, opt-in smoke check for one provider-listed public business.
// Normal execution and CI never enter this branch or access the Internet.
if (args is ["--live-public-company"])
{
    using var live = new DasOertlicheReverseLookupProvider();
    ReverseLookupResult result = await live.LookupAsync("+49302290212", CancellationToken.None);
    Console.WriteLine($"Live public company: {result.Status}; name={result.Name}; {result.Message}");
    return result.Status == ReverseLookupStatus.Found ? 0 : 1;
}

// Explicit offline inspection of a page captured separately from a public-company request.
// This mode performs no network access and is excluded from the automated test counts.
if (args is ["--captured", string capturePath, string number, string expected])
{
    using var capturedProvider = new DasOertlicheReverseLookupProvider(new StubHandler((_, _) =>
        Task.FromResult(Html(File.ReadAllText(capturePath), expected == "NotFound" ? HttpStatusCode.NotFound : HttpStatusCode.OK))));
    ReverseLookupResult capture = await capturedProvider.LookupAsync(number, CancellationToken.None);
    Console.WriteLine($"Captured public page: {capture.Status}; name={capture.Name}; {capture.Message}");
    return capture.Status.ToString() == expected ? 0 : 1;
}

await Check("Transparent HTTPS request and fixed provider origin", async () =>
{
    using var handler = new StubHandler((request, _) =>
    {
        Equal("GET", request.Method.Method);
        Equal("https", request.RequestUri!.Scheme);
        Equal("www.dasoertliche.de", request.RequestUri.Host);
        Equal("/rueckwaertssuche/", request.RequestUri.AbsolutePath);
        Equal("?ph=030399760", request.RequestUri.Query);
        string agent = request.Headers.UserAgent.ToString();
        Require(agent.StartsWith("CM-AnrufMonitor/", StringComparison.Ordinal), "Application identity absent.");
        Require(agent.Contains("https://github.com/ceromihalik-dev/CM-AnrufMonitor", StringComparison.Ordinal), "Project identity absent.");
        Require(!agent.Contains("Mozilla", StringComparison.OrdinalIgnoreCase), "Browser impersonation.");
        Require(!request.Headers.Contains("Cookie") && request.Headers.Authorization is null, "Unexpected credentials.");
        return Task.FromResult(Html(JsonEntity("FRITZ GmbH", "+49 (0)30 39976-0")));
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    ReverseLookupResult result = await provider.LookupAsync(Number, CancellationToken.None);
    Equal(ReverseLookupStatus.Found, result.Status);
    Equal("dasoertliche", result.Provider);
    Equal(Number, result.NormalizedNumber);
    Equal("https://www.dasoertliche.de/rueckwaertssuche/?ph=030399760", result.SourceUrl);
    Equal(1, handler.Calls);
});

await Check("JSON-LD name and complete national phone belong to same entity", async () =>
    await Status(JsonEntity("Örtliche Beispiel GmbH", "030 / 39976-0"), ReverseLookupStatus.Found, "Örtliche Beispiel GmbH"));
await Check("JSON-LD international phone array", async () =>
    await Status("""<script type="application/ld+json">{"@type":"Organization","name":"Beispiel","telephone":["+49301234567","0049 30 399760"]}</script>""", ReverseLookupStatus.Found, "Beispiel"));
await Check("JSON-LD graph ignores global brand and unmatched entities", async () =>
    await Status("""<script type="application/ld+json">{"@graph":[{"@type":"WebSite","name":"Global brand"},{"@type":"Organization","name":"Wrong firm","telephone":"+49301234567"},{"@type":"Corporation","name":"Correct firm","telephone":"+4930399760"}]}</script>""", ReverseLookupStatus.Found, "Correct firm"));
await Check("A matching ContactPoint stays bound to its organization", async () =>
    await Status("""<script type="application/ld+json">{"@type":"Organization","name":"Office","contactPoint":{"@type":"ContactPoint","telephone":"030399760"}}</script>""", ReverseLookupStatus.Found, "Office"));
await Check("Two names for exact same number are ambiguous", async () =>
    await Status(JsonEntity("First firm", "030399760") + JsonEntity("Second firm", "030399760"), ReverseLookupStatus.Ambiguous));
await Check("Repeated equivalent identity does not create ambiguity", async () =>
    await Status(JsonEntity("Example GmbH", "030399760") + JsonEntity("EXAMPLE GMBH", "+4930399760"), ReverseLookupStatus.Found, "Example GmbH"));
await Check("No matching by suffix", async () =>
    await Status(JsonEntity("Wrong owner", "+4930330399760"), ReverseLookupStatus.UnrecognizedResponse));
await Check("No matching without full area or country prefix", async () =>
    await Status(JsonEntity("Wrong owner", "399760"), ReverseLookupStatus.UnrecognizedResponse));
await Check("No cross-entity pairing of global name and nested telephone", async () =>
    await Status("""<script type="application/ld+json">{"@type":"Organization","name":"Global brand","mainEntity":{"@type":"Organization","telephone":"030399760"}}</script>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("WebSite metadata is never a caller name", async () =>
    await Status("""<script type="application/ld+json">{"@type":"WebSite","name":"Directory","telephone":"030399760"}</script>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Malformed JSON is not a negative result", async () =>
    await Status("""<script type="application/ld+json">{"name":broken}</script>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("An individually unreadable result is distinct from an access block and a miss", async () =>
{
    using var handler = new StubHandler((_, _) => Task.FromResult(Html(unrecognizedResponse)));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    ReverseLookupResult result = await provider.LookupAsync(Number, CancellationToken.None);
    Equal(ReverseLookupStatus.UnrecognizedResponse, result.Status);
    Equal("", result.Name);
    Equal("", result.SourceUrl);
    Require(!result.Message.Contains("angehalten", StringComparison.OrdinalIgnoreCase), "Individual response claims to stop the batch.");
    Require(!result.Message.Contains("Beispielkontakt", StringComparison.Ordinal)
        && !result.Message.Contains("030399760", StringComparison.Ordinal)
        && !result.Message.Contains(Number, StringComparison.Ordinal)
        && !result.Message.Contains("<", StringComparison.Ordinal), "Response diagnostics contain personal page data.");
});
await Check("Empty successful response is unrecognized rather than a cached miss", async () =>
    await Status("", ReverseLookupStatus.UnrecognizedResponse));
await Check("The next exact identity remains resolvable after an unrecognized answer", async () =>
{
    int request = 0;
    using var handler = new StubHandler((_, _) => Task.FromResult(Html(++request == 1
        ? unrecognizedResponse : JsonEntity("Öffentliches Beispielunternehmen", "030399760"))));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.UnrecognizedResponse, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
    ReverseLookupResult next = await provider.LookupAsync(Number, CancellationToken.None);
    Equal(ReverseLookupStatus.Found, next.Status);
    Equal("Öffentliches Beispielunternehmen", next.Name);
    Equal(2, handler.Calls);
});
await Check("Microdata content values and exact scope", async () =>
    await Status("""<section itemscope itemtype="https://schema.org/LocalBusiness"><meta itemprop="name" content="Bäckerei &amp; Café"><meta itemprop="telephone" content="+49 30 399760"></section>""", ReverseLookupStatus.Found, "Bäckerei & Café"));
await Check("Nested microdata names cannot attach to outer phone", async () =>
    await Status("""<section itemscope><meta itemprop="telephone" content="030399760"><aside itemscope><span itemprop="name">Wrong owner</span></aside></section>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Result card binds name to its own tel link", async () =>
    await Status("""<div class="hit"><a class="name"><span>Beispiel Handel</span></a><a href="tel:+4930399760">Anrufen</a></div>""", ReverseLookupStatus.Found, "Beispiel Handel"));
await Check("Different result cards cannot cross-match", async () =>
    await Status("""<div class="hit"><a class="name">Wrong owner</a><a href="tel:+49301234567">Anrufen</a></div><div class="hit"><a href="tel:+4930399760">Anrufen</a></div>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Nested result cards cannot donate their names to parent phone", async () =>
    await Status("""<div class="hit"><a href="tel:+4930399760">Anrufen</a><div class="hit"><a class="name">Wrong owner</a><a href="tel:+49301234567">Anrufen</a></div></div>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Page heading and query echo are not an identity", async () =>
    await Status("""<h1>Das Örtliche</h1><input name="ph" value="030399760"><a href="tel:030399760">Anrufen</a>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Hidden result card is ignored", async () =>
    await Status("""<div class="hit" hidden><a class="name">Hidden owner</a><a href="tel:030399760">Anrufen</a></div>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("JavaScript text is never evaluated as a result", async () =>
    await Status("""<script>document.write('<div class="hit"><a class="name">Fake</a><a href="tel:030399760">Call</a></div>');</script>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Current explicit no-hit HTML accepts HTTP404", async () =>
    await Status(noHit, ReverseLookupStatus.NotFound, status: HttpStatusCode.NotFound));
await Check("Current explicit no-hit HTML accepts HTTP200", async () =>
    await Status(noHit, ReverseLookupStatus.NotFound));
await Check("No-hit must echo the exact requested phone", async () =>
    await Status(noHit.Replace("value=\"030399760\"", "value=\"0301234567\"", StringComparison.Ordinal), ReverseLookupStatus.UnrecognizedResponse, status: HttpStatusCode.NotFound));
await Check("No-hit without input echo remains unknown", async () =>
    await Status("""<div class="nonumber01">Leider konnten wir keinen Eintrag zu dieser Telefonnummer finden.</div>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("FAQ no-hit wording never creates NotFound", async () =>
    await Status("""<input name="ph" value="030399760"><article class="faq">Leider konnten wir keinen Eintrag zu dieser Telefonnummer finden.</article>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Generic HTTP404 never creates NotFound", async () =>
    await Status("<html><h1>Not Found</h1></html>", ReverseLookupStatus.UnrecognizedResponse, status: HttpStatusCode.NotFound));
await Check("HTTP404 cannot carry a positive identity", async () =>
    await Status(JsonEntity("Example", "030399760"), ReverseLookupStatus.UnrecognizedResponse, status: HttpStatusCode.NotFound));
await Check("An HTTP404 challenge stays a real access block", async () =>
    await Status("<title>Security Check / CAPTCHA</title><form>Verify you are human</form>",
        ReverseLookupStatus.Blocked, status: HttpStatusCode.NotFound));
await Check("Legacy HTTP410 stops even with no-hit wording", async () =>
    await Status(noHit, ReverseLookupStatus.Blocked, status: HttpStatusCode.Gone));
await Check("HTTP403 blocks without interpreting body", async () =>
    await Status(JsonEntity("Example", "030399760"), ReverseLookupStatus.Blocked, status: HttpStatusCode.Forbidden));
await Check("HTTP401 requests no fabricated login", async () =>
    await Status("", ReverseLookupStatus.AuthRequired, status: HttpStatusCode.Unauthorized));
await Check("HTTP503 remains a transient error", async () =>
    await Status("", ReverseLookupStatus.TemporaryError, status: HttpStatusCode.ServiceUnavailable));
await Check("Rate limit exposes server retry delay", async () =>
{
    using var handler = new StubHandler((_, _) =>
    {
        var response = Html("", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
        return Task.FromResult(response);
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    ReverseLookupResult result = await provider.LookupAsync(Number, CancellationToken.None);
    Equal(ReverseLookupStatus.RateLimited, result.Status);
    Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    Equal("", result.SourceUrl);
});
await Check("Redirect is rejected and never followed", async () =>
{
    using var handler = new StubHandler((_, _) =>
    {
        var response = Html("", HttpStatusCode.Redirect);
        response.Headers.Location = new Uri("https://example.invalid/collect");
        return Task.FromResult(response);
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.Blocked, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
    Equal(1, handler.Calls);
});
await Check("Unsupported foreign numbers perform no HTTP request", async () =>
{
    using var handler = new StubHandler((_, _) => throw new Exception("Must not request."));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    foreach (string value in new[] { "+4312345678", "+49030399760", "030399760", "**610", "+49", "+4930x399760", "", "+49303997601234567" })
    {
        Require(!provider.SupportsNumber(value), "Invalid or unsupported number accepted.");
        Equal(ReverseLookupStatus.Unsupported, (await provider.LookupAsync(value, CancellationToken.None)).Status);
    }
    Equal(0, handler.Calls);
});
await Check("Declared response size is bounded", async () =>
{
    using var handler = new StubHandler((_, _) =>
    {
        var response = Html("small");
        response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
        return Task.FromResult(response);
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.Blocked, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
});
await Check("Unknown-length response is bounded while streaming", async () =>
{
    using var stream = new TrackingStream(new byte[4 * 1024 * 1024]);
    using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) }));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.Blocked, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
    Require(stream.BytesRead <= 2 * 1024 * 1024 + 16 * 1024, "Unbounded response consumed.");
});
await Check("Caller cancellation interrupts an idle HTTP request", async () =>
{
    using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Html(""); });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
    var stopwatch = Stopwatch.StartNew();
    try { await provider.LookupAsync(Number, cancel.Token); throw new Exception("Cancellation swallowed."); }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    Require(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Cancellation was not prompt.");
});
await Check("Caller cancellation interrupts a stalled response body", async () =>
{
    using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new BlockingStream()) }));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
    try { await provider.LookupAsync(Number, cancel.Token); throw new Exception("Cancellation swallowed."); }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
});
await Check("Cancellation also interrupts CPU parsing of nested provider markup", async () =>
{
    string nested = string.Concat(Enumerable.Repeat("<form>", 100))
        + string.Concat(Enumerable.Repeat("<span></span>", 14000))
        + string.Concat(Enumerable.Repeat("</form>", 100));
    using var handler = new StubHandler((_, _) => Task.FromResult(Html(nested)));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    var stopwatch = Stopwatch.StartNew();
    try { await provider.LookupAsync(Number, cancel.Token); throw new Exception("CPU cancellation swallowed."); }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "CPU cancellation was not prompt.");
});
await Check("Pre-cancelled request performs no HTTP call", async () =>
{
    using var handler = new StubHandler((_, _) => throw new Exception("Must not request."));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await provider.LookupAsync(Number, cancel.Token); throw new Exception("Cancellation swallowed."); }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    Equal(0, handler.Calls);
});
await Check("Transport errors do not expose a query or exception detail", async () =>
{
    using var handler = new StubHandler((_, _) => throw new HttpRequestException("private-marker https://example.invalid/?number=" + Number));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    ReverseLookupResult result = await provider.LookupAsync(Number, CancellationToken.None);
    Equal(ReverseLookupStatus.TemporaryError, result.Status);
    Require(!result.Message.Contains("private-marker", StringComparison.Ordinal) && !result.Message.Contains(Number, StringComparison.Ordinal), "Sensitive error exposed.");
});
await Check("Invalid UTF8 never manufactures a caller name", async () =>
{
    using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([0xc3, 0x28]) }));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.TemporaryError, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
});
await Check("Declared Latin1 names retain umlauts", async () =>
{
    using var handler = new StubHandler((_, _) =>
    {
        var content = new ByteArrayContent(Encoding.Latin1.GetBytes(JsonEntity("Bäckerei Müller", "030399760")));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "ISO-8859-1" };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal("Bäckerei Müller", (await provider.LookupAsync(Number, CancellationToken.None)).Name);
});
await Check("Unexpected content type blocks rather than caching a miss", async () =>
{
    using var handler = new StubHandler((_, _) =>
    {
        var response = Html(JsonEntity("Example", "030399760"));
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.Blocked, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
});
await Check("CAPTCHA page stops even if it contains misleading identity metadata", async () =>
    await Status("<title>Security Check / CAPTCHA</title>" + JsonEntity("Example", "030399760"), ReverseLookupStatus.Blocked));
await Check("Challenge script is not executed or bypassed", async () =>
    await Status("""<script src="https://challenges.cloudflare.com/turnstile/v0/api.js"></script><h1>Verify you are human</h1>""", ReverseLookupStatus.Blocked));
await Check("A visible German CAPTCHA request still stops the search", async () =>
    await Status("<html><head><title>Rückwärtssuche</title></head><body><form>Bitte bestätigen Sie, dass Sie kein Roboter sind.</form></body></html>",
        ReverseLookupStatus.Blocked));
await Check("Unreasonably deep HTML is bounded", async () =>
    await Status(string.Concat(Enumerable.Repeat("<div>", 150)) + JsonEntity("Example", "030399760"), ReverseLookupStatus.Blocked));
await Check("Overlong identity is rejected rather than truncated", async () =>
    await Status(JsonEntity(new string('a', 301), "030399760"), ReverseLookupStatus.UnrecognizedResponse));
await Check("HTML entities decoded and display controls removed", async () =>
    await Status(JsonEntity("  Beispiel  &amp;  Söhne\u202e  ", "030399760"), ReverseLookupStatus.Found, "Beispiel & Söhne"));
await Check("Current result markup binds labelled phone text to matching name", async () =>
    await Status(currentResult, ReverseLookupStatus.Found, "Sparkasse Schwaben-Bodensee", number: "+4983316090"));
await Check("Current detail page accepts intentionally hidden Organization metadata", async () =>
    await Status(currentDetail, ReverseLookupStatus.Found, "Juwelier Leicht, Juwelier im Hotel Adlon", number: "+49302290212"));
await Check("Current detail metadata still requires exact full number", async () =>
    await Status(currentDetail, ReverseLookupStatus.UnrecognizedResponse));
await Check("Intentionally hidden metadata inside a template remains inert", async () =>
    await Status("<template>" + currentDetail + "</template>", ReverseLookupStatus.UnrecognizedResponse, number: "+49302290212"));
await Check("Different current branch names remain ambiguous", async () =>
    await Status(currentResult.Replace("href=\"/another-ignored-detail\">Sparkasse Schwaben-Bodensee", "href=\"/another-ignored-detail\">Sparkasse Schwaben-Bodensee - SB-Filiale", StringComparison.Ordinal), ReverseLookupStatus.Ambiguous, number: "+4983316090"));
await Check("Current phoneblock never matches by suffix", async () =>
    await Status(currentResult, ReverseLookupStatus.UnrecognizedResponse, number: "+493083316090"));
await Check("A fax-only or unlabelled phoneblock is not a telephone hit", async () =>
    await Status(currentResult.Replace("Tel.", "Fax", StringComparison.Ordinal), ReverseLookupStatus.UnrecognizedResponse, number: "+4983316090"));
await Check("Nonbreaking spaces in published phone numbers are accepted", async () =>
    await Status(JsonEntity("Example", "030\u00a039976-0"), ReverseLookupStatus.Found, "Example"));
await Check("FAQ, application and contact-label metadata are not caller entities", async () =>
{
    foreach (string type in new[] { "FAQPage", "Question", "ContactPoint", "SoftwareApplication", "MadeUpEntity" })
        await Status(JsonEntity("Not an identity", "030399760").Replace("Organization", type, StringComparison.Ordinal), ReverseLookupStatus.UnrecognizedResponse);
});
await Check("Microdata WebSite type is not a caller entity", async () =>
    await Status("""<section itemscope itemtype="https://schema.org/WebSite"><span itemprop="name">Directory brand</span><meta itemprop="telephone" content="030399760"></section>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("A result card cannot borrow a nested microdata phone", async () =>
    await Status("""<div class="hit"><a class="name">Wrong parent</a><section itemscope itemtype="https://schema.org/Person"><a href="tel:030399760">call</a></section></div>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Microdata cannot borrow a nested result-card phone", async () =>
    await Status("""<section itemscope itemtype="https://schema.org/Organization"><span itemprop="name">Wrong parent</span><div class="hit"><a itemprop="telephone" href="tel:030399760">call</a></div></section>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Raw script closing prefix cannot expose JavaScript templates as HTML", async () =>
    await Status("""<script>const marker="</scriptx>"; const html='<div class="hit"><a class="name">Script fake</a><a href="tel:030399760">call</a></div>';</script>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Inert templates cannot create live caller identities", async () =>
    await Status("""<template><div class="hit"><a class="name">Placeholder Name</a><a href="tel:030399760">call</a></div></template>""", ReverseLookupStatus.UnrecognizedResponse));
await Check("Metadata in a template is also inert", async () =>
    await Status("<template>" + JsonEntity("Placeholder Name", "030399760") + "</template>", ReverseLookupStatus.UnrecognizedResponse));
await Check("Title and textarea raw text cannot expose fake result cards", async () =>
{
    foreach (string tag in new[] { "title", "textarea" })
        await Status("<" + tag + ">" + """<div class="hit"><a class="name">Raw fake</a><a href="tel:030399760">call</a></div>""" + "</" + tag + ">", ReverseLookupStatus.UnrecognizedResponse);
});
await Check("A valid same-origin positive-search redirect is followed", async () =>
{
    using var handler = new StubHandler((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/rueckwaertssuche/")
        {
            var response = Html("", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("//www.dasoertliche.de/?form_name=search_inv&ph=030399760", UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }
        Equal("?form_name=search_inv&ph=030399760", request.RequestUri.Query);
        Require(!request.Headers.Contains("Cookie") && request.Headers.Authorization is null, "Redirect credentials.");
        return Task.FromResult(Html(JsonEntity("Example", "030399760")));
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.Found, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
    Equal(2, handler.Calls);
});
await Check("Observed same-origin numeric detail redirect is followed and independently matched", async () =>
{
    using var handler = new StubHandler((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/rueckwaertssuche/")
        {
            var response = Html("", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("//www.dasoertliche.de/?form_name=detail&id=2239118714538", UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        }
        Equal("?form_name=detail&id=2239118714538", request.RequestUri.Query);
        return Task.FromResult(Html(currentDetail));
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    ReverseLookupResult result = await provider.LookupAsync("+49302290212", CancellationToken.None);
    Equal(ReverseLookupStatus.Found, result.Status);
    Equal("Juwelier Leicht, Juwelier im Hotel Adlon", result.Name);
    Equal(2, handler.Calls);
});
await Check("Detail redirect never supplies an identity without the final exact number", async () =>
{
    using var handler = new StubHandler((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/rueckwaertssuche/")
        {
            var response = Html("", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("/?form_name=detail&id=2239118714538", UriKind.Relative);
            return Task.FromResult(response);
        }
        return Task.FromResult(Html(currentDetail));
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.UnrecognizedResponse, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
});
await Check("Redirect validation rejects host, scheme, path, userinfo and changed numbers", async () =>
{
    string[] locations =
    [
        "http://www.dasoertliche.de/?form_name=search_inv&ph=030399760",
        "https://www.dasoertliche.de.example.invalid/?form_name=search_inv&ph=030399760",
        "https://user@www.dasoertliche.de/?form_name=search_inv&ph=030399760",
        "https://www.dasoertliche.de:8443/?form_name=search_inv&ph=030399760",
        "https://www.dasoertliche.de/captcha?ph=030399760",
        "https://www.dasoertliche.de/?form_name=detail&ph=030399760",
        "https://www.dasoertliche.de/?form_name=detail&id=letters",
        "https://www.dasoertliche.de/?form_name=detail&id=2239118714538&redirect=https%3A%2F%2Fexample.invalid",
        "https://www.dasoertliche.de/?form_name=detail&id=2239118714538&ph=030399760",
        "https://www.dasoertliche.de/?form_name=search_inv&ph=0301234567",
        "https://www.dasoertliche.de/?form_name=search_inv&ph=030399760&ph=0301234567",
        "https://www.dasoertliche.de/?form_name=search_inv&ph=030399760&url=https%3A%2F%2Fexample.invalid",
        "https://www.dasoertliche.de/?form_name=search_inv&ph=030399760#challenge"
    ];
    foreach (string location in locations)
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = Html("", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        });
        using var provider = new DasOertlicheReverseLookupProvider(handler);
        Equal(ReverseLookupStatus.Blocked, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
        Equal(1, handler.Calls);
    }
});
await Check("Same-origin redirect loop has a hard two-redirect limit", async () =>
{
    using var handler = new StubHandler((request, _) =>
    {
        var response = Html("", HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(request.RequestUri!.AbsolutePath == "/"
            ? "https://www.dasoertliche.de/rueckwaertssuche/?ph=030399760"
            : "https://www.dasoertliche.de/?form_name=search_inv&ph=030399760");
        return Task.FromResult(response);
    });
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    Equal(ReverseLookupStatus.Blocked, (await provider.LookupAsync(Number, CancellationToken.None)).Status);
    Equal(3, handler.Calls);
});

Console.WriteLine($"ReverseLookupProviderChecks: {passed}/{passed + failed} passed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
}

static async Task Status(string html, ReverseLookupStatus expected, string? name = null, HttpStatusCode status = HttpStatusCode.OK, string number = Number)
{
    using var handler = new StubHandler((_, _) => Task.FromResult(Html(html, status)));
    using var provider = new DasOertlicheReverseLookupProvider(handler);
    ReverseLookupResult result = await provider.LookupAsync(number, CancellationToken.None);
    Equal(expected, result.Status);
    if (name is not null) Equal(name, result.Name);
    else if (expected != ReverseLookupStatus.Found) Equal("", result.Name);
}

static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "text/html") };

static string JsonEntity(string name, string phone) => "<script type=\"application/ld+json\">"
    + System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        { ["@context"] = "https://schema.org", ["@type"] = "Organization", ["name"] = name, ["telephone"] = phone }) + "</script>";

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
}

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return callback(request, cancellationToken); }
}

sealed class TrackingStream(byte[] bytes) : Stream
{
    private int position;
    public int BytesRead => position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int length = Math.Min(count, bytes.Length - position);
        bytes.AsSpan(position, length).CopyTo(buffer.AsSpan(offset, length)); position += length; return length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int length = Math.Min(buffer.Length, bytes.Length - position);
        bytes.AsMemory(position, length).CopyTo(buffer); position += length; return ValueTask.FromResult(length);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class BlockingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
