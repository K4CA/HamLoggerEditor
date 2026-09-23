namespace HamLoggerEditor.Services;

/// <summary>
/// The logs opened most recently, newest first, kept in a small text file under the user's
/// AppData folder. All failures are ignored on purpose: a recent list is a convenience, and
/// never a reason for the editor to fail to start or to refuse to open a file.
/// </summary>
public sealed class RecentFilesService
{
    public const int MaxItems = 10;

    private readonly string _filePath;
    private readonly List<string> _items = [];

    public RecentFilesService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HamLoggerEditor", "recent-files.txt");
        Load();
    }

    public IReadOnlyList<string> Items => _items;

    public void Add(string path)
    {
        string full = SafeFullPath(path);
        _items.RemoveAll(p => p.Equals(full, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, full);
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    public void Remove(string path)
    {
        if (_items.RemoveAll(p => p.Equals(SafeFullPath(path), StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            foreach (string line in File.ReadAllLines(_filePath))
            {
                string path = line.Trim();
                if (path.Length == 0 || _items.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                _items.Add(path);
                if (_items.Count == MaxItems) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllLines(_filePath, _items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
