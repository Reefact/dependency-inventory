using System.Reflection;

namespace DependencyInventory;

public static class AppVersion
{
    /// <summary>
    /// The version the package was built with (the release tag), without the source revision SourceLink appends after '+'.
    /// </summary>
    public static string Current { get; } = Resolve(typeof(AppVersion).Assembly);

    public static string Resolve(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var metadata = version.IndexOf('+');
        return metadata >= 0 ? version[..metadata] : version;
    }
}
