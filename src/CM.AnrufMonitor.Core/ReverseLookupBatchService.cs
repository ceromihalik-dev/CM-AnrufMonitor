using System.Diagnostics;
using System.Globalization;

namespace CM.AnrufMonitor.Core;

/// <summary>Explicit, sequential lookup of one history snapshot. It never starts background searches.</summary>
public sealed class ReverseLookupBatchService
{
    private readonly IReverseLookupProvider provider;
    private readonly ReverseLookupStore store;
    private readonly string providerId;
    private int running;

    public ReverseLookupBatchService(IReverseLookupProvider provider, ReverseLookupStore store)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(store);
        if (!ReverseLookupStore.ValidProvider(provider.Id)) throw new ArgumentException("Ungültige Anbieterkennung.", nameof(provider));
        this.provider = provider;
        this.store = store;
        providerId = provider.Id;
    }

    public ReverseLookupBatchPlan CreatePlan(IReadOnlyList<CallRecord> explicitSnapshot,
        Func<CallRecord, bool> hasPreferredName, ReverseLookupBatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(explicitSnapshot);
        ArgumentNullException.ThrowIfNull(hasPreferredName);
        options ??= new ReverseLookupBatchOptions();
        ValidateOptions(options);
        // Match the application history limit and look up the most recent calls first.
        CallRecord[] snapshot = explicitSnapshot.Where(x => x is not null)
            .OrderByDescending(x => x.StartedAt).Take(CallTracker.MaximumRecords).ToArray();
        var ownNumbers = snapshot.Where(x => !x.IsDemo)
            .Select(x => ReverseLookupNumbers.Normalize(x.LocalNumber, options.CountryCode, options.AreaCode))
            .Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var candidates = snapshot.Select(call => (Call: call,
            Number: call.IsDemo ? "" : ReverseLookupNumbers.Normalize(call.RemoteNumber, options.CountryCode, options.AreaCode),
            Known: !call.IsDemo && hasPreferredName(call))).ToArray();
        // A known name on any occurrence prevents sending that same number from another occurrence.
        var knownNumbers = candidates.Where(x => x.Known && x.Number.Length > 0)
            .Select(x => x.Number).ToHashSet(StringComparer.Ordinal);
        var numbers = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        int skipped = explicitSnapshot.Count - snapshot.Length, duplicates = 0;
        foreach (var entry in candidates)
        {
            if (entry.Call.IsDemo || entry.Number.Length == 0 || ownNumbers.Contains(entry.Number) || knownNumbers.Contains(entry.Number))
            { skipped++; continue; }
            if (!provider.SupportsNumber(entry.Number))
            { unsupported.Add(entry.Number); skipped++; continue; }
            if (!unique.Add(entry.Number)) { duplicates++; continue; }
            numbers.Add(entry.Number);
        }
        var cache = store.Load().ToDictionary(x => ReverseLookupStore.Key(x.Provider, x.NormalizedNumber), StringComparer.Ordinal);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pending = new List<string>();
        var cached = new List<ReverseLookupResult>();
        foreach (string number in numbers)
        {
            if (!options.RefreshCached && cache.TryGetValue(ReverseLookupStore.Key(providerId, number), out ReverseLookupResult? result)
                && result.ExpiresAtUtc > now) cached.Add(result);
            else pending.Add(number);
        }
        return new ReverseLookupBatchPlan(providerId, options, numbers, pending, cached,
            explicitSnapshot.Count, skipped, duplicates, unsupported);
    }

    public async Task<ReverseLookupBatchSummary> RunAsync(ReverseLookupBatchPlan plan,
        IProgress<ReverseLookupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.Provider, providerId, StringComparison.Ordinal))
            throw new ArgumentException("Der Suchplan gehört zu einem anderen Anbieter.", nameof(plan));
        ValidateOptions(plan.Options);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Es läuft bereits eine Rückwärtssuche.");
        try
        {
            return await RunCoreAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task<ReverseLookupBatchSummary> RunCoreAsync(ReverseLookupBatchPlan plan,
        IProgress<ReverseLookupProgress>? progress, CancellationToken cancellationToken)
    {
        var cache = store.Load().ToDictionary(x => ReverseLookupStore.Key(x.Provider, x.NormalizedNumber), StringComparer.Ordinal);
        var results = new List<ReverseLookupResult>();
        int requested = 0, cached = 0, found = 0, notFound = 0, ambiguous = 0, errors = 0, unrecognized = 0;
        int consecutiveUnrecognized = 0;
        int unsupported = plan.UnsupportedCount;
        bool cancelled = false;
        ReverseLookupStatus? stopped = null;
        string stopMessage = "";
        long? previousRequest = null;

        try
        {
            foreach (string number in plan.Numbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = ReverseLookupStore.Key(providerId, number);
                bool fromCache = !plan.Options.RefreshCached && cache.TryGetValue(key, out ReverseLookupResult? existing)
                    && existing.ExpiresAtUtc > DateTimeOffset.UtcNow;
                ReverseLookupResult result;
                if (fromCache)
                {
                    result = cache[key];
                    cached++;
                }
                else
                {
                    if (previousRequest.HasValue)
                    {
                        TimeSpan wait = plan.Options.RequestInterval - Stopwatch.GetElapsedTime(previousRequest.Value);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    long started = Stopwatch.GetTimestamp();
                    requested++;
                    try
                    {
                        result = await provider.LookupAsync(number, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        // Provider messages can contain complete responses or secrets; do not persist them.
                        result = new ReverseLookupResult
                        {
                            Status = ReverseLookupStatus.TemporaryError,
                            Message = error is OperationCanceledException or TimeoutException
                                ? "Der Anbieter hat nicht rechtzeitig geantwortet."
                                : "Die Rückwärtssuche konnte gerade nicht ausgeführt werden."
                        };
                    }
                    result = PrepareResult(result, number, plan.Options);
                    if (result.Status == ReverseLookupStatus.Unsupported) requested--;
                    else previousRequest = started;
                    // Only a recognized network response proves that the source is still usable.
                    // Cached results, local exclusions and transient errors do not clear this streak.
                    if (result.Status == ReverseLookupStatus.UnrecognizedResponse) consecutiveUnrecognized++;
                    else if (result.Status is ReverseLookupStatus.Found or ReverseLookupStatus.NotFound or ReverseLookupStatus.Ambiguous)
                        consecutiveUnrecognized = 0;
                    if (result.Status is ReverseLookupStatus.Found or ReverseLookupStatus.NotFound)
                    {
                        cache[key] = result with { Message = "", RetryAfter = null };
                        Persist(cache);
                    }
                    else if (result.Status == ReverseLookupStatus.Ambiguous && cache.Remove(key))
                    {
                        // A definitive ambiguous answer invalidates an earlier unique name.
                        Persist(cache);
                    }
                }

                results.Add(result);
                switch (result.Status)
                {
                    case ReverseLookupStatus.Found: found++; break;
                    case ReverseLookupStatus.NotFound: notFound++; break;
                    case ReverseLookupStatus.Ambiguous: ambiguous++; break;
                    case ReverseLookupStatus.UnrecognizedResponse: unrecognized++; break;
                    case ReverseLookupStatus.Unsupported: unsupported++; break;
                    default: errors++; break;
                }
                if (result.Status is ReverseLookupStatus.RateLimited or ReverseLookupStatus.AuthRequired or ReverseLookupStatus.Blocked)
                {
                    stopped = result.Status;
                    stopMessage = result.Message;
                }
                else if (consecutiveUnrecognized >= plan.Options.MaximumConsecutiveUnrecognizedResponses)
                {
                    stopped = ReverseLookupStatus.UnrecognizedResponse;
                    stopMessage = $"{consecutiveUnrecognized} Antworten des Anbieters konnten nicht ausgewertet werden, ohne dass dazwischen ein verwertbares Online-Ergebnis vorlag. "
                        + "Die Suche wurde vorsorglich angehalten. Die betroffenen Rufnummern sind zur manuellen Prüfung aufgeführt."
                        + (results.Count < plan.TotalCount ? " Weitere Rufnummern wurden noch nicht abgefragt." : "");
                }
                progress?.Report(new ReverseLookupProgress
                {
                    TotalCount = plan.TotalCount, CompletedCount = results.Count, RequestedCount = requested,
                    CachedCount = cached, FoundCount = found, NotFoundCount = notFound, AmbiguousCount = ambiguous,
                    ErrorCount = errors, UnrecognizedCount = unrecognized, UnsupportedCount = unsupported, Result = result, FromCache = fromCache,
                    IsStopped = stopped.HasValue
                });
                if (stopped.HasValue) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; }

        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        ReverseLookupResult[] unresolved = results.Where(x => x.Status is not (ReverseLookupStatus.Found or ReverseLookupStatus.NotFound))
            .Concat(plan.UnsupportedNumbers.Select(number => new ReverseLookupResult
            {
                NormalizedNumber = number, Provider = providerId, Status = ReverseLookupStatus.Unsupported,
                CheckedAtUtc = checkedAt, ExpiresAtUtc = checkedAt,
                Message = "Der aktuelle Anbieter unterstützt diese Rufnummer nicht (z. B. ausländische Landesvorwahl). Es wurde keine Internetabfrage gesendet; dies ist kein Abruffehler."
            })).ToArray();
        return new ReverseLookupBatchSummary
        {
            TotalCount = plan.TotalCount, CompletedCount = results.Count, RequestedCount = requested,
            CachedCount = cached, FoundCount = found, NotFoundCount = notFound, AmbiguousCount = ambiguous,
            ErrorCount = errors, UnrecognizedCount = unrecognized, UnsupportedCount = unsupported, Results = Array.AsReadOnly(results.ToArray()),
            UnresolvedResults = Array.AsReadOnly(unresolved), RemainingNumbers = Array.AsReadOnly(plan.Numbers.Skip(results.Count).ToArray()),
            Cancelled = cancelled, StopStatus = stopped, Message = cancelled ? "Rückwärtssuche abgebrochen." : stopMessage
        };
    }

    private ReverseLookupResult PrepareResult(ReverseLookupResult? result, string number, ReverseLookupBatchOptions options)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (result is null || !Enum.IsDefined(result.Status)
            || !string.IsNullOrEmpty(result.NormalizedNumber) && result.NormalizedNumber != number
            || !string.IsNullOrEmpty(result.Provider) && result.Provider != providerId
            || !ReverseLookupStore.ValidSourceUrl(result.SourceUrl))
            return InvalidResult(number, now);
        string name = CleanText(result.Name, 300);
        if (result.Status == ReverseLookupStatus.Found && name.Length == 0) return InvalidResult(number, now);
        if (result.Status != ReverseLookupStatus.Found) name = "";
        TimeSpan ttl = result.Status == ReverseLookupStatus.Found ? options.FoundTtl : options.NotFoundTtl;
        return result with
        {
            NormalizedNumber = number, Provider = providerId, Name = name,
            CheckedAtUtc = now, ExpiresAtUtc = now + ttl,
            Message = CleanDiagnosticMessage(result.Message, result.Status),
            RetryAfter = result.RetryAfter is { } retry && retry >= TimeSpan.Zero ? retry : null
        };
    }

    private ReverseLookupResult InvalidResult(string number, DateTimeOffset now) => new()
    {
        NormalizedNumber = number, Provider = providerId, Status = ReverseLookupStatus.TemporaryError,
        CheckedAtUtc = now, ExpiresAtUtc = now,
        Message = "Die Antwort des Anbieters konnte nicht eindeutig zugeordnet werden."
    };

    private void Persist(Dictionary<string, ReverseLookupResult> cache)
    {
        // Keep named results, including stale ones, before disposable negative lookups.
        var retained = cache.Values.OrderByDescending(x => x.Status == ReverseLookupStatus.Found)
            .ThenByDescending(x => x.CheckedAtUtc).Take(ReverseLookupStore.MaximumResults).ToArray();
        store.Save(retained);
        if (cache.Count > retained.Length)
        {
            var kept = retained.Select(x => ReverseLookupStore.Key(x.Provider, x.NormalizedNumber)).ToHashSet(StringComparer.Ordinal);
            foreach (string key in cache.Keys.Where(x => !kept.Contains(x)).ToArray()) cache.Remove(key);
        }
    }

    private static string CleanText(string? value, int maximumLength)
    {
        if (value is null || value.Length > maximumLength * 4) return "";
        string cleaned = string.Join(" ", new string(value.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= maximumLength && !cleaned.Any(char.IsControl) ? cleaned : "";
    }

    private static string CleanDiagnosticMessage(string? value, ReverseLookupStatus status)
    {
        string cleaned = CleanText(value, 500);
        if (cleaned.Length > 0 && !cleaned.Contains('<') && !cleaned.Contains('>')) return cleaned;
        // Diagnostics are short application messages, never raw HTML or an unbounded response body.
        return status switch
        {
            ReverseLookupStatus.UnrecognizedResponse => "Die Antwort des Anbieters konnte nicht ausgewertet werden. Diese Rufnummer wurde übersprungen.",
            ReverseLookupStatus.TemporaryError => "Die Rückwärtssuche ist für diese Rufnummer vorübergehend fehlgeschlagen.",
            ReverseLookupStatus.Ambiguous => "Mehrere Namen kommen infrage. Es wurde kein Name automatisch übernommen.",
            ReverseLookupStatus.Unsupported => "Diese Rufnummer wird vom Anbieter nicht unterstützt.",
            ReverseLookupStatus.RateLimited => "Der Anbieter begrenzt weitere Anfragen. Die Suche wurde angehalten.",
            ReverseLookupStatus.AuthRequired => "Der Anbieter verlangt eine Anmeldung. Die Suche wurde angehalten.",
            ReverseLookupStatus.Blocked => "Die Suche wurde für diesen Anbieter angehalten.",
            _ => ""
        };
    }

    private static void ValidateOptions(ReverseLookupBatchOptions options)
    {
        if (options.CountryCode is null || options.CountryCode.Length > 8 || options.AreaCode is null || options.AreaCode.Length > 30
            || options.RequestInterval < TimeSpan.Zero || options.RequestInterval > TimeSpan.FromMinutes(1)
            || options.FoundTtl <= TimeSpan.Zero || options.FoundTtl > TimeSpan.FromDays(366)
            || options.NotFoundTtl <= TimeSpan.Zero || options.NotFoundTtl > TimeSpan.FromDays(366)
            || options.MaximumConsecutiveUnrecognizedResponses is < 1 or > 10)
            throw new ArgumentException("Ungültige Einstellungen für die Rückwärtssuche.", nameof(options));
    }
}
