using System.Globalization;

namespace CM.AnrufMonitor.Core;

/// <summary>A semicolon-separated FRITZ!Box call-monitor message. Router times are local wall times.</summary>
public sealed record MonitorEvent(DateTime Timestamp, string Kind, string ConnectionId,
    string RemoteNumber, string LocalNumber, string Extension, string Line, int DurationSeconds)
{
    public const int MaximumLineLength = 8192;
    private static readonly CultureInfo TimestampCulture = CreateTimestampCulture();
    private static CultureInfo CreateTimestampCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.Calendar = new GregorianCalendar { TwoDigitYearMax = 2099 };
        return culture;
    }

    public static bool TryParse(string? line, out MonitorEvent? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumLineLength) return false;
        line = line.TrimEnd('\r', '\n');
        if (line.Any(char.IsControl)) return false;
        string[] fields = line.Split(';', StringSplitOptions.None);
        if (fields.Length < 4 || !DateTime.TryParseExact(fields[0],
                ["dd.MM.yy HH:mm:ss", "dd.MM.yyyy HH:mm:ss"], TimestampCulture,
                DateTimeStyles.None, out DateTime timestamp)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int connectionId)
            || connectionId < 0) return false;
        string kind = fields[1];
        int expected = kind switch { "RING" => 6, "CALL" => 7, "CONNECT" => 5, "DISCONNECT" => 4, _ => 0 };
        if (expected == 0 || fields.Length < expected) return false;
        // The protocol allows a final empty field. Unknown extra fields are ignored for forward compatibility.
        if (fields.Skip(2).Any(x => x.Length > 256)) return false;
        string remote = "", local = "", extension = "", channel = "";
        int duration = 0;
        switch (kind)
        {
            case "RING": remote = fields[3].Trim(); local = fields[4].Trim(); channel = fields[5].Trim(); break;
            case "CALL": extension = fields[3].Trim(); local = fields[4].Trim(); remote = fields[5].Trim(); channel = fields[6].Trim(); break;
            case "CONNECT": extension = fields[3].Trim(); remote = fields[4].Trim(); break;
            case "DISCONNECT":
                if (!int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out duration) || duration < 0) return false;
                break;
        }
        result = new MonitorEvent(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), kind,
            connectionId.ToString(CultureInfo.InvariantCulture), remote, local, extension, channel, duration);
        return true;
    }
}
