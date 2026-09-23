using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HamLoggerEditor.Services;

/// <summary>
/// Makes Windows open .adi / .adif files with this program when they are double-clicked.
/// Everything is written under HKEY_CURRENT_USER, so no administrator rights are needed and
/// the change affects only the signed-in user. <see cref="Unregister"/> puts it back.
/// </summary>
public static class FileAssociationService
{
    /// <summary>The file type this program owns in the registry.</summary>
    public const string ProgId = "HamLoggerEditor.AdifLog";

    private const string FriendlyTypeName = "ADIF Log";
    private const string ClassesKey = @"Software\Classes";
    private const string PreviousValuePrefix = "PreviousProgId";

    public static readonly string[] Extensions = [".adi", ".adif"];

    /// <summary>The .exe Windows should launch, or null when running under "dotnet run" (nothing to register).</summary>
    public static string? ApplicationPath
    {
        get
        {
            string? path = Environment.ProcessPath;
            return path is null || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                ? null : path;
        }
    }

    public static bool IsSupportedFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>True when every extension currently points at this program's registration.</summary>
    public static bool IsRegistered()
    {
        string? exe = ApplicationPath;
        if (exe is null) return false;
        try
        {
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{ProgId}\shell\open\command");
            if (command?.GetValue(null) as string != CommandLine(exe)) return false;
            foreach (string extension in Extensions)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{extension}");
                if (key?.GetValue(null) as string != ProgId) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Registers this program for .adi and .adif, remembering whatever they pointed at before.</summary>
    public static void Register()
    {
        string exe = ApplicationPath ?? throw new InvalidOperationException(
            "Start the built HamLoggerEditor.exe (in the project's bin folder) and register from there, " +
            "not from a \"dotnet run\" session.");

        using (RegistryKey progId = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{ProgId}"))
        {
            progId.SetValue(null, FriendlyTypeName);
            progId.SetValue("FriendlyTypeName", FriendlyTypeName);
            using (RegistryKey icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (RegistryKey command = progId.CreateSubKey(@"shell\open\command")) command.SetValue(null, CommandLine(exe));

            foreach (string extension in Extensions)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{extension}");
                string? previous = key.GetValue(null) as string;
                if (!string.IsNullOrEmpty(previous) && previous != ProgId)
                    progId.SetValue(PreviousValuePrefix + extension, previous);
                key.SetValue(null, ProgId);
                // Also list the program in "Open with", even if the user later picks something else as default.
                using RegistryKey openWith = key.CreateSubKey("OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
        }
        NotifyShell();
    }

    /// <summary>Undoes <see cref="Register"/>, restoring the previous file type where one was recorded.</summary>
    public static void Unregister()
    {
        using (RegistryKey? progId = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{ProgId}"))
        {
            foreach (string extension in Extensions)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{extension}", writable: true);
                if (key is null) continue;
                if (key.GetValue(null) as string == ProgId)
                {
                    if (progId?.GetValue(PreviousValuePrefix + extension) is string previous && previous.Length > 0)
                        key.SetValue(null, previous);
                    else
                        key.DeleteValue(string.Empty, throwOnMissingValue: false);
                }
                using RegistryKey? openWith = key.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\{ProgId}", throwOnMissingSubKey: false);
        NotifyShell();
    }

    private static string CommandLine(string exe) => $"\"{exe}\" \"%1\"";

    /// <summary>Tells Explorer that file associations changed, so it doesn't need a restart.</summary>
    private static void NotifyShell() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
