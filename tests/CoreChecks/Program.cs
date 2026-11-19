using CM.AnrufMonitor.Core;
using System.Text;
using System.Xml;

int passed = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException("FAIL: " + label);
    passed++; Console.WriteLine("PASS: " + label);
}
CallRecord Event(CallTracker tracker, string line) => tracker.Apply(line) ?? throw new InvalidOperationException("Event rejected: " + line);

Check(MonitorEvent.TryParse("19.11.26 14:30:00;RING;2;;030777;SIP0;", out var parsed) && parsed!.RemoteNumber == "", "Blank withheld caller retained");
Check(!MonitorEvent.TryParse("19.11.26 14:30:00;RING;2;123", out _), "Truncated RING ignored");
Check(!MonitorEvent.TryParse("19.11.26 14:30:00;DISCONNECT;2;-1;", out _), "Negative duration ignored");
Check(!MonitorEvent.TryParse("bad date;CONNECT;2;1;123;", out _), "Malformed date ignored");
Check(!MonitorEvent.TryParse(new string('x', 9000), out _), "Oversize record ignored");
Check(!MonitorEvent.TryParse("19.11.26 14:30:00;RING;-2;123;456;SIP0;", out _), "Negative connection ID ignored");
Check(MonitorEvent.TryParse("19.11.30 14:30:00;CONNECT;2;1;123;", out var future) && future!.Timestamp.Year == 2030, "Two-digit year is 2000-2099");

var tracker = new CallTracker();
CallRecord incoming = Event(tracker, "19.11.26 14:30:00;RING;2;030123;030777;SIP0;");
CallRecord outgoing = Event(tracker, "19.11.26 14:30:01;CALL;3;4;030777;040456;SIP1;");
Check(incoming.Direction == CallDirection.Incoming && outgoing.Direction == CallDirection.Outgoing && outgoing.RemoteNumber == "040456", "CALL field positions and directions");
Check(tracker.Snapshot().Count(x => x.IsActive) == 2, "Concurrent incoming/outgoing calls tracked independently");
Event(tracker, "19.11.26 14:30:03;CONNECT;2;4;030123;");
var answered = Event(tracker, "19.11.26 14:30:03;DISCONNECT;2;0;");
Check(answered.State == CallState.Answered && answered.DurationSeconds == 0, "Connected zero-second call remains answered");
var unanswered = Event(tracker, "19.11.26 14:30:04;DISCONNECT;3;0;");
Check(unanswered.State == CallState.NotAnswered, "Unanswered outbound call distinguished from missed incoming");
var reused = Event(tracker, "19.11.26 14:31:00;RING;2;030123;030777;SIP0;");
Check(reused.Id != incoming.Id && reused.ConnectionId == incoming.ConnectionId, "Reused connection ID gets new permanent record ID");
var missed = Event(tracker, "19.11.26 14:31:05;DISCONNECT;2;0;");
Check(missed.State == CallState.Missed, "Incoming ring without connect classified missed");
Check(tracker.Apply("19.11.26 14:31:05;DISCONNECT;2;0;") is null, "Duplicate disconnect ignored");
// Connection IDs and timestamps can repeat. De-duplication must follow the call lifetime.
var sameSecondAnswered = new CallTracker();
for (int round = 0; round < 2; round++)
{
    Event(sameSecondAnswered, "19.11.26 15:00:00;RING;5;030123;030777;SIP0;");
    sameSecondAnswered.Apply("19.11.26 15:00:00;CONNECT;5;4;030123;");
    sameSecondAnswered.Apply("19.11.26 15:00:00;DISCONNECT;5;0;");
}
var rapidAnswered = sameSecondAnswered.Snapshot();
Check(rapidAnswered.Count == 2 && rapidAnswered.Select(x => x.Id).Distinct().Count() == 2
    && rapidAnswered.All(x => x.State == CallState.Answered && x.DurationSeconds == 0 && !x.IsActive),
    "Same-second reused ID completes both answered zero-second calls");
var sameSecondMissed = new CallTracker();
for (int round = 0; round < 2; round++)
{
    Event(sameSecondMissed, "19.11.26 15:01:00;RING;5;030123;030777;SIP0;");
    sameSecondMissed.Apply("19.11.26 15:01:00;DISCONNECT;5;0;");
}
var rapidMissed = sameSecondMissed.Snapshot();
Check(rapidMissed.Count == 2 && rapidMissed.Select(x => x.Id).Distinct().Count() == 2
    && rapidMissed.All(x => x.State == CallState.Missed && x.DurationSeconds == 0 && !x.IsActive),
    "Same-second reused ID completes both missed zero-second calls");

Event(tracker, "19.11.26 14:32:00;RING;7;030987;030777;SIP0;");
tracker.MarkInterrupted();
Check(tracker.Snapshot().First(x => x.ConnectionId == "7").State == CallState.Interrupted, "Socket loss marks ringing call interrupted, not missed");
var partial = Event(tracker, "19.11.26 14:33:00;CONNECT;8;4;030321;");
Check(partial.Direction == CallDirection.Unknown && partial.State == CallState.Connected, "Attach mid-call preserves unknown direction");
var onlyEnd = Event(tracker, "19.11.26 14:33:00;DISCONNECT;9;100;");
Check(onlyEnd.State == CallState.Unknown && onlyEnd.Direction == CallDirection.Unknown, "Standalone end does not invent incoming/missed status");
Event(tracker, "19.11.26 14:34:00;RING;10;030321;030777;SIP0;");
var unknown = Event(tracker, "19.11.26 14:34:10;DISCONNECT;10;5;");
Check(unknown.State == CallState.Unknown, "Positive duration with missing CONNECT retains uncertainty");
tracker.ClearHistory();
Check(tracker.Snapshot().Count == 1 && tracker.Snapshot()[0].ConnectionId == "8", "Clearing history retains active calls");
var reloaded = new CallTracker(); reloaded.LoadHistory(tracker.Snapshot());
Check(reloaded.Snapshot()[0].State == CallState.Interrupted && !reloaded.Snapshot()[0].IsActive, "Reload never resurrects stale active call");
var hostile = new CallTracker();
for (int i = 0; i < 2200; i++) hostile.Apply($"19.11.26 14:35:00;RING;{i};030123;030777;SIP0;");
Check(hostile.Snapshot().Count <= 2000 && hostile.Snapshot().Count(x => x.IsActive) <= 128, "Both total history and active calls bounded");

Check(ContactBook.NormalizeNumber("030 / 123-456") == "+4930123456", "German national number normalization");
Check(ContactBook.NormalizeNumber("0049 (0)30 123456") == "+4930123456", "International 0049 with optional trunk prefix");
Check(ContactBook.NormalizeNumber("+49 (0)30 123456") == "+4930123456", "International +49 with optional trunk prefix");
Check(ContactBook.NormalizeNumber("123456", "+49", "030") == "+4930123456", "Local subscriber normalized using configured area code");
Check(ContactBook.NormalizeNumber("**610") == "**610", "Internal extension preserved");
Check(ContactBook.NormalizeNumber("") == "" && ContactBook.NormalizeNumber("anonymous") == "", "No matching key for anonymous numbers");
var contacts = new[] { new Contact("Testkontakt", "+49 30 123456"), new Contact("Anderer Anschluss", "+49 40 123456") };
Check(ContactBook.ResolveName("030123456", contacts) == "Testkontakt", "Exact normalized contact lookup");
Check(ContactBook.ResolveName("123456", contacts) is null, "No suffix-only contact matching");
Check(ContactBook.ResolveName("", new[] { new Contact("Invalid", "") }) is null, "Blank caller never matches a blank contact");
Check(ContactBook.ResolveName("030123456", new[] { contacts[0], new Contact("Zweiter Name", "030123456") }) == "Testkontakt / Zweiter Name", "Ambiguous contact names shown together");

string folder = Path.Combine(Path.GetTempPath(), "cm-anrufmonitor-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string xml = Path.Combine(folder, "phonebook.xml");
    File.WriteAllText(xml, "<phonebooks><phonebook name=\"Privat\"><contact><person><realName>Max &amp; Erika</realName></person><telephony><number type=\"home\">030123456</number><number type=\"mobile\">+4915112345678</number></telephony></contact></phonebook></phonebooks>");
    var imported = ContactBook.ImportFritzXml(xml);
    Check(imported.Count == 2 && imported[0].Name == "Max & Erika", "FRITZ XML imports multiple numbers and escaped names");
    string secret = Path.Combine(folder, "secret.txt"); File.WriteAllText(secret, "DO_NOT_READ");
    File.WriteAllText(xml, "<!DOCTYPE phonebooks [<!ENTITY xxe SYSTEM \"" + new Uri(secret).AbsoluteUri + "\">]><phonebooks><phonebook><contact><person><realName>&xxe;</realName></person></contact></phonebook></phonebooks>");
    bool xxeRejected = false;
    try { ContactBook.ImportFritzXml(xml); } catch (XmlException) { xxeRejected = true; }
    Check(xxeRejected, "XML DTD/external entity injection rejected");
    string csv = Path.Combine(folder, "contacts.csv");
    File.WriteAllText(csv, "Name;Nummer\r\n\"Müller; Büro\";030123456\r\n\"Erika \"\"E\"\"\";+4915112345678\r\n", new UTF8Encoding(true));
    var csvContacts = ContactBook.ImportCsv(csv);
    Check(csvContacts.Count == 2 && csvContacts[0].Name == "Müller; Büro" && csvContacts[1].Name == "Erika \"E\"", "CSV handles BOM, delimiters in quoted names, escaped quotes");
    File.WriteAllText(csv, "Name;Nummer\n\"broken;030123456");
    bool brokenCsvRejected = false; try { ContactBook.ImportCsv(csv); } catch (InvalidDataException) { brokenCsvRejected = true; }
    Check(brokenCsvRejected, "Malformed CSV is rejected rather than partially imported");

    var store = new AppStore(Path.Combine(folder, "data"));
    Check(store.LoadSettings().Host == "fritz.box" && store.LoadHistory().Count == 0, "New store returns documented defaults");
    var settings = new AppSettings { Host = "192.168.178.1", AlwaysOnTop = true, Opacity = 0.85, AreaCode = "030" };
    store.SaveSettings(settings);
    Check(store.LoadSettings().AlwaysOnTop && store.LoadSettings().Opacity == 0.85, "Settings round trip");
    store.SaveHistory([answered, missed, incoming with { IsDemo = true }]);
    Check(store.LoadHistory().Count == 2 && store.LoadHistory().All(x => !x.IsDemo), "Demo records excluded from persistent history");
    store.SaveContacts(imported); Check(store.LoadContacts().Count == 2, "Contact persistence round trip");
    string corruptPath = Path.Combine(store.DirectoryPath, "history.json");
    File.WriteAllText(corruptPath, "corrupt original");
    Check(store.LoadHistory().Count == 0 && store.Warnings.Count == 1, "Corruption recovered with visible warning");
    string backup = Directory.GetFiles(store.DirectoryPath, "history.json.*.broken").Single();
    Check(File.ReadAllText(backup) == "corrupt original", "Corrupt original preserved verbatim before replacement");
    store.SaveHistory([answered]);
    Check(File.Exists(backup) && store.LoadHistory().Count == 1 && !Directory.EnumerateFiles(store.DirectoryPath, "*.tmp").Any(), "Atomic write leaves preserved backup and no temporary files");
}
finally { Directory.Delete(folder, recursive: true); }
Console.WriteLine($"\n{passed} core checks passed.");
