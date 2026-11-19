using System.Reflection;

namespace CM.AnrufMonitor.App;

internal static class AppVersion
{
    public static string Display { get; } = typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "unbekannt";
}
