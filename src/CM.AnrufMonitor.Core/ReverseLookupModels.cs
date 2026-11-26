namespace CM.AnrufMonitor.Core;

public enum ReverseLookupStatus
{
    Found, NotFound, Ambiguous, TemporaryError, RateLimited, AuthRequired, Blocked, Unsupported, UnrecognizedResponse
}

/// <summary>An Internet result stays separate from trusted manual and router contacts.</summary>
public sealed record ReverseLookupResult
{
    public ReverseLookupStatus Status { get; init; } = ReverseLookupStatus.TemporaryError;
    public string NormalizedNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string Provider { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public DateTimeOffset CheckedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string Message { get; init; } = "";
    public TimeSpan? RetryAfter { get; init; }
}

public interface IReverseLookupProvider
{
    string Id { get; }
    bool SupportsNumber(string normalizedNumber) => true;
    Task<ReverseLookupResult> LookupAsync(string normalizedNumber, CancellationToken cancellationToken);
}

public sealed record ReverseLookupBatchOptions
{
    public string CountryCode { get; init; } = "+49";
    public string AreaCode { get; init; } = "";
    public TimeSpan RequestInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan FoundTtl { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan NotFoundTtl { get; init; } = TimeSpan.FromDays(7);
    public bool RefreshCached { get; init; }
    public int MaximumConsecutiveUnrecognizedResponses { get; init; } = 3;
}

/// <summary>A frozen plan built from the caller's explicit history or selection snapshot.</summary>
public sealed class ReverseLookupBatchPlan
{
    internal ReverseLookupBatchPlan(string provider, ReverseLookupBatchOptions options,
        IEnumerable<string> numbers, IEnumerable<string> pendingNumbers,
        IEnumerable<ReverseLookupResult> cachedResults, int sourceRecordCount,
        int skippedRecordCount, int duplicateRecordCount, IEnumerable<string> unsupportedNumbers)
    {
        Provider = provider;
        Options = options with { };
        Numbers = Array.AsReadOnly(numbers.ToArray());
        PendingNumbers = Array.AsReadOnly(pendingNumbers.ToArray());
        CachedResults = Array.AsReadOnly(cachedResults.ToArray());
        SourceRecordCount = sourceRecordCount;
        SkippedRecordCount = skippedRecordCount;
        DuplicateRecordCount = duplicateRecordCount;
        UnsupportedNumbers = Array.AsReadOnly(unsupportedNumbers.ToArray());
    }

    public string Provider { get; }
    public ReverseLookupBatchOptions Options { get; }
    public IReadOnlyList<string> Numbers { get; }
    public IReadOnlyList<string> PendingNumbers { get; }
    public IReadOnlyList<ReverseLookupResult> CachedResults { get; }
    public int SourceRecordCount { get; }
    public int SkippedRecordCount { get; }
    public int DuplicateRecordCount { get; }
    public IReadOnlyList<string> UnsupportedNumbers { get; }
    public int UnsupportedCount => UnsupportedNumbers.Count;
    public int TotalCount => Numbers.Count;
    public int RequestCount => PendingNumbers.Count;
    public int CachedCount => CachedResults.Count;
}

public sealed record ReverseLookupProgress
{
    public int TotalCount { get; init; }
    public int CompletedCount { get; init; }
    public int RequestedCount { get; init; }
    public int CachedCount { get; init; }
    public int FoundCount { get; init; }
    public int NotFoundCount { get; init; }
    public int AmbiguousCount { get; init; }
    public int ErrorCount { get; init; }
    public int UnrecognizedCount { get; init; }
    public int UnsupportedCount { get; init; }
    public ReverseLookupResult? Result { get; init; }
    public bool FromCache { get; init; }
    public bool IsStopped { get; init; }
}

public sealed record ReverseLookupBatchSummary
{
    public int TotalCount { get; init; }
    public int CompletedCount { get; init; }
    public int RequestedCount { get; init; }
    public int CachedCount { get; init; }
    public int FoundCount { get; init; }
    public int NotFoundCount { get; init; }
    public int AmbiguousCount { get; init; }
    public int ErrorCount { get; init; }
    public int UnrecognizedCount { get; init; }
    public int UnsupportedCount { get; init; }
    public IReadOnlyList<ReverseLookupResult> Results { get; init; } = Array.Empty<ReverseLookupResult>();
    /// <summary>Completed uncertain/failed results plus local unsupported numbers; never raw provider HTML.</summary>
    public IReadOnlyList<ReverseLookupResult> UnresolvedResults { get; init; } = Array.Empty<ReverseLookupResult>();
    /// <summary>Eligible numbers not completed; this reports remaining work without inventing failed results.</summary>
    public IReadOnlyList<string> RemainingNumbers { get; init; } = Array.Empty<string>();
    public bool Cancelled { get; init; }
    public ReverseLookupStatus? StopStatus { get; init; }
    public string Message { get; init; } = "";
}
