using System.Text.Json;
using System.Text.Json.Serialization;

namespace CM.AnrufMonitor.Core;

public sealed class AppStore
{
    private readonly string directory;
    private readonly List<string> warnings = [];
    private readonly object sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public AppStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }
    public event Action<string>? Warning;
    public IReadOnlyList<string> Warnings => warnings.AsReadOnly();
    public string DirectoryPath => directory;
    public AppSettings LoadSettings() => Read("settings.json", () => new AppSettings(), ValidateSettings);
    public IReadOnlyList<CallRecord> LoadHistory() => Read("history.json", () => new List<CallRecord>(), ValidateHistory);
    public IReadOnlyList<Contact> LoadContacts() => Read("contacts.json", () => new List<Contact>(), ValidateContacts);
    /// <summary>Call only after verifying the router identity; a different router never receives this cache.</summary>
    public IReadOnlyList<Contact> LoadRouterContacts(string routerIdentity)
    {
        if (string.IsNullOrWhiteSpace(routerIdentity)) return Array.Empty<Contact>();
        RouterContactCache cache = Read("router-contacts.json", () => new RouterContactCache(), ValidateRouterContacts);
        return string.Equals(cache.RouterIdentity, routerIdentity, StringComparison.Ordinal)
            ? cache.Contacts.ToArray() : Array.Empty<Contact>();
    }
    public void SaveSettings(AppSettings settings) { ValidateSettings(settings); Write("settings.json", settings); }
    public void SaveHistory(IEnumerable<CallRecord> history)
    {
        var list = history.Where(x => !x.IsDemo).OrderByDescending(x => x.StartedAt).Take(CallTracker.MaximumRecords).ToList();
        ValidateHistory(list); Write("history.json", list);
    }
    public void SaveContacts(IEnumerable<Contact> contacts)
    {
        var list = contacts.ToList(); ValidateContacts(list); Write("contacts.json", list);
    }
    public void SaveRouterContacts(string routerIdentity, IEnumerable<Contact> contacts)
    {
        if (string.IsNullOrWhiteSpace(routerIdentity))
            throw new ArgumentException("Die verifizierte FRITZ!Box-Gerätekennung fehlt.", nameof(routerIdentity));
        ArgumentNullException.ThrowIfNull(contacts);
        var cache = new RouterContactCache { RouterIdentity = routerIdentity, Contacts = contacts.Distinct().ToList() };
        ValidateRouterContacts(cache);
        Write("router-contacts.json", cache);
    }
    public void ClearRouterContacts() => Write("router-contacts.json", new RouterContactCache());

    private T Read<T>(string filename, Func<T> fallback, Action<T> validate)
    {
        lock (sync)
        {
            string path = Path.Combine(directory, filename);
            if (!File.Exists(path)) return fallback();
            try
            {
                if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("Die Datei ist unerwartet groß.");
                using var input = File.OpenRead(path);
                T value = JsonSerializer.Deserialize<T>(input, JsonOptions) ?? throw new InvalidDataException("Leere JSON-Daten.");
                validate(value);
                return value;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
            {
                string backup = path + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "." + Guid.NewGuid().ToString("N")[..6] + ".broken";
                // If preserving the original fails, propagate. Never overwrite unpreserved user data.
                File.Move(path, backup);
                string message = $"{filename} konnte nicht gelesen werden. Die ursprüngliche Datei wurde als {Path.GetFileName(backup)} erhalten. Für diesen Bereich werden zunächst leere Standarddaten verwendet.";
                warnings.Add(message); Warning?.Invoke(message);
                return fallback();
            }
        }
    }

    private void Write<T>(string filename, T value)
    {
        lock (sync)
        {
            string path = Path.Combine(directory, filename);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, value, JsonOptions);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static void ValidateSettings(AppSettings value)
    {
        if (value.Host is null || value.Host.Length > 253 || value.Host.Length == 0 || value.Port is < 1 or > 65535
            || value.FritzUsername is null || value.FritzUsername.Length > 256 || value.FritzUsername.Any(char.IsControl)
            || value.FritzPort is < 1 or > 65535
            || value.CountryCode is null || value.AreaCode is null || value.CountryCode.Length > 8 || value.AreaCode.Length > 30
            || !double.IsFinite(value.Opacity) || value.Opacity is < 0.3 or > 1
            || !double.IsFinite(value.WindowWidth) || !double.IsFinite(value.WindowHeight)
            || value.WindowWidth is < 100 or > 20000 || value.WindowHeight is < 100 or > 20000
            || value.WindowLeft.HasValue && !double.IsFinite(value.WindowLeft.Value)
            || value.WindowTop.HasValue && !double.IsFinite(value.WindowTop.Value))
            throw new InvalidDataException("Ungültige Einstellungen.");
    }
    private static void ValidateHistory(List<CallRecord> values)
    {
        if (values.Count > 10000 || values.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128
            || x.ConnectionId is null || x.RemoteNumber is null || x.LocalNumber is null || x.Extension is null || x.Line is null
            || x.RouterCallId is null || x.RouterCallId.Length > 256
            || x.RouterIdentity is null || x.RouterIdentity.Length > 512 || x.RouterIdentity.Any(char.IsControl)
            || x.RouterName is null || x.RouterName.Length > 300
            || x.RemoteNumber.Length > 256 || x.LocalNumber.Length > 256 || x.DurationSeconds < 0
            || !Enum.IsDefined(x.Direction) || !Enum.IsDefined(x.State)))
            throw new InvalidDataException("Ungültige Anrufdaten.");
    }
    private static void ValidateContacts(List<Contact> values)
    {
        if (values.Count > 50000 || values.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 300
            || string.IsNullOrWhiteSpace(x.Number) || x.Number.Length > 256))
            throw new InvalidDataException("Ungültige Kontaktdaten.");
    }
    private static void ValidateRouterContacts(RouterContactCache value)
    {
        if (value.RouterIdentity is null || value.RouterIdentity.Length > 512 || value.RouterIdentity.Any(char.IsControl)
            || value.Contacts is null || value.RouterIdentity.Length == 0 && value.Contacts.Count > 0)
            throw new InvalidDataException("Ungültige FRITZ!Box-Telefonbuchdaten.");
        ValidateContacts(value.Contacts);
    }
    private sealed class RouterContactCache
    {
        public RouterContactCache() { }
        public string RouterIdentity { get; set; } = "";
        public List<Contact> Contacts { get; set; } = [];
    }
}
