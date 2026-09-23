using System.Reflection;

namespace HamLoggerEditor.UI;

/// <summary>
/// The application icon, loaded once from the .ico embedded in the executable (see the csproj).
/// Falls back to the icon Windows already associates with the running .exe, and to nothing at all,
/// because a missing icon must never stop a window from opening.
/// </summary>
public static class AppIcon
{
    private static readonly Lazy<Icon?> Cached = new(Load, isThreadSafe: true);

    public static Icon? Value => Cached.Value;

    private static Icon? Load()
    {
        try
        {
            Assembly assembly = typeof(AppIcon).Assembly;
            string? name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using Stream? stream = assembly.GetManifestResourceStream(name);
                if (stream is not null) return new Icon(stream);
            }
            return Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
