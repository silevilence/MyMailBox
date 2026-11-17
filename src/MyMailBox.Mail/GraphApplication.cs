using System.Reflection;

namespace MyMailBox.Mail;

/// <summary>Publisher-owned public client registration, shared by all personal-account users.</summary>
public static class GraphApplication
{
    // Injected at build time; actual registration stays outside tracked source files.
    public static string ClientId { get; } = typeof(GraphApplication).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == "GraphClientId")?.Value ?? "";
}
