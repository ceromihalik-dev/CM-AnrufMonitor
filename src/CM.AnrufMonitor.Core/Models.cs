using System.Text.Json.Serialization;

namespace CM.AnrufMonitor.Core;

public enum CallDirection { Incoming, Outgoing, Unknown }
// Append states to retain the numeric representation of existing 0.1 histories.
public enum CallState { Ringing, Dialing, Connected, Answered, Missed, NotAnswered, Interrupted, Unknown, Rejected }
public enum MonitorConnectionState { Disconnected, Connecting, Connected, Reconnecting }

public sealed record CallRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? ConnectedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public string RemoteNumber { get; init; } = "";
    public string LocalNumber { get; init; } = "";
    public string Extension { get; init; } = "";
    public string Line { get; init; } = "";
    public CallDirection Direction { get; init; } = CallDirection.Unknown;
    public CallState State { get; init; } = CallState.Unknown;
    public int DurationSeconds { get; init; }
    public string RouterCallId { get; init; } = "";
    public string RouterIdentity { get; init; } = "";
    public string RouterName { get; init; } = "";
    public bool DurationIsApproximate { get; init; }
    public bool IsFromRouter { get; init; }
    public bool IsDemo { get; init; }
    [JsonIgnore] public bool IsActive => State is CallState.Ringing or CallState.Dialing or CallState.Connected;
}

public sealed record Contact
{
    public string Name { get; init; } = "";
    public string Number { get; init; } = "";
    public Contact() { }
    public Contact(string name, string number) { Name = name; Number = number; }
}

public sealed class AppSettings
{
    public string Host { get; set; } = "fritz.box";
    public int Port { get; set; } = 1012;
    public string CountryCode { get; set; } = "+49";
    public string AreaCode { get; set; } = "";
    public bool AlwaysOnTop { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool StartWithWindows { get; set; }
    public bool AutoConnect { get; set; }
    public bool FritzSyncEnabled { get; set; }
    public string FritzUsername { get; set; } = "";
    public int FritzPort { get; set; } = 49000;
    public bool SyncPhonebooks { get; set; } = true;
    public DateTime? LastSuccessfulSync { get; set; }
    public DateTime? LastPhonebookSync { get; set; }
    public bool Notifications { get; set; } = true;
    public bool CompactMode { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 920;
    public double WindowHeight { get; set; } = 700;
}
