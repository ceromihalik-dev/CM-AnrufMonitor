using CM.AnrufMonitor.Core;
using System.Text.Json;
using System.Diagnostics;

int passed = 0, failed = 0;
void Check(bool condition, string description)
{
    if (condition) { passed++; Console.WriteLine("PASS | " + description); }
    else { failed++; Console.WriteLine("FAIL | " + description); }
}

const string box = "uuid:synthetic-fritzbox-a";
DateTime when = new(2026, 11, 19, 14, 30, 0);
FritzCallEntry Entry(string id, int minutesAgo = 0, string remote = "030123456", string local = "030555001",
    int duration = 60, CallState state = CallState.Answered, CallDirection direction = CallDirection.Incoming,
    int type = 1) => new()
{
    Id = id, StartedAt = when.AddMinutes(-minutesAgo), RemoteNumber = remote, LocalNumber = local,
    Name = "Synthetischer Kontakt", DurationSeconds = duration, State = state, Direction = direction,
    Type = type, DurationIsApproximate = true
};
FritzCallListSnapshot Source(params FritzCallEntry[] calls) => new() { RouterIdentity = box, Calls = calls };
CallRecord Local(string id = "live-a", int seconds = 17, int duration = 29,
    CallState state = CallState.Answered, string remote = "030123456", string local = "030555001",
    CallDirection direction = CallDirection.Incoming) => new()
{
    Id = id, ConnectionId = id, StartedAt = when.AddSeconds(seconds), RemoteNumber = remote,
    LocalNumber = local, Direction = direction, State = state, DurationSeconds = duration,
    ConnectedAt = state == CallState.Answered ? when.AddSeconds(seconds + 4) : null,
    EndedAt = when.AddSeconds(seconds + duration + 4)
};

var tracker = new CallTracker();
var first = tracker.MergeRouterCalls(Source(Entry("100"), Entry("99", 1, state: CallState.Missed, duration: 0, type: 2)));
Check(first.Added == 2 && tracker.Snapshot().Count == 2, "Initial router history imports completed calls");
var stableIds = tracker.Snapshot().Select(x => x.Id).ToArray();
var repeat = tracker.MergeRouterCalls(Source(Entry("100"), Entry("99", 1, state: CallState.Missed, duration: 0, type: 2))
    with { SnapshotIdentity = "different-list-fetch-timestamp" });
Check(repeat.Added == 0 && repeat.Updated == 0 && repeat.Matched == 2
    && stableIds.SequenceEqual(tracker.Snapshot().Select(x => x.Id)), "Repeated sync ignores changing list timestamp and adds no duplicates");
Check(tracker.Snapshot().All(x => x.IsFromRouter && x.DurationIsApproximate && x.EndedAt is null),
    "Router-only records retain rounded duration and no invented exact end");

tracker = new CallTracker();
var live = Local();
tracker.LoadHistory([live]);
var merged = tracker.MergeRouterCalls(Source(Entry("100")));
var found = tracker.Snapshot().Single();
Check(merged.Added == 0 && merged.Matched == 1 && found.Id == live.Id && found.RouterCallId == "100",
    "Unique live call matches router minute without replacing permanent local identity");
Check(found.StartedAt == live.StartedAt && found.DurationSeconds == 29 && !found.DurationIsApproximate,
    "Precise live seconds and start timestamp survive rounded router import");
Check(found.RouterName == "Synthetischer Kontakt" && !found.IsFromRouter && found.RouterIdentity == box,
    "Existing live record receives router name and verified router identity");
var renamed = tracker.MergeRouterCalls(Source(Entry("100") with { Name = "Neuer Telefonbuchname" }));
Check(renamed.Updated == 1 && tracker.Snapshot().Single().RouterName == "Neuer Telefonbuchname",
    "Repeat fetch refreshes router name in place");

tracker = new CallTracker();
tracker.LoadHistory([Local("live-a", 5, 12), Local("live-b", 30, 85)]);
var uniqueDurations = tracker.MergeRouterCalls(Source(Entry("101", duration: 120), Entry("100", duration: 60)));
Check(uniqueDurations.Matched == 2 && uniqueDurations.Added == 0 && tracker.Snapshot().Count == 2,
    "Two same-number same-minute calls match separately when durations prove unique pairing");
Check(tracker.Snapshot().Single(x => x.Id == "live-a").RouterCallId == "100"
    && tracker.Snapshot().Single(x => x.Id == "live-b").RouterCallId == "101",
    "Batch matching uses one-to-one duration evidence and does not greedily swap calls");

tracker = new CallTracker();
tracker.LoadHistory([Local("live-a", 5, 12), Local("live-b", 30, 18)]);
var ambiguous = tracker.MergeRouterCalls(Source(Entry("101"), Entry("100")));
Check(ambiguous.Matched == 0 && ambiguous.Ambiguous == 2 && tracker.Snapshot().Count == 4,
    "Indistinguishable calls remain separate rather than silently collapsing genuine calls");
Check(tracker.MergeRouterCalls(Source(Entry("101"), Entry("100"))).Added == 0 && tracker.Snapshot().Count == 4,
    "Ambiguous preserved history does not multiply on later fetches");

tracker = new CallTracker();
tracker.LoadHistory([Local("hidden-live", remote: "", duration: 0, state: CallState.Missed)]);
var hidden = tracker.MergeRouterCalls(Source(Entry("hidden-box", remote: "", duration: 0, state: CallState.Missed, type: 2)));
Check(hidden.Matched == 0 && hidden.Ambiguous == 1 && tracker.Snapshot().Count == 2,
    "Withheld numbers never create an unproven live-to-router match");
Check(tracker.MergeRouterCalls(Source(Entry("hidden-box", remote: "", duration: 0, state: CallState.Missed, type: 2))).Added == 0,
    "Withheld router calls still deduplicate by verified router key");

tracker = new CallTracker();
tracker.LoadHistory([Local("different-own-number", local: "030555002")]);
Check(tracker.MergeRouterCalls(Source(Entry("100"))).Matched == 0 && tracker.Snapshot().Count == 2,
    "Different own numbers prevent cross-line merging");
tracker = new CallTracker();
tracker.LoadHistory([Local("suffix-only", remote: "123456")]);
Check(tracker.MergeRouterCalls(Source(Entry("100"))).Matched == 0, "No suffix-only number merge without configured area code");
tracker = new CallTracker();
tracker.LoadHistory([Local("local-prefix", remote: "123456")]);
Check(tracker.MergeRouterCalls(Source(Entry("100")), "+49", "030").Matched == 1,
    "Configured area code permits exact canonical local-number matching");
tracker = new CallTracker();
tracker.LoadHistory([Local("wrong-minute", seconds: -1)]);
Check(tracker.MergeRouterCalls(Source(Entry("100"))).Matched == 0,
    "Calls across minute boundaries do not merge merely because they are less than 60 seconds apart");

tracker = new CallTracker();
tracker.MergeRouterCalls(Source(Entry("100", 5)));
tracker.MergeRouterCalls(Source(Entry("100", 0)));
Check(tracker.Snapshot().Count == 2, "Reused router call IDs after reset do not overwrite a different start minute");
tracker.MergeRouterCalls(Source(Entry("100", remote: "030999999")));
Check(tracker.Snapshot().Count == 3, "Reused IDs also retain calls with different endpoint fingerprints");
tracker.MergeRouterCalls(Source(Entry("100")) with { RouterIdentity = "uuid:synthetic-fritzbox-b" });
Check(tracker.Snapshot().Count == 4, "Identical ID/time/number on another router remains a different call");

tracker = new CallTracker();
tracker.LoadHistory([Local() with { RouterIdentity = "uuid:old-router" }]);
Check(tracker.MergeRouterCalls(Source(Entry("100"))).Matched == 0, "A live record bound to another router is never reassigned");

tracker = new CallTracker();
tracker.Apply("19.11.26 14:30:17;RING;7;030123456;030555001;SIP0;");
tracker.Apply("19.11.26 14:30:21;CONNECT;7;11;030123456;");
string activeId = tracker.Snapshot().Single().Id;
var racing = tracker.MergeRouterCalls(Source(Entry("100")));
Check(racing.Deferred == 1 && racing.Added == 0 && tracker.Snapshot().Single().IsActive,
    "Completed router entry is deferred while its live call still awaits DISCONNECT");
tracker.Apply("19.11.26 14:30:50;DISCONNECT;7;29;");
var afterCompletion = tracker.MergeRouterCalls(Source(Entry("100")));
Check(afterCompletion.Matched == 1 && tracker.Snapshot().Single().Id == activeId
    && tracker.Snapshot().Single().DurationSeconds == 29,
    "DISCONNECT still reaches its active record after merge and post-call sync reconciles in place");
var lateEvents = new CallTracker();
lateEvents.MergeRouterCalls(Source(Entry("100")));
lateEvents.Apply("19.11.26 14:30:17;RING;7;030123456;030555001;SIP0;");
lateEvents.Apply("19.11.26 14:30:21;CONNECT;7;11;030123456;");
var stillActive = lateEvents.MergeRouterCalls(Source(Entry("100")));
Check(stillActive.Deferred == 1 && lateEvents.Snapshot().Count == 2
    && lateEvents.Snapshot().Count(x => x.IsActive) == 1,
    "A pre-imported record is not merged into a late-arriving live call while it is active");
lateEvents.Apply("19.11.26 14:30:50;DISCONNECT;7;29;");
string preciseLateId = lateEvents.Snapshot().Single(x => !x.IsFromRouter).Id;
var reconciledLate = lateEvents.MergeRouterCalls(Source(Entry("100")));
Check(reconciledLate.Added == 0 && reconciledLate.Matched == 1 && lateEvents.Snapshot().Count == 1
    && lateEvents.Snapshot().Single().Id == preciseLateId && lateEvents.Snapshot().Single().DurationSeconds == 29,
    "Queued late live events replace an earlier imported copy once precise completion is available");
Check(lateEvents.MergeRouterCalls(Source(Entry("100"))).Added == 0 && lateEvents.Snapshot().Count == 1,
    "Late-event reconciliation remains deduplicated on following syncs");
var lateAmbiguous = new CallTracker();
lateAmbiguous.MergeRouterCalls(Source(Entry("100"), Entry("101")));
lateAmbiguous.Apply("19.11.26 14:30:17;RING;7;030123456;030555001;SIP0;");
lateAmbiguous.Apply("19.11.26 14:30:21;CONNECT;7;11;030123456;");
lateAmbiguous.Apply("19.11.26 14:30:40;DISCONNECT;7;19;");
var lateUncertain = lateAmbiguous.MergeRouterCalls(Source(Entry("100"), Entry("101")));
Check(lateUncertain.Ambiguous == 2 && lateAmbiguous.Snapshot().Count == 3,
    "Late live data is not arbitrarily attached to one of two indistinguishable imported calls");
tracker.Apply("19.11.26 14:31:00;RING;8;030111111;030555001;SIP0;");
tracker.MergeRouterCalls(Source(Entry("100"), Entry("99", 4)));
tracker.Apply("19.11.26 14:31:20;CONNECT;8;11;030111111;");
tracker.Apply("19.11.26 14:31:35;DISCONNECT;8;15;");
Check(tracker.Snapshot().Single(x => x.ConnectionId == "8").State == CallState.Answered,
    "An unrelated history fetch preserves the active connection-ID map");
Check(tracker.Snapshot().Single(x => x.ConnectionId == "8").RouterIdentity == box,
    "New live calls inherit the last verified router binding");
tracker.SetRouterIdentity("");
tracker.Apply("19.11.26 14:32:00;RING;9;030111111;030555001;SIP0;");
Check(tracker.Snapshot().Single(x => x.ConnectionId == "9").RouterIdentity == "",
    "Resetting router binding prevents stamping a new host with the previous UDN");

tracker = new CallTracker();
tracker.Apply("19.11.26 14:30:17;RING;7;030123456;030555001;SIP0;");
tracker.Apply("19.11.26 14:30:21;CONNECT;7;11;030123456;");
tracker.MarkInterrupted();
var repaired = tracker.MergeRouterCalls(Source(Entry("100", duration: 120)));
Check(repaired.Matched == 1 && tracker.Snapshot().Single().State == CallState.Answered
    && tracker.Snapshot().Single().DurationSeconds == 120 && tracker.Snapshot().Single().DurationIsApproximate,
    "Completed router data repairs a call interrupted by a lost monitor connection");
Check(tracker.Snapshot().Single().EndedAt is null, "Repair does not retain the socket-loss time as a false exact call end");

tracker = new CallTracker();
tracker.Apply("19.11.26 14:30:17;RING;7;030123456;030555001;SIP0;");
tracker.Apply("19.11.26 14:30:40;DISCONNECT;7;19;");
Check(tracker.Snapshot().Single().State == CallState.Unknown, "Partial positive-duration live call starts with uncertain status");
tracker.MergeRouterCalls(Source(Entry("100")));
Check(tracker.Snapshot().Single().State == CallState.Answered && tracker.Snapshot().Single().DurationSeconds == 19
    && !tracker.Snapshot().Single().DurationIsApproximate,
    "Router evidence repairs unknown status while preserving observed precise disconnect duration");

tracker = new CallTracker();
tracker.LoadHistory([Local("zero-answer", duration: 0, direction: CallDirection.Outgoing)]);
tracker.MergeRouterCalls(Source(Entry("100", duration: 0, state: CallState.Unknown, direction: CallDirection.Outgoing, type: 3)));
Check(tracker.Snapshot().Count == 1 && tracker.Snapshot().Single().State == CallState.Answered,
    "Zero-duration outgoing router history cannot downgrade a precise observed CONNECT");
tracker = new CallTracker();
tracker.LoadHistory([Local("not-connected", duration: 0, state: CallState.Missed)]);
Check(tracker.MergeRouterCalls(Source(Entry("100", duration: 60))).Matched == 0 && tracker.Snapshot().Count == 2,
    "Conflicting incoming answered/missed outcomes prevent false merging");
tracker = new CallTracker();
tracker.LoadHistory([Local("rejected", duration: 0, state: CallState.Missed)]);
tracker.MergeRouterCalls(Source(Entry("100", duration: 0, state: CallState.Rejected, type: 10)));
Check(tracker.Snapshot().Single().State == CallState.Rejected,
    "Router call-barring evidence distinguishes rejected from monitor-only missed status");

tracker = new CallTracker();
var ongoing = tracker.MergeRouterCalls(Source(Entry("active-in", type: 9, state: CallState.Connected),
    Entry("active-out", type: 11, state: CallState.Connected, direction: CallDirection.Outgoing)));
Check(ongoing.Added == 0 && ongoing.Deferred == 2 && tracker.Snapshot().Count == 0,
    "Router ongoing types 9 and 11 are never imported as completed history");
tracker.MergeRouterCalls(Source(Entry("same"), Entry("same")));
Check(tracker.Snapshot().Count == 1, "Duplicate rows inside one source fetch are added once");
var beforeInvalid = tracker.Snapshot().ToArray();
bool rejectedInvalid = false;
try { tracker.MergeRouterCalls(Source(Entry("good-next", 2), Entry("bad", 3) with { DurationSeconds = -1 })); }
catch (InvalidDataException) { rejectedInvalid = true; }
Check(rejectedInvalid && beforeInvalid.SequenceEqual(tracker.Snapshot()),
    "Malformed snapshot is rejected atomically before altering any history");

tracker = new CallTracker();
for (int i = 0; i < CallTracker.MaximumActiveCalls; i++)
    tracker.Apply($"19.11.26 14:30:17;RING;{i};030{i + 100000};030555001;SIP0;");
tracker.MergeRouterCalls(Source(Enumerable.Range(1, 2100).Select(i => Entry(i.ToString(), i,
    duration: 0, state: CallState.Missed, type: 2)).ToArray()));
Check(tracker.Snapshot().Count == CallTracker.MaximumRecords
    && tracker.Snapshot().Count(x => x.IsActive) == CallTracker.MaximumActiveCalls,
    "Large router history stays at 2000 records while preserving all bounded active calls");
tracker.ClearHistory();
Check(tracker.Snapshot().Count == CallTracker.MaximumActiveCalls,
    "Clear local history keeps active calls; router history can intentionally reload on later sync");

var fullHistory = new CallTracker();
var fullSnapshot = Source(Enumerable.Range(1, CallTracker.MaximumRecords)
    .Select(i => Entry(i.ToString(), i, duration: i % 4 * 60)).ToArray());
fullHistory.MergeRouterCalls(fullSnapshot);
var fullHistoryIds = fullHistory.Snapshot().Select(x => x.Id).ToArray();
var repeatTimer = Stopwatch.StartNew();
var fullRepeat = fullHistory.MergeRouterCalls(fullSnapshot);
repeatTimer.Stop();
Check(fullRepeat.Added == 0 && fullRepeat.Updated == 0 && fullRepeat.Matched == CallTracker.MaximumRecords
    && fullHistoryIds.SequenceEqual(fullHistory.Snapshot().Select(x => x.Id)),
    "Repeated sync of a realistic full 2000-record history preserves every identity without duplicates");
Console.WriteLine($"OBSERVATION | Repeated 2000-record sync: {repeatTimer.Elapsed.TotalMilliseconds:F1} ms (not a timing gate)");

string temporary = Path.Combine(Path.GetTempPath(), "CM-AnrufMonitor-HistorySync-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var store = new AppStore(temporary);
    File.WriteAllText(Path.Combine(temporary, "settings.json"), "{\"Host\":\"fritz.box\",\"Port\":1012,\"AutoConnect\":true}");
    var oldSettings = store.LoadSettings();
    Check(oldSettings.AutoConnect && !oldSettings.FritzSyncEnabled && oldSettings.FritzPort == 49000
        && oldSettings.SyncPhonebooks && oldSettings.FritzUsername == "",
        "Version 0.1 settings load with backward-compatible opt-in sync defaults");
    File.WriteAllText(Path.Combine(temporary, "history.json"),
        "[{\"Id\":\"legacy\",\"ConnectionId\":\"7\",\"StartedAt\":\"2026-11-19T14:30:17\",\"RemoteNumber\":\"030123456\",\"LocalNumber\":\"030555001\",\"Direction\":\"Incoming\",\"State\":\"Answered\",\"DurationSeconds\":29}]");
    var migrated = store.LoadHistory();
    Check(migrated.Count == 1 && migrated[0].RouterCallId == "" && migrated[0].RouterIdentity == ""
        && !migrated[0].IsFromRouter && !migrated[0].DurationIsApproximate,
        "Version 0.1 history loads without being quarantined or losing exact durations");
    tracker = new CallTracker(); tracker.LoadHistory(migrated);
    tracker.MergeRouterCalls(Source(Entry("100")));
    store.SaveHistory(tracker.Snapshot());
    var restarted = new CallTracker(); restarted.LoadHistory(store.LoadHistory());
    Check(restarted.MergeRouterCalls(Source(Entry("100"))).Added == 0 && restarted.Snapshot().Count == 1
        && restarted.Snapshot().Single().DurationSeconds == 29,
        "Migrated history persists sync metadata and remains deduplicated across application restarts");
    oldSettings.FritzSyncEnabled = true; oldSettings.FritzUsername = "readonly-user";
    oldSettings.LastSuccessfulSync = when; oldSettings.LastPhonebookSync = when.AddMinutes(-5);
    store.SaveSettings(oldSettings);
    Check(store.LoadSettings().LastSuccessfulSync == when && store.LoadSettings().FritzUsername == "readonly-user",
        "Sync preferences and last successful times round-trip");
    Check(!typeof(AppSettings).GetProperties().Any(p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase))
        && !File.ReadAllText(Path.Combine(temporary, "settings.json")).Contains("password", StringComparison.OrdinalIgnoreCase),
        "Persisted settings model contains no plaintext password field");
    store.SaveContacts([new Contact("Eigener Kontakt", "030111111")]);
    store.SaveRouterContacts(box, [new Contact("FRITZ! Kontakt", "030222222")]);
    Check(store.LoadRouterContacts(box).Count == 1 && store.LoadRouterContacts("uuid:other-box").Count == 0
        && store.LoadRouterContacts("").Count == 0,
        "Automatic phonebook cache is available only to the verified same router identity");
    Check(store.LoadContacts().Single().Name == "Eigener Kontakt",
        "Router contact refresh preserves manually managed contacts separately");
    store.ClearRouterContacts();
    Check(store.LoadRouterContacts(box).Count == 0 && store.LoadContacts().Count == 1,
        "Clearing router cache preserves manual contacts");
    store.SaveRouterContacts("uuid:new-box", [new Contact("Anderer Router", "030333333")]);
    Check(store.LoadRouterContacts(box).Count == 0 && store.LoadRouterContacts("uuid:new-box").Count == 1,
        "Replacing router cache never returns names from the previously configured box");
    Check(!Directory.EnumerateFiles(temporary, "*.broken").Any(),
        "Backward migration and valid sync metadata do not trigger broken-data recovery");
    var rejectedRecord = Local() with { State = CallState.Rejected, RouterCallId = "rejected-id", RouterIdentity = box };
    store.SaveHistory([rejectedRecord]);
    Check(store.LoadHistory().Single().State == CallState.Rejected && (int)CallState.Unknown == 7,
        "New rejected state round-trips without changing previous enum values");
}
finally { Directory.Delete(temporary, recursive: true); }

Console.WriteLine($"HISTORY_SYNC_CHECKS: {passed}/{passed + failed} passed; synthetic data only.");
return failed == 0 ? 0 : 1;
