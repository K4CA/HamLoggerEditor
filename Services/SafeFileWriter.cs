using System.Text;

namespace HamLoggerEditor.Services;

/// <summary>
/// Writes a file all-or-nothing: the content goes to a temporary file next to the target, which only
/// replaces the target once writing has finished. A failure or a cancelled save leaves the original
/// file untouched instead of half-written.
/// </summary>
public static class SafeFileWriter
{
    public static void Write(string path, Encoding encoding, Action<StreamWriter> write)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new StreamWriter(tempPath, false, encoding, 1 << 16))
            {
                write(writer);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Reports "done of total" as a 0–100 percentage, only when the percentage changes.</summary>
public sealed class PercentReporter(IProgress<int>? progress, long total)
{
    private int _lastPercent = -1;

    public void Report(long done)
    {
        if (progress is null || total <= 0) return;
        int percent = (int)Math.Min(100, done * 100 / total);
        if (percent == _lastPercent) return;
        _lastPercent = percent;
        progress.Report(percent);
    }
}

/// <summary>Maps a 0–100 sub-task onto part of an overall bar, e.g. parsing = 0–80%, building = 80–100%.</summary>
public sealed class ProgressRange(IProgress<int> inner, int from, int to) : IProgress<int>
{
    public void Report(int value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 100) / 100);
}
