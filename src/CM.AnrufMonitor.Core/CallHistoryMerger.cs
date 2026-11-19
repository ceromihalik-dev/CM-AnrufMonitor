using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CM.AnrufMonitor.Core;

public sealed record HistoryMergeResult(int Added = 0, int Updated = 0, int Matched = 0,
    int Ambiguous = 0, int Deferred = 0);

internal sealed record HistoryMergePlan(IReadOnlyList<CallRecord> Records, HistoryMergeResult Result);

/// <summary>
/// Reconciles completed FRITZ!Box history without inventing a second-precise router timestamp.
/// Live-to-router matches require a unique one-to-one correspondence for the entire fetched batch.
/// Ambiguous records remain separate; the returned count makes that uncertainty visible to the UI.
/// </summary>
internal static class CallHistoryMerger
{
    public static HistoryMergePlan Build(IReadOnlyList<CallRecord> current, FritzCallListSnapshot snapshot,
        string countryCode, string areaCode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.RouterIdentity) || snapshot.RouterIdentity.Length > 512
            || snapshot.RouterIdentity.Any(char.IsControl) || snapshot.Calls is null || snapshot.Calls.Count > 10000)
            throw new InvalidDataException("Die FRITZ!Box-Anrufliste enthält keine gültige Gerätekennung oder zu viele Einträge.");
        foreach (FritzCallEntry entry in snapshot.Calls) ValidateEntry(entry);

        string router = snapshot.RouterIdentity;
        // Only unbound currently active calls can be tagged here. Never relabel another router's history.
        var working = current.Select(x => x.IsActive && x.RouterIdentity.Length == 0
            ? x with { RouterIdentity = router } : x).ToList();
        int added = 0, updated = 0, matched = 0, ambiguous = 0, deferred = 0;
        var pending = new List<(FritzCallEntry Entry, string Key, int? KnownImportedIndex)>();
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var knownByRouterKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < working.Count; index++)
        {
            CallRecord record = working[index];
            if (record.RouterIdentity != router || record.RouterCallId.Length == 0) continue;
            string key = RouterKey(record.RouterIdentity, record.RouterCallId, record.StartedAt,
                record.Direction, record.RemoteNumber, record.LocalNumber, countryCode, areaCode);
            if (!knownByRouterKey.TryGetValue(key, out var indices)) knownByRouterKey[key] = indices = [];
            indices.Add(index);
        }

        foreach (FritzCallEntry entry in snapshot.Calls.OrderByDescending(x => x.StartedAt))
        {
            if (entry.Type is 9 or 11 || entry.State is CallState.Ringing or CallState.Dialing or CallState.Connected)
            {
                deferred++;
                continue;
            }
            string key = RouterKey(router, entry.Id, entry.StartedAt, entry.Direction,
                entry.RemoteNumber, entry.LocalNumber, countryCode, areaCode);
            if (!sourceKeys.Add(key)) continue;

            IReadOnlyList<int> known = knownByRouterKey.TryGetValue(key, out var knownIndices)
                ? knownIndices : Array.Empty<int>();
            if (known.Count > 1)
            {
                // Preserve pre-existing ambiguous data, but do not add yet another copy on every sync.
                ambiguous++;
                continue;
            }
            if (known.Count == 1)
            {
                int index = known[0];
                if (working[index].IsActive) { deferred++; continue; }
                if (working[index].IsFromRouter)
                {
                    // A router snapshot can arrive before queued live events. Reconcile an
                    // imported copy with a newly completed precise live record on the next fetch.
                    pending.Add((entry, key, index));
                    continue;
                }
                CallRecord replacement = Enrich(working[index], entry, router);
                if (replacement != working[index]) { working[index] = replacement; updated++; }
                matched++;
                continue;
            }
            pending.Add((entry, key, null));
        }

        // Build the whole matching graph before consuming any candidate. Greedy matching can
        // incorrectly merge two genuine calls to the same number during a single router minute.
        var candidateBuckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < working.Count; index++)
        {
            CallRecord record = working[index];
            if (!EligibleUnbound(record, router) || record.Direction == CallDirection.Unknown) continue;
            var fingerprint = EndpointFingerprint(record.StartedAt, record.Direction, record.RemoteNumber,
                record.LocalNumber, countryCode, areaCode);
            if (!candidateBuckets.TryGetValue(fingerprint.Key, out var indices))
                candidateBuckets[fingerprint.Key] = indices = [];
            indices.Add(index);
        }
        var candidateSets = new Dictionary<int, int[]>();
        var localDegrees = new Dictionary<int, int>();
        var deferredSources = new HashSet<int>();
        var ambiguousUnknownSources = new HashSet<int>();
        for (int source = 0; source < pending.Count; source++)
        {
            FritzCallEntry entry = pending[source].Entry;
            var fingerprint = EndpointFingerprint(entry.StartedAt, entry.Direction, entry.RemoteNumber,
                entry.LocalNumber, countryCode, areaCode);
            IReadOnlyList<int> bucket = candidateBuckets.TryGetValue(fingerprint.Key, out var bucketIndices)
                ? bucketIndices : Array.Empty<int>();
            bool hasActiveCandidate = bucket.Any(index => working[index].IsActive);
            if (hasActiveCandidate)
            {
                deferredSources.Add(source);
                deferred++;
                if (pending[source].KnownImportedIndex is int knownIndex)
                {
                    CallRecord replacement = Enrich(working[knownIndex], entry, router);
                    if (replacement != working[knownIndex]) { working[knownIndex] = replacement; updated++; }
                    matched++;
                }
                continue;
            }

            int[] possible = bucket.Where(index => !working[index].IsActive
                && CompatibleOutcomeAndDuration(working[index], entry)).ToArray();
            int[] candidates = fingerprint.CanMerge ? possible : Array.Empty<int>();
            if (!fingerprint.CanMerge && possible.Length > 0) ambiguousUnknownSources.Add(source);
            candidateSets[source] = candidates;
            foreach (int index in candidates) localDegrees[index] = localDegrees.GetValueOrDefault(index) + 1;
        }

        var removedImportedIndices = new HashSet<int>();
        for (int source = 0; source < pending.Count; source++)
        {
            if (deferredSources.Contains(source)) continue;
            var item = pending[source];
            int[] candidates = candidateSets[source];
            if (candidates.Length == 1 && localDegrees[candidates[0]] == 1)
            {
                int index = candidates[0];
                CallRecord replacement = Enrich(working[index], item.Entry, router);
                if (replacement != working[index]) { working[index] = replacement; updated++; }
                if (item.KnownImportedIndex is int importedIndex) removedImportedIndices.Add(importedIndex);
                matched++;
            }
            else
            {
                if (candidates.Length > 0 || ambiguousUnknownSources.Contains(source)) ambiguous++;
                if (item.KnownImportedIndex is int knownIndex)
                {
                    CallRecord replacement = Enrich(working[knownIndex], item.Entry, router);
                    if (replacement != working[knownIndex]) { working[knownIndex] = replacement; updated++; }
                    matched++;
                }
                else
                {
                    working.Add(CreateImported(item.Entry, router, item.Key));
                    added++;
                }
            }
        }

        return new HistoryMergePlan(working.Where((_, index) => !removedImportedIndices.Contains(index))
            .OrderByDescending(x => x.StartedAt).ToArray(),
            new HistoryMergeResult(added, updated, matched, ambiguous, deferred));
    }

    private static bool EligibleUnbound(CallRecord local, string router) => !local.IsDemo
        && local.RouterCallId.Length == 0 && (local.RouterIdentity.Length == 0 || local.RouterIdentity == router);

    private static (string Key, bool CanMerge) EndpointFingerprint(DateTime start, CallDirection direction,
        string remoteNumber, string localNumber, string countryCode, string areaCode)
    {
        string remote = ContactBook.NormalizeNumber(remoteNumber, countryCode, areaCode);
        string endpoint = ContactBook.NormalizeNumber(localNumber, countryCode, areaCode);
        // Equality of unknown values is useful only for deferring/flagging, never for merging.
        string key = string.Join('\u001F', Minute(start).Ticks.ToString(CultureInfo.InvariantCulture),
            ((int)direction).ToString(CultureInfo.InvariantCulture), remote, endpoint);
        return (key, direction != CallDirection.Unknown && remote.Length > 0 && endpoint.Length > 0);
    }

    private static bool CompatibleOutcomeAndDuration(CallRecord local, FritzCallEntry entry)
    {
        if (local.State == CallState.Interrupted) return true;
        if (local.State == CallState.Answered && entry.State is CallState.Missed or CallState.Rejected)
            return false;
        if (local.State is CallState.Missed or CallState.Rejected && entry.State == CallState.Answered)
            return false;
        if (local.State == CallState.NotAnswered && entry.DurationSeconds > 0) return false;
        if (local.State == CallState.Unknown && local.DurationSeconds == 0) return true;
        if (!entry.DurationIsApproximate) return local.DurationSeconds == entry.DurationSeconds;
        if (local.DurationIsApproximate) return local.DurationSeconds == entry.DurationSeconds;
        if (entry.DurationSeconds == 0) return local.DurationSeconds == 0;
        // Router durations are hh:mm with minutes rounded up. Live values remain exact seconds.
        return local.DurationSeconds <= entry.DurationSeconds
            && (local.DurationSeconds > entry.DurationSeconds - 60
                || local.DurationSeconds == 0 && entry.DurationSeconds == 60 && local.State == CallState.Answered);
    }

    private static CallRecord Enrich(CallRecord local, FritzCallEntry entry, string router)
    {
        bool precise = HasPreciseCompletedDuration(local);
        CallState state = entry.State;
        if (entry.State == CallState.Unknown && local.State is not (CallState.Unknown or CallState.Interrupted))
            state = local.State;
        // A CONNECT event is stronger evidence than an outgoing history row with duration 0.
        if (local.State == CallState.Answered && local.ConnectedAt.HasValue) state = CallState.Answered;
        return local with
        {
            RouterCallId = entry.Id,
            RouterIdentity = router,
            RouterName = entry.Name.Length == 0 ? local.RouterName : entry.Name,
            State = state,
            DurationSeconds = precise ? local.DurationSeconds : entry.DurationSeconds,
            DurationIsApproximate = precise ? false : entry.DurationIsApproximate,
            // Imported rounded durations do not establish an exact end time.
            EndedAt = precise ? local.EndedAt : null
        };
    }

    private static bool HasPreciseCompletedDuration(CallRecord record) => !record.DurationIsApproximate
        && !record.IsFromRouter && !record.IsActive && record.State != CallState.Interrupted
        && (record.State is CallState.Answered or CallState.Missed or CallState.NotAnswered or CallState.Rejected
            || record.DurationSeconds > 0);

    private static CallRecord CreateImported(FritzCallEntry entry, string router, string key) => new()
    {
        Id = "fritz-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant(),
        StartedAt = entry.StartedAt,
        RemoteNumber = entry.RemoteNumber,
        LocalNumber = entry.LocalNumber,
        Direction = entry.Direction,
        State = entry.State,
        DurationSeconds = entry.DurationSeconds,
        DurationIsApproximate = entry.DurationIsApproximate,
        RouterCallId = entry.Id,
        RouterIdentity = router,
        RouterName = entry.Name,
        IsFromRouter = true
    };

    private static string RouterKey(string router, string id, DateTime start, CallDirection direction,
        string remote, string local, string countryCode, string areaCode)
    {
        static string NumberKey(string value, string country, string area)
        {
            string normalized = ContactBook.NormalizeNumber(value, country, area);
            return normalized.Length == 0 ? "unknown:" + value.Trim() : normalized;
        }
        return string.Join('\u001F', router, id, Minute(start).Ticks.ToString(CultureInfo.InvariantCulture),
            ((int)direction).ToString(CultureInfo.InvariantCulture), NumberKey(remote, countryCode, areaCode),
            NumberKey(local, countryCode, areaCode));
    }

    private static DateTime Minute(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute,
        DateTimeKind.Unspecified);

    private static void ValidateEntry(FritzCallEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 256
            || entry.Id.Any(char.IsControl) || entry.StartedAt == default || entry.DurationSeconds < 0
            || entry.RemoteNumber is null || entry.RemoteNumber.Length > 256 || entry.RemoteNumber.Any(char.IsControl)
            || entry.LocalNumber is null || entry.LocalNumber.Length > 256 || entry.LocalNumber.Any(char.IsControl)
            || entry.Name is null || entry.Name.Length > 300
            || !Enum.IsDefined(entry.Direction) || !Enum.IsDefined(entry.State))
            throw new InvalidDataException("Die FRITZ!Box-Anrufliste enthält einen ungültigen Eintrag.");
    }
}
