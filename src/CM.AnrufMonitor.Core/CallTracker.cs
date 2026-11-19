namespace CM.AnrufMonitor.Core;

/// <summary>
/// Maintains concurrent calls by the connection ID, while every recorded call gets its own permanent ID.
/// Own this class on one thread (normally the UI dispatcher). It is intentionally not thread-safe.
/// </summary>
public sealed class CallTracker
{
    public const int MaximumRecords = 2000;
    public const int MaximumActiveCalls = 128;
    private readonly List<CallRecord> records = [];
    private readonly Dictionary<string, string> activeIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> recentLines = new(StringComparer.Ordinal);
    private readonly Queue<(string ConnectionId, string Key)> lineOrder = new();
    private string currentRouterIdentity = "";

    /// <summary>Reset to empty before changing routers; a successful sync binds its verified identity.</summary>
    public void SetRouterIdentity(string routerIdentity)
    {
        ArgumentNullException.ThrowIfNull(routerIdentity);
        if (routerIdentity.Length > 512 || routerIdentity.Any(char.IsControl))
            throw new ArgumentException("Ungültige FRITZ!Box-Gerätekennung.", nameof(routerIdentity));
        currentRouterIdentity = routerIdentity;
    }

    public CallRecord? Apply(string line)
    {
        if (!MonitorEvent.TryParse(line, out var message) || message is null) return null;
        CallRecord? previous = FindActive(message.ConnectionId);
        if (previous is not null && message.Timestamp < previous.StartedAt) return null;
        if (message.Kind is "RING" or "CALL")
        {
            // A repeated announcement of an active call is still a duplicate. A genuine new
            // start, however, establishes a new generation even when its ID and timestamp repeat.
            if (previous is not null && previous.StartedAt == message.Timestamp
                && previous.RemoteNumber == message.RemoteNumber && previous.LocalNumber == message.LocalNumber)
                return null;
            ForgetConnectionEvents(message.ConnectionId);
        }
        string key = line.TrimEnd('\r', '\n');
        if (!recentLines.Add(key)) return null;
        lineOrder.Enqueue((message.ConnectionId, key));
        if (lineOrder.Count > 512) recentLines.Remove(lineOrder.Dequeue().Key);
        CallRecord next;
        if (message.Kind is "RING" or "CALL")
        {
            if (previous is not null)
            {
                Replace(previous with { State = CallState.Interrupted, EndedAt = message.Timestamp });
            }
            next = new CallRecord
            {
                ConnectionId = message.ConnectionId, StartedAt = message.Timestamp,
                RemoteNumber = message.RemoteNumber, LocalNumber = message.LocalNumber,
                Extension = message.Extension, Line = message.Line, RouterIdentity = currentRouterIdentity,
                Direction = message.Kind == "RING" ? CallDirection.Incoming : CallDirection.Outgoing,
                State = message.Kind == "RING" ? CallState.Ringing : CallState.Dialing
            };
            records.Insert(0, next);
            activeIds[message.ConnectionId] = next.Id;
        }
        else if (message.Kind == "CONNECT")
        {
            previous ??= new CallRecord
            {
                ConnectionId = message.ConnectionId, StartedAt = message.Timestamp,
                Direction = CallDirection.Unknown, State = CallState.Unknown, RouterIdentity = currentRouterIdentity
            };
            if (!records.Any(x => x.Id == previous.Id)) records.Insert(0, previous);
            next = previous with
            {
                ConnectedAt = previous.ConnectedAt ?? message.Timestamp,
                RemoteNumber = string.IsNullOrWhiteSpace(message.RemoteNumber) ? previous.RemoteNumber : message.RemoteNumber,
                Extension = message.Extension, State = CallState.Connected
            };
            Replace(next);
            activeIds[message.ConnectionId] = next.Id;
        }
        else
        {
            if (previous is null)
            {
                // Joining during a call can yield DISCONNECT alone. Keep the uncertainty explicit.
                next = new CallRecord
                {
                    ConnectionId = message.ConnectionId, StartedAt = message.Timestamp,
                    EndedAt = message.Timestamp, State = CallState.Unknown,
                    Direction = CallDirection.Unknown, DurationSeconds = message.DurationSeconds,
                    RouterIdentity = currentRouterIdentity
                };
                records.Insert(0, next);
            }
            else
            {
                CallState final = previous.ConnectedAt.HasValue ? CallState.Answered
                    : message.DurationSeconds > 0 ? CallState.Unknown
                    : previous.Direction == CallDirection.Incoming ? CallState.Missed
                    : previous.Direction == CallDirection.Outgoing ? CallState.NotAnswered : CallState.Unknown;
                next = previous with { EndedAt = message.Timestamp, DurationSeconds = message.DurationSeconds, State = final };
                Replace(next);
                activeIds.Remove(message.ConnectionId);
            }
        }
        Trim();
        return next;
    }

    public IReadOnlyList<CallRecord> Snapshot() => records.OrderByDescending(x => x.StartedAt).ToArray();

    /// <summary>
    /// Atomically merges a fetched history on the owning/UI thread. Existing active call IDs survive.
    /// Older records without a router identity can only be associated by a unique full-number/time match.
    /// </summary>
    public HistoryMergeResult MergeRouterCalls(FritzCallListSnapshot snapshot, string countryCode = "+49", string areaCode = "")
    {
        HistoryMergePlan plan = CallHistoryMerger.Build(records, snapshot, countryCode, areaCode);
        records.Clear();
        records.AddRange(plan.Records);
        currentRouterIdentity = snapshot.RouterIdentity;
        Trim();
        return plan.Result;
    }

    public void MarkInterrupted()
    {
        for (int i = 0; i < records.Count; i++)
            if (records[i].IsActive) records[i] = records[i] with { State = CallState.Interrupted, EndedAt = DateTime.Now };
        activeIds.Clear();
        recentLines.Clear();
        lineOrder.Clear();
    }

    public void LoadHistory(IEnumerable<CallRecord> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        records.Clear(); activeIds.Clear(); recentLines.Clear(); lineOrder.Clear();
        records.AddRange(history.Where(x => x is not null && !x.IsDemo).DistinctBy(x => x.Id)
            .OrderByDescending(x => x.StartedAt).Take(MaximumRecords)
            .Select(x => x.IsActive ? x with { State = CallState.Interrupted, EndedAt = null } : x));
    }

    public void ClearHistory()
    {
        // This clears the local view only. A future router sync intentionally reloads router history;
        // the UI must explain this in its existing confirmation dialog.
        records.RemoveAll(x => !x.IsActive);
        recentLines.Clear(); lineOrder.Clear();
    }

    private void ForgetConnectionEvents(string connectionId)
    {
        // Both structures must be purged together. Otherwise an old queue entry could evict
        // a byte-identical event belonging to the newly reused connection ID later on.
        int count = lineOrder.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = lineOrder.Dequeue();
            if (entry.ConnectionId == connectionId) recentLines.Remove(entry.Key);
            else lineOrder.Enqueue(entry);
        }
    }

    private CallRecord? FindActive(string connectionId) => activeIds.TryGetValue(connectionId, out string? id)
        ? records.FirstOrDefault(x => x.Id == id) : null;
    private void Replace(CallRecord record)
    {
        int index = records.FindIndex(x => x.Id == record.Id);
        if (index >= 0) records[index] = record;
    }
    private void Trim()
    {
        // A malformed sender must not grow memory without bound by announcing thousands of active IDs.
        while (activeIds.Count > MaximumActiveCalls)
        {
            int index = records.FindLastIndex(x => x.IsActive);
            if (index < 0) break;
            CallRecord oldest = records[index];
            records[index] = oldest with { State = CallState.Interrupted, EndedAt = null };
            activeIds.Remove(oldest.ConnectionId);
        }
        while (records.Count > MaximumRecords)
        {
            int index = records.FindLastIndex(x => !x.IsActive);
            if (index < 0) break;
            records.RemoveAt(index);
        }
    }
}
