namespace CM.AnrufMonitor.Core;

/// <summary>Connection inputs used only in memory. This type must never be serialized to settings.</summary>
public sealed record FritzAccessOptions
{
    public string Host { get; init; } = "fritz.box";
    public int Port { get; init; } = 49000;
    public string CountryCode { get; init; } = "+49";
    public string AreaCode { get; init; } = "";
    public string Username { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Password { get; init; } = "";
    public override string ToString() => "FRITZ!Box access options (credentials omitted)";
}

public sealed record FritzCallEntry
{
    public string Id { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public string RemoteNumber { get; init; } = "";
    public string LocalNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public CallDirection Direction { get; init; } = CallDirection.Unknown;
    public CallState State { get; init; } = CallState.Unknown;
    public int DurationSeconds { get; init; }
    public bool DurationIsApproximate { get; init; } = true;
    public int Type { get; init; }
}

public sealed record FritzCallListSnapshot
{
    public string RouterIdentity { get; init; } = "";
    /// <summary>The router's raw list timestamp; it is not guaranteed to be a factory-reset generation.</summary>
    public string SnapshotIdentity { get; init; } = "";
    public IReadOnlyList<FritzCallEntry> Calls { get; init; } = Array.Empty<FritzCallEntry>();
}

/// <summary>A user-facing error deliberately excluding router URLs, session IDs and credentials.</summary>
public sealed class FritzTr064Exception : IOException
{
    public FritzTr064Exception(string message) : base(message) { }
}
