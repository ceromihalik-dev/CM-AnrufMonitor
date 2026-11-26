using CM.AnrufMonitor.Core;
using System.Diagnostics;
using System.Text.Json;

int passed = 0, failed = 0;
void Check(bool condition, string description)
{
    if (condition) passed++;
    else { failed++; Console.WriteLine("FAIL | " + description); }
}
void Throws<T>(Action action, string description) where T : Exception
{
    try { action(); Check(false, description); }
    catch (T) { Check(true, description); }
}
async Task ThrowsAsync<T>(Func<Task> action, string description) where T : Exception
{
    try { await action(); Check(false, description); }
    catch (T) { Check(true, description); }
}

string root = Path.Combine(Path.GetTempPath(), "cm-reverse-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int storeSequence = 0;
ReverseLookupStore NewStore() => new(Path.Combine(root, (++storeSequence).ToString()));
ReverseLookupBatchOptions Fast() => new() { RequestInterval = TimeSpan.Zero };
CallRecord Call(string number = "030111110", string? known = null) => new()
{
    RemoteNumber = number, LocalNumber = "030999990", RouterName = known ?? "", StartedAt = DateTime.Now,
    Direction = CallDirection.Incoming, State = CallState.Answered
};
ReverseLookupResult Answer(ReverseLookupStatus status = ReverseLookupStatus.Found, string name = "Synthetischer Name") => new()
{
    Status = status, Name = status == ReverseLookupStatus.Found ? name : "", SourceUrl = "https://example.invalid/lookup"
};
ReverseLookupResult Cached(string number = "+4930111110", bool stale = false,
    ReverseLookupStatus status = ReverseLookupStatus.Found, string name = "Gespeicherter Name", string provider = "fixture")
{
    DateTimeOffset checkedAt = DateTimeOffset.UtcNow.AddDays(stale ? -31 : -1);
    return Answer(status, name) with
    {
        NormalizedNumber = number, Provider = provider, CheckedAtUtc = checkedAt,
        ExpiresAtUtc = checkedAt.AddDays(status == ReverseLookupStatus.Found ? 30 : 7)
    };
}
ReverseLookupBatchPlan Plan(ReverseLookupBatchService service, params CallRecord[] calls) =>
    service.CreatePlan(calls, x => x.RouterName.Length > 0, Fast());
CallRecord[] Ordered(params string[] numbers) => numbers.Select((number, index) => Call(number) with
    { StartedAt = new DateTime(2026, 11, 26, 12, 0, 0).AddSeconds(-index) }).ToArray();
CallRecord[] History(int count) => Ordered(Enumerable.Range(0, count).Select(index => "0306" + index.ToString("D5")).ToArray());

try
{
    Check(ReverseLookupNumbers.Normalize("030 111-110") == "+4930111110", "National number normalizes to exact E164");
    Check(ReverseLookupNumbers.Normalize("0049 30 111110") == "+4930111110", "International00 prefix canonicalizes");
    Check(ReverseLookupNumbers.Normalize("+49 (0)30 111110") == "+4930111110", "Optional international trunk prefix canonicalizes");
    Check(ReverseLookupNumbers.Normalize("111110") == "", "Partial local number is ineligible without configured area");
    Check(ReverseLookupNumbers.Normalize("211110", "+49", "030") == "+4930211110", "Configured area resolves complete local number");
    Check(new[] { "", "anonym", "unbekannt", "private", "**610", "#31#030111110", "610", "112", "1234" }
        .All(x => ReverseLookupNumbers.Normalize(x, "+49", "030") == ""), "Anonymous, extension and short dial strings never leave app");
    Check(new[] { "+4930\n111110", "030111110@example.invalid", "030111xxx", "++4930111110", "+49301111101234567", "+00000000" }
        .All(x => ReverseLookupNumbers.Normalize(x) == ""), "Malformed, control, partial and excessive numbers rejected");
    Check(ReverseLookupNumbers.Normalize("+43 1 2345678") == "+4312345678", "Generic normalization preserves valid nonGerman country");
    Check(ReverseLookupNumbers.Normalize("030111110", "bad49") == ""
        && ReverseLookupNumbers.Normalize("211110", "+49", "wrong030") == ""
        && ReverseLookupNumbers.Normalize("211110", "+49", "+4330") == "",
        "Invalid country or conflicting area configuration never guesses an external target");
    Check(ReverseLookupNumbers.Normalize("211110", "+49", "+49 (0)30") == "+4930211110"
        && ReverseLookupNumbers.Normalize("211110", "+49", "004930") == "+4930211110",
        "Valid international area configuration resolves exact local number");
    Check(ReverseLookupNumbers.Normalize("116117", "+49", "030") == ""
        && ReverseLookupNumbers.Normalize("11833", "+49", "030") == "", "Known service-number patterns do not become local subscriber identities");

    var store = NewStore();
    var provider = new FakeProvider((_, _) => Task.FromResult(Answer()));
    var service = new ReverseLookupBatchService(provider, store);
    CallRecord[] input = [Call(), Call("+4930111110"), Call("004930111110"), Call("030222220"),
        Call("030999990"), Call("**610"), Call("") with { IsDemo = true }, Call("030333330", "Routername"), Call("030333330")];
    var plan = Plan(service, input);
    Check(plan.SourceRecordCount == 9 && plan.TotalCount == 2 && plan.DuplicateRecordCount == 2 && plan.SkippedRecordCount == 5,
        "Planning deduplicates E164 and skips own, internal, demo and all occurrences of known names");
    Check(provider.Calls.Count == 0 && !File.Exists(Path.Combine(store.DirectoryPath, ReverseLookupStore.FileName)),
        "Reviewable plan sends no requests and creates no result file");
    var reports = new List<ReverseLookupProgress>();
    var summary = await service.RunAsync(plan, new InlineProgress(reports.Add));
    Check(summary.FoundCount == 2 && summary.CompletedCount == 2 && summary.RequestedCount == 2 && provider.Calls.Count == 2,
        "Batch queries each eligible full number once");
    Check(store.Load().Count == 2 && reports.All(x => x.Result?.Provider == "fixture"), "Completed names persist with provider provenance");
    Check(reports.Select(x => x.CompletedCount).SequenceEqual([1, 2]) && reports.All(x => x.TotalCount == 2),
        "Per-result progress counts monotonically reach reviewed total");
    Check(store.Load().All(x => x.CheckedAtUtc.Offset == TimeSpan.Zero && x.ExpiresAtUtc - x.CheckedAtUtc == TimeSpan.FromDays(30)),
        "Found results receive UTC timestamps and30day refresh age");
    var secondPlan = Plan(service, input);
    Check(secondPlan.CachedCount == 2 && secondPlan.RequestCount == 0, "Fresh cache avoids repeated plan requests");
    var repeated = await service.RunAsync(secondPlan);
    Check(repeated.CachedCount == 2 && repeated.RequestedCount == 0 && provider.Calls.Count == 2,
        "Repeated run uses cached names without network or throttle");
    var restarted = new ReverseLookupStore(store.DirectoryPath);
    Check(restarted.Load().Select(x => x.Name).All(x => x == "Synthetischer Name"), "Names survive new store instance and app restart");

    var selected = Plan(service, Call("030777770"));
    var allHistory = Plan(service, Call("030777770"), Call("030888880"));
    Check(selected.TotalCount == 1 && allHistory.TotalCount == 2, "Caller explicitly selects allhistory or selection scope");
    var large = Plan(service, Enumerable.Range(0, 2100).Select(i => Call("0304" + i.ToString("D6"))).ToArray());
    Check(large.TotalCount == CallTracker.MaximumRecords && large.SourceRecordCount == 2100 && large.SkippedRecordCount == 100,
        "History plan is bounded to2000 latest records");

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer())) { Support = n => n.StartsWith("+49", StringComparison.Ordinal) };
    service = new ReverseLookupBatchService(provider, store);
    plan = Plan(service, Call("+4312345678"), Call("+441234567890"), Call("+4312345678"));
    summary = await service.RunAsync(plan);
    Check(plan.TotalCount == 0 && plan.UnsupportedCount == 2 && summary.UnsupportedCount == 2 && summary.RequestedCount == 0
        && provider.Calls.Count == 0, "Unsupported countries are excluded before pacing and HTTPattempt counting");

    store = NewStore(); store.Save([Cached(stale: true)]);
    provider = new FakeProvider((_, _) => Task.FromResult(Answer(ReverseLookupStatus.TemporaryError)));
    service = new ReverseLookupBatchService(provider, store);
    plan = Plan(service, Call());
    Check(plan.RequestCount == 1 && store.Load().Single().Name == "Gespeicherter Name", "Stale name stays stored while next manual plan schedules refresh");
    summary = await service.RunAsync(plan);
    Check(summary.ErrorCount == 1 && store.Load().Single().Name == "Gespeicherter Name", "Transient refresh failure preserves recognized stale name");
    provider.Handler = (_, _) => Task.FromResult(Answer(ReverseLookupStatus.NotFound));
    summary = await service.RunAsync(Plan(service, Call()));
    Check(store.Load().Single().Status == ReverseLookupStatus.NotFound && store.Load().Single().Name.Length == 0,
        "Definitive noresult replaces stale name instead of retaining wrong identity");
    Check(store.Load().Single().ExpiresAtUtc - store.Load().Single().CheckedAtUtc == TimeSpan.FromDays(7), "Negative cache expires after7days");
    int previousCalls = provider.Calls.Count;
    await service.RunAsync(Plan(service, Call()));
    Check(provider.Calls.Count == previousCalls, "Fresh definitive noresult is not queried again");
    store.Save([Cached(stale: true, status: ReverseLookupStatus.NotFound)]);
    Check(Plan(service, Call()).RequestCount == 1, "Expired negative result becomes eligible at nextmanualbatch");
    store.Save([Cached(stale: true)]);
    provider.Handler = (_, _) => Task.FromResult(Answer(ReverseLookupStatus.Ambiguous));
    summary = await service.RunAsync(Plan(service, Call()));
    Check(summary.AmbiguousCount == 1 && store.Load().Count == 0, "Definitive ambiguity invalidates earlier unique name without choosing identity");

    store.Save([Cached()]);
    provider.Handler = (_, _) => Task.FromResult(Answer(name: "Neuer Name"));
    var forced = service.CreatePlan([Call()], _ => false, Fast() with { RefreshCached = true });
    Check(forced.RequestCount == 1 && forced.CachedCount == 0, "Explicit refresh can bypass fresh cache");
    summary = await service.RunAsync(forced);
    Check(store.Load().Single().Name == "Neuer Name", "A successful refresh replaces old name atomically");

    store = NewStore(); store.Save([Cached(provider: "another-provider")]);
    provider = new FakeProvider((_, _) => Task.FromResult(Answer()));
    service = new ReverseLookupBatchService(provider, store);
    Check(Plan(service, Call()).RequestCount == 1, "Provider-specific cache never reuses another directory result as its own");
    await service.RunAsync(Plan(service, Call()));
    Check(store.Load().Count == 2, "Independent provider provenance survives cache updates");

    foreach (ReverseLookupStatus stop in new[] { ReverseLookupStatus.RateLimited, ReverseLookupStatus.AuthRequired, ReverseLookupStatus.Blocked })
    {
        store = NewStore(); store.Save([Cached(stale: true)]);
        provider = new FakeProvider((_, _) => Task.FromResult(Answer(stop) with { RetryAfter = TimeSpan.FromMinutes(5) }));
        service = new ReverseLookupBatchService(provider, store);
        summary = await service.RunAsync(Plan(service, Call(), Call("030222220")));
        Check(summary.StopStatus == stop && summary.CompletedCount == 1 && provider.Calls.Count == 1,
            stop + " stops queue without retrying or querying next number");
        Check(store.Load().Single().Name == "Gespeicherter Name" && summary.Results[0].RetryAfter == TimeSpan.FromMinutes(5),
            stop + " preserves prior name and returns retry guidance without caching error");
    }

    store = NewStore();
    int answerIndex = 0;
    ReverseLookupResult[] invalidAnswers = [Answer() with { NormalizedNumber = "+4930777777" },
        Answer() with { Provider = "different" }, Answer(name: " "), Answer() with { SourceUrl = "javascript:alert(1)" },
        Answer(name: new string('x', 301))];
    provider = new FakeProvider((_, _) => Task.FromResult(invalidAnswers[answerIndex++]));
    service = new ReverseLookupBatchService(provider, store);
    summary = await service.RunAsync(Plan(service, Enumerable.Range(0, 5).Select(i => Call("03066666" + i)).ToArray()));
    Check(summary.ErrorCount == 5 && store.Load().Count == 0 && summary.Results.All(x => x.Status == ReverseLookupStatus.TemporaryError),
        "Wrong-number, wrong-provider, blank-name, unsafe-URL and oversized-name responses cannot become identity cache");
    provider.Handler = (_, _) => Task.FromResult(Answer(name: "  Test\tName\nFirma\u202e "));
    summary = await service.RunAsync(Plan(service, Call()));
    Check(store.Load().Single().Name == "Test Name Firma", "Whitespace and bidi formatting controls cannot spoof displayed names");
    provider.Handler = (_, _) => throw new HttpRequestException("SECRET fake response body");
    summary = await service.RunAsync(Plan(service, Call("030222220")));
    Check(summary.ErrorCount == 1 && !summary.Results[0].Message.Contains("SECRET", StringComparison.Ordinal)
        && store.Load().Count == 1, "Provider exception becomes bounded message without persisting secret/error as negative result");

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer()));
    service = new ReverseLookupBatchService(provider, store);
    plan = service.CreatePlan([Call(), Call("030222220"), Call("030333330")], _ => false,
        Fast() with { RequestInterval = TimeSpan.FromMilliseconds(130) });
    summary = await service.RunAsync(plan);
    Check(provider.Started.Zip(provider.Started.Skip(1), (left, right) => Stopwatch.GetElapsedTime(left, right))
        .All(interval => interval >= TimeSpan.FromMilliseconds(110)), "Sequential requests honor configured minimum pacing");
    Check(provider.MaximumConcurrent == 1, "Batch never sends concurrent provider requests");

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer()));
    service = new ReverseLookupBatchService(provider, store);
    using (var cancellation = new CancellationTokenSource())
    {
        summary = await service.RunAsync(Plan(service, Call(), Call("030222220")),
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token);
        Check(summary.Cancelled && summary.CompletedCount == 1 && provider.Calls.Count == 1 && store.Load().Count == 1,
            "Cancellation between results keeps completed cache and sends no further number");
    }

    store = NewStore();
    var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    provider = new FakeProvider(async (_, token) =>
    {
        lookupStarted.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Answer();
    });
    service = new ReverseLookupBatchService(provider, store);
    using (var cancellation = new CancellationTokenSource())
    {
        Task<ReverseLookupBatchSummary> active = service.RunAsync(Plan(service, Call()), cancellationToken: cancellation.Token);
        await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Plan(service, Call())), "A second simultaneous batch is rejected");
        cancellation.Cancel();
        summary = await active.WaitAsync(TimeSpan.FromSeconds(3));
        Check(summary.Cancelled && summary.CompletedCount == 0 && store.Load().Count == 0,
            "Cancellation during pending provider request finishes without phantom results");
    }
    provider.Handler = (_, _) => Task.FromResult(Answer());
    summary = await service.RunAsync(Plan(service, Call()));
    Check(summary.FoundCount == 1, "Service can run again after cancellation releases concurrency guard");

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer()));
    service = new ReverseLookupBatchService(provider, store);
    var firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var cancellation = new CancellationTokenSource())
    {
        Task<ReverseLookupBatchSummary> active = service.RunAsync(service.CreatePlan([Call(), Call("030222220")], _ => false,
            Fast() with { RequestInterval = TimeSpan.FromSeconds(2) }), new InlineProgress(_ => firstResult.TrySetResult()), cancellation.Token);
        await firstResult.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(30); cancellation.Cancel();
        summary = await active.WaitAsync(TimeSpan.FromSeconds(3));
        Check(summary.Cancelled && provider.Calls.Count == 1 && store.Load().Count == 1, "Cancellation interrupts throttle pause promptly");
    }
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel(); int callsBefore = provider.Calls.Count;
        summary = await service.RunAsync(Plan(service, Call("030555550")), cancellationToken: cancellation.Token);
        Check(summary.Cancelled && summary.CompletedCount == 0 && provider.Calls.Count == callsBefore, "Precancelled batch sends nothing");
    }

    // Regression: one unrecognized page at item22 must not strand the remaining history.
    Check((int)ReverseLookupStatus.Found == 0 && (int)ReverseLookupStatus.Unsupported == 7
        && (int)ReverseLookupStatus.UnrecognizedResponse == 8, "New status is appended without changing existing enum values");
    store = NewStore();
    CallRecord[] screenshotHistory = History(157);
    string unknownNumber = ReverseLookupNumbers.Normalize(screenshotHistory[21].RemoteNumber);
    store.Save(screenshotHistory.Take(8).Select(call => Cached(ReverseLookupNumbers.Normalize(call.RemoteNumber), status: ReverseLookupStatus.NotFound)));
    provider = new FakeProvider((number, _) => Task.FromResult(Answer(number == unknownNumber
        ? ReverseLookupStatus.UnrecognizedResponse : ReverseLookupStatus.NotFound)))
        { Support = number => number.StartsWith("+49", StringComparison.Ordinal) };
    service = new ReverseLookupBatchService(provider, store);
    reports.Clear();
    plan = Plan(service, [.. screenshotHistory, Call("+4312345678")]);
    summary = await service.RunAsync(plan, new InlineProgress(reports.Add));
    Check(summary.CompletedCount == 157 && summary.TotalCount == 157 && summary.StopStatus is null
        && summary.RequestedCount == 149 && summary.CachedCount == 8 && summary.UnsupportedCount == 1,
        "One unknown response at22of157 continues through all remaining eligible numbers with8cachehits");
    Check(summary.UnrecognizedCount == 1 && summary.NotFoundCount == 156 && summary.ErrorCount == 0
        && summary.FoundCount == 0 && !reports[21].IsStopped,
        "Unrecognized response has its own count and is never called a miss or an access block");
    Check(store.Load().Count == 156 && store.Load().All(result => result.NormalizedNumber != unknownNumber),
        "An unknown response creates neither a negative cache entry nor a fabricated name");
    Check(summary.Results.Count == summary.CompletedCount && summary.RemainingNumbers.Count == 0
        && summary.UnresolvedResults.Count == 2
        && summary.UnresolvedResults.Any(result => result.NormalizedNumber == unknownNumber && result.Status == ReverseLookupStatus.UnrecognizedResponse)
        && summary.UnresolvedResults.Any(result => result.NormalizedNumber == "+4312345678" && result.Status == ReverseLookupStatus.Unsupported),
        "Per-number unresolved details include failed parsing and preflight exclusion without inflating completion counts");
    int beforeUnknownRetry = provider.Calls.Count;
    plan = Plan(service, screenshotHistory);
    Check(plan.CachedCount == 156 && plan.RequestCount == 1, "Next manual run reuses completed cache and leaves unknown result eligible");
    summary = await service.RunAsync(plan);
    Check(summary.StopStatus is null && summary.CompletedCount == 157 && summary.RequestedCount == 1
        && summary.UnrecognizedCount == 1 && provider.Calls.Count == beforeUnknownRetry + 1,
        "The same isolated bad number cannot immediately stop the next manual run");

    store = NewStore(); store.Save([Cached(stale: true)]);
    provider = new FakeProvider((number, _) => Task.FromResult(Answer(number == "+4930111110"
        ? ReverseLookupStatus.UnrecognizedResponse : ReverseLookupStatus.Found)));
    service = new ReverseLookupBatchService(provider, store);
    summary = await service.RunAsync(Plan(service, Ordered("030111110", "030222220")));
    Check(summary.CompletedCount == 2 && summary.FoundCount == 1 && summary.UnrecognizedCount == 1
        && store.Load().Single(result => result.NormalizedNumber == "+4930111110").Name == "Gespeicherter Name",
        "Unrecognized refresh preserves an existing name while later valid names are still found");
    Check(summary.UnresolvedResults.Single().Name.Length == 0 && summary.UnresolvedResults.Single().Message.Length is > 0 and <= 500,
        "Unresolved detail contains a bounded explanation but no replacement identity");

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer(ReverseLookupStatus.UnrecognizedResponse)));
    service = new ReverseLookupBatchService(provider, store);
    CallRecord[] fourUnknown = History(4);
    reports.Clear();
    summary = await service.RunAsync(Plan(service, fourUnknown), new InlineProgress(reports.Add));
    Check(summary.StopStatus == ReverseLookupStatus.UnrecognizedResponse && summary.CompletedCount == 3
        && summary.RequestedCount == 3 && provider.Calls.Count == 3 && summary.UnrecognizedCount == 3,
        "Three consecutive unknown network responses stop a potentially broken source before the fourth request");
    Check(reports.Select(report => report.IsStopped).SequenceEqual([false, false, true])
        && summary.RemainingNumbers.SequenceEqual([ReverseLookupNumbers.Normalize(fourUnknown[3].RemoteNumber)])
        && summary.UnresolvedResults.Count == 3 && summary.Results.Count == 3,
        "Stop progress and remaining-number diagnostics identify exactly what was attempted and what remains");
    Check(summary.Message.Contains("vorsorglich", StringComparison.Ordinal) && store.Load().Count == 0,
        "Source guard explains cautious pause without caching failed parser responses");

    store = NewStore();
    CallRecord[] mixedWithCache = History(6);
    store.Save([Cached(ReverseLookupNumbers.Normalize(mixedWithCache[1].RemoteNumber)),
        Cached(ReverseLookupNumbers.Normalize(mixedWithCache[3].RemoteNumber), status: ReverseLookupStatus.NotFound)]);
    provider = new FakeProvider((_, _) => Task.FromResult(Answer(ReverseLookupStatus.UnrecognizedResponse)));
    service = new ReverseLookupBatchService(provider, store);
    summary = await service.RunAsync(Plan(service, mixedWithCache));
    Check(summary.StopStatus == ReverseLookupStatus.UnrecognizedResponse && summary.CompletedCount == 5
        && summary.RequestedCount == 3 && summary.CachedCount == 2 && summary.RemainingNumbers.Count == 1,
        "Cached positive and negative results never reset an unknown network-response streak");

    store = NewStore();
    var statusSequence = new Queue<ReverseLookupStatus>([ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.TemporaryError,
        ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.TemporaryError, ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.Found]);
    provider = new FakeProvider((_, _) => Task.FromResult(Answer(statusSequence.Dequeue())));
    service = new ReverseLookupBatchService(provider, store);
    summary = await service.RunAsync(Plan(service, History(6)));
    Check(summary.StopStatus == ReverseLookupStatus.UnrecognizedResponse && summary.UnrecognizedCount == 3
        && summary.ErrorCount == 2 && summary.CompletedCount == 5 && provider.Calls.Count == 5,
        "Transient network errors do not pretend the source recovered between unrecognized responses");

    foreach (ReverseLookupStatus recognized in new[] { ReverseLookupStatus.Found, ReverseLookupStatus.NotFound, ReverseLookupStatus.Ambiguous })
    {
        store = NewStore();
        statusSequence = new Queue<ReverseLookupStatus>([ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.UnrecognizedResponse,
            recognized, ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.Found]);
        provider = new FakeProvider((_, _) => Task.FromResult(Answer(statusSequence.Dequeue())));
        service = new ReverseLookupBatchService(provider, store);
        summary = await service.RunAsync(Plan(service, History(6)));
        Check(summary.StopStatus is null && summary.CompletedCount == 6 && summary.UnrecognizedCount == 4 && summary.ErrorCount == 0,
            "A recognized network " + recognized + " breaks the source-failure streak");
    }

    store = NewStore();
    statusSequence = new Queue<ReverseLookupStatus>([ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.Unsupported,
        ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.Unsupported, ReverseLookupStatus.UnrecognizedResponse, ReverseLookupStatus.Found]);
    provider = new FakeProvider((_, _) => Task.FromResult(Answer(statusSequence.Dequeue())));
    service = new ReverseLookupBatchService(provider, store);
    summary = await service.RunAsync(Plan(service, History(6)));
    Check(summary.StopStatus == ReverseLookupStatus.UnrecognizedResponse && summary.CompletedCount == 5
        && summary.RequestedCount == 3 && summary.UnsupportedCount == 2 && summary.RemainingNumbers.Count == 1,
        "Local unsupported responses neither reset network streak nor count as internet requests");

    foreach (ReverseLookupStatus realStop in new[] { ReverseLookupStatus.Blocked, ReverseLookupStatus.RateLimited, ReverseLookupStatus.AuthRequired })
    {
        store = NewStore();
        statusSequence = new Queue<ReverseLookupStatus>([ReverseLookupStatus.UnrecognizedResponse, realStop, ReverseLookupStatus.Found]);
        provider = new FakeProvider((_, _) => Task.FromResult(Answer(statusSequence.Dequeue())));
        service = new ReverseLookupBatchService(provider, store);
        summary = await service.RunAsync(Plan(service, History(3)));
        Check(summary.StopStatus == realStop && summary.CompletedCount == 2 && provider.Calls.Count == 2
            && summary.UnrecognizedCount == 1 && summary.ErrorCount == 1 && summary.RemainingNumbers.Count == 1,
            "A real " + realStop + " still stops immediately after an isolated unknown response without retry");
    }

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer(ReverseLookupStatus.UnrecognizedResponse) with
        { Name = "Not a confirmed identity", Message = "<html><body>Untrusted private response payload</body></html>" }));
    service = new ReverseLookupBatchService(provider, store);
    summary = await service.RunAsync(Plan(service, Call()));
    Check(summary.UnresolvedResults.Single().Name.Length == 0
        && !summary.UnresolvedResults.Single().Message.Contains("<", StringComparison.Ordinal)
        && !summary.UnresolvedResults.Single().Message.Contains("private response", StringComparison.Ordinal),
        "Failure diagnostics expose neither raw HTML nor an unconfirmed name");
    Throws<InvalidDataException>(() => store.Save([Cached() with { Status = ReverseLookupStatus.UnrecognizedResponse }]),
        "Unrecognized response cannot be persisted through the cache API");
    Throws<ArgumentException>(() => service.CreatePlan([Call()], _ => false, Fast() with { MaximumConsecutiveUnrecognizedResponses = 0 }),
        "Source-failure stop guard cannot be accidentally disabled by invalid configuration");

    store = NewStore();
    provider = new FakeProvider((_, _) => Task.FromResult(Answer()));
    service = new ReverseLookupBatchService(provider, store);
    CallRecord[] cancellableHistory = History(3);
    using (var cancellation = new CancellationTokenSource())
    {
        summary = await service.RunAsync(Plan(service, cancellableHistory),
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token);
        Check(summary.Cancelled && summary.CompletedCount == 1 && summary.RemainingNumbers.SequenceEqual(
            cancellableHistory.Skip(1).Select(call => ReverseLookupNumbers.Normalize(call.RemoteNumber)))
            && summary.UnresolvedResults.Count == 0,
            "Cancellation reports untouched remaining numbers separately from genuine failed results");
    }

    store = NewStore();
    string path = Path.Combine(store.DirectoryPath, ReverseLookupStore.FileName);
    File.WriteAllText(path, "{ definitely broken source bytes");
    string[] warnings = [];
    store.Warning += warning => warnings = [.. warnings, warning];
    Check(store.Load().Count == 0 && Directory.GetFiles(store.DirectoryPath, "*.broken").Length == 1
        && File.ReadAllText(Directory.GetFiles(store.DirectoryPath, "*.broken")[0]) == "{ definitely broken source bytes",
        "Malformed cache original is preserved byte-for-byte before fallback");
    Check(store.Warnings.Count == 1 && warnings.Length == 1, "Malformed cache reports visible warning");
    store.Save([Cached()]);
    Check(store.Load().Count == 1 && Directory.GetFiles(store.DirectoryPath, "*.broken").Length == 1,
        "Recovered cache can save while broken original remains retained");

    store = NewStore(); path = Path.Combine(store.DirectoryPath, ReverseLookupStore.FileName);
    File.WriteAllText(path, "[not the expected envelope]");
    store.Save([Cached()]);
    Check(store.Load().Count == 1 && Directory.GetFiles(store.DirectoryPath, "*.broken").Length == 1,
        "Saving before Load also preserves an existing corrupt cache");
    string goodBytes = File.ReadAllText(path);
    Throws<InvalidDataException>(() => store.Save([Cached() with { SourceUrl = "file:///private" }]), "Unsafe persisted source links are rejected");
    Check(File.ReadAllText(path) == goodBytes, "Invalid Save input cannot overwrite good cached names");
    Throws<InvalidDataException>(() => store.Save([Cached(), Cached()]), "Duplicate provider-number keys rejected in cache file");
    Throws<InvalidDataException>(() => store.Save([Cached() with { Status = ReverseLookupStatus.TemporaryError }]), "Transient statuses cannot be persisted as permanent misses");
    Throws<InvalidDataException>(() => store.Save([Cached() with { Name = "Name\u202e" }]), "Persisted bidi formatting controls are rejected");
    Throws<InvalidDataException>(() => store.Save([Cached() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(10) }]), "Malformed longlived cache does not suppress refresh indefinitely");
    Throws<InvalidDataException>(() => store.Save(Enumerable.Range(0, ReverseLookupStore.MaximumResults + 1)
        .Select(i => Cached("+49304" + i.ToString("D6")))), "Cache size limit prevents unbounded stored results");
    Check(File.ReadAllText(path) == goodBytes && Directory.GetFiles(store.DirectoryPath, "*.tmp").Length == 0,
        "Failed saves leave original bytes and no temporary files");
    File.WriteAllText(Path.Combine(store.DirectoryPath, "history.json"), "history sentinel");
    File.WriteAllText(Path.Combine(store.DirectoryPath, "contacts.json"), "contacts sentinel");
    store.Save([Cached() with { Message = "not persisted", RetryAfter = TimeSpan.FromMinutes(3) }]);
    Check(!File.ReadAllText(path).Contains("not persisted", StringComparison.Ordinal) && store.Load()[0].RetryAfter is null,
        "Cache retains name/provenance only, not provider diagnostic payloads");
    Check(File.ReadAllText(Path.Combine(store.DirectoryPath, "history.json")) == "history sentinel"
        && File.ReadAllText(Path.Combine(store.DirectoryPath, "contacts.json")) == "contacts sentinel",
        "Internet cache never writes caller history or trusted contacts");

    store = NewStore(); path = Path.Combine(store.DirectoryPath, ReverseLookupStore.FileName);
    Directory.CreateDirectory(path);
    bool saveFailed = false;
    try { store.Save([Cached()]); }
    // Replacing a directory reports IO failure on Unix and access denied on Windows.
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveFailed = true; }
    Check(saveFailed, "An unwritable target propagates storage failure");
    Check(Directory.Exists(path) && Directory.GetFiles(store.DirectoryPath, "*.tmp").Length == 0,
        "Failed atomic replacement preserves destination and removes temporary output");

    service = new ReverseLookupBatchService(new FakeProvider((_, _) => Task.FromResult(Answer())), NewStore());
    Throws<ArgumentException>(() => service.CreatePlan([Call()], _ => false, Fast() with { RequestInterval = TimeSpan.FromSeconds(-1) }),
        "Invalid pacing configuration is rejected before requests");
    Throws<ArgumentException>(() => service.CreatePlan([Call()], _ => false, Fast() with { FoundTtl = TimeSpan.Zero }),
        "Invalid cache lifetime cannot create permanent or instantlyambiguous cache entries");
    var anotherService = new ReverseLookupBatchService(new FakeProvider((_, _) => Task.FromResult(Answer())) { Id = "other" }, NewStore());
    await ThrowsAsync<ArgumentException>(() => anotherService.RunAsync(Plan(service, Call())), "A plan cannot be run against a different provider");
}
catch (Exception error)
{
    failed++; Console.WriteLine("FAIL | Unhandled verification exception: " + error);
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine($"ReverseLookupChecks: {passed}/{passed + failed} PASS. Synthetic providers only; no external lookup.");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class FakeProvider(Func<string, CancellationToken, Task<ReverseLookupResult>> handler) : IReverseLookupProvider
{
    private int concurrent;
    public string Id { get; init; } = "fixture";
    public Func<string, CancellationToken, Task<ReverseLookupResult>> Handler { get; set; } = handler;
    public Func<string, bool> Support { get; init; } = _ => true;
    public List<string> Calls { get; } = [];
    public List<long> Started { get; } = [];
    public int MaximumConcurrent { get; private set; }
    public bool SupportsNumber(string normalizedNumber) => Support(normalizedNumber);
    public async Task<ReverseLookupResult> LookupAsync(string normalizedNumber, CancellationToken cancellationToken)
    {
        Calls.Add(normalizedNumber); Started.Add(Stopwatch.GetTimestamp());
        MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
        try { return await Handler(normalizedNumber, cancellationToken); }
        finally { Interlocked.Decrement(ref concurrent); }
    }
}

sealed class InlineProgress(Action<ReverseLookupProgress> action) : IProgress<ReverseLookupProgress>
{
    public void Report(ReverseLookupProgress value) => action(value);
}
