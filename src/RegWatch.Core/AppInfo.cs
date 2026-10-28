using System.Reflection;

namespace RegWatch.Core;

/// <summary>Version details, all from &lt;Version&gt; in Directory.Build.props.</summary>
public static class AppInfo
{
    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    /// <summary>Numeric version, e.g. "0.1.0" (used in the User-Agent).</summary>
    public static string Version { get; } = Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Version with any pre-release label and without the build's commit hash, e.g. "0.1.0-alpha".</summary>
    public static string DisplayVersion { get; } =
        (Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version).Split('+')[0];
}
