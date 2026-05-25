using System.Reflection;

namespace IntuneAssignmentViewer.Services;

public static class VersionInfo
{
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info?.InformationalVersion is { } v)
        {
            // strip git hash suffix if any
            var plusIdx = v.IndexOf('+');
            return plusIdx > 0 ? v[..plusIdx] : v;
        }
        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
