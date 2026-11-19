using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace CM.AnrufMonitor.Core;

/// <summary>Unencrypted, local cache of Internet names and their provenance, never addresses or credentials.</summary>
public sealed class ReverseLookupStore
{
    public const int MaximumResults = 10000;
    public const string FileName = "reverse-lookup.json";
    private const int MaximumBytes = 20 * 1024 * 1024;
    private readonly object sync = new();
    private readonly List<string> warnings = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ReverseLookupStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }
    public event Action<string>? Warning;
    public IReadOnlyList<string> Warnings { get { lock (sync) return warnings.ToArray(); } }

    public IReadOnlyList<ReverseLookupResult> Load()
    {
        lock (sync) return Read().ToArray();
    }

    public void Save(IEnumerable<ReverseLookupResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        // Materialize a bounded input before any mutation. Caller mistakes cannot erase a good cache.
        var entries = results.Take(MaximumResults + 1).ToList();
        Validate(entries);
        entries = entries.Select(x => x with { Message = "", RetryAfter = null }).ToList();
        lock (sync)
        {
            // A fresh instance saving before Load must also preserve any unreadable existing file.
            _ = Read();
            string path = Path.Combine(DirectoryPath, FileName);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, new CacheEnvelope { Results = entries }, JsonOptions);
                    if (output.Length > MaximumBytes) throw new InvalidDataException("Der Rückwärtssuche-Cache ist zu groß.");
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private List<ReverseLookupResult> Read()
    {
        string path = Path.Combine(DirectoryPath, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Die Cache-Datei ist unerwartet groß.");
            using var input = File.OpenRead(path);
            var envelope = JsonSerializer.Deserialize<CacheEnvelope>(input, JsonOptions)
                ?? throw new InvalidDataException("Leere Cache-Daten.");
            if (envelope.Version != 1 || envelope.Results is null) throw new InvalidDataException("Unbekanntes Cache-Format.");
            Validate(envelope.Results);
            return envelope.Results;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            string backup = path + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "." + Guid.NewGuid().ToString("N")[..6] + ".broken";
            // If backup fails, propagate instead of replacing the only remaining original.
            File.Move(path, backup);
            string message = $"{FileName} konnte nicht gelesen werden. Die ursprüngliche Datei bleibt als {Path.GetFileName(backup)} erhalten.";
            warnings.Add(message);
            Warning?.Invoke(message);
            return [];
        }
    }

    private static void Validate(List<ReverseLookupResult> entries)
    {
        if (entries.Count > MaximumResults) throw new InvalidDataException("Zu viele gespeicherte Rückwärtssuche-Ergebnisse.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReverseLookupResult entry in entries)
        {
            if (entry is null || entry.Status is not (ReverseLookupStatus.Found or ReverseLookupStatus.NotFound)
                || !ReverseLookupNumbers.IsFullNumber(entry.NormalizedNumber)
                || !ValidProvider(entry.Provider)
                || entry.Name is null || entry.Name.Length > 300 || entry.Name.Any(c => char.IsControl(c)
                    || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                || entry.Status == ReverseLookupStatus.Found && string.IsNullOrWhiteSpace(entry.Name)
                || entry.Status == ReverseLookupStatus.NotFound && entry.Name.Length != 0
                || !ValidSourceUrl(entry.SourceUrl)
                || entry.Message is null || entry.Message.Length > 500
                || entry.CheckedAtUtc < DateTimeOffset.UnixEpoch || entry.ExpiresAtUtc <= entry.CheckedAtUtc
                || entry.ExpiresAtUtc - entry.CheckedAtUtc > TimeSpan.FromDays(366)
                || entry.CheckedAtUtc.Offset != TimeSpan.Zero || entry.ExpiresAtUtc.Offset != TimeSpan.Zero
                || !keys.Add(Key(entry.Provider, entry.NormalizedNumber)))
                throw new InvalidDataException("Ungültige Rückwärtssuche-Ergebnisse.");
        }
    }

    internal static string Key(string provider, string number) => provider + "\n" + number;
    internal static bool ValidProvider(string? provider) => provider is { Length: > 0 and <= 100 }
        && provider.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    internal static bool ValidSourceUrl(string? value) => value is not null && value.Length <= 2048
        && !value.Any(char.IsControl) && (value.Length == 0 || Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0);

    private sealed class CacheEnvelope
    {
        public int Version { get; set; } = 1;
        public List<ReverseLookupResult> Results { get; set; } = [];
    }
}
