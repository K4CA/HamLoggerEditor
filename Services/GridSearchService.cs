using System.Data;

namespace HamLoggerEditor.Services;

/// <summary>What to look for and how to match it.</summary>
public sealed record SearchOptions(string Find, string Replacement, bool MatchCase, bool WholeCell)
{
    public StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// An immutable picture of what the grid shows: the rows in on-screen order and the columns to search.
/// It is built on the UI thread (cheap: it copies row references, not values) so the search itself can
/// run on a background thread without touching the DataGridView.
/// </summary>
public sealed class SearchSnapshot(IReadOnlyList<DataRow> rows, IReadOnlyList<string> columns)
{
    public IReadOnlyList<DataRow> Rows { get; } = rows;

    /// <summary>DataTable column names, in on-screen (left-to-right) order.</summary>
    public IReadOnlyList<string> Columns { get; } = columns;

    public long CellCount => (long)Rows.Count * Columns.Count;
}

/// <summary>A matching cell. <see cref="Row"/> is kept (not just the index) so the match survives re-sorting.</summary>
public sealed record CellMatch(DataRow Row, int RowIndex, string Column, string Value);

/// <summary>A value to write back on the UI thread.</summary>
public sealed record CellChange(DataRow Row, string Column, string NewValue);

/// <summary>
/// Search logic with no UI dependencies. Every method only reads the DataRows, reports progress as
/// 0–100 and honours cancellation, so callers can run it with <c>Task.Run</c>.
/// </summary>
public static class GridSearchService
{
    /// <summary>Cap for Find All so a one-letter search can't build a list of millions of rows.</summary>
    public const int MaxFindAllResults = 5_000;

    private const int ProgressEveryCells = 20_000;

    public static bool IsMatch(string value, SearchOptions options) =>
        options.WholeCell
            ? value.Trim().Equals(options.Find, options.Comparison)
            : value.Contains(options.Find, options.Comparison);

    public static string ReplaceIn(string value, SearchOptions options) =>
        options.WholeCell ? options.Replacement : value.Replace(options.Find, options.Replacement, options.Comparison);

    public static string CellText(DataRow row, string column) =>
        row.RowState is DataRowState.Detached or DataRowState.Deleted ? string.Empty : row[column] as string ?? string.Empty;

    /// <summary>
    /// Finds the first match after linear position <paramref name="startPosition"/>
    /// (position = rowIndex * columnCount + columnIndex; pass -1 to start at the first cell), wrapping around once.
    /// </summary>
    public static CellMatch? FindNext(SearchSnapshot snapshot, long startPosition, SearchOptions options,
        IProgress<int>? progress, CancellationToken token)
    {
        long total = snapshot.CellCount;
        if (total == 0 || options.Find.Length == 0) return null;
        int columnCount = snapshot.Columns.Count;
        int lastPercent = -1;

        for (long step = 1; step <= total; step++)
        {
            if (step % ProgressEveryCells == 0)
            {
                token.ThrowIfCancellationRequested();
                Report(progress, step, total, ref lastPercent);
            }
            long position = ((startPosition + step) % total + total) % total;
            int rowIndex = (int)(position / columnCount);
            string column = snapshot.Columns[(int)(position % columnCount)];
            DataRow row = snapshot.Rows[rowIndex];
            string text = CellText(row, column);
            if (IsMatch(text, options)) return new CellMatch(row, rowIndex, column, text);
        }
        return null;
    }

    /// <summary>Every match, in on-screen order, up to <see cref="MaxFindAllResults"/>.</summary>
    public static (List<CellMatch> Matches, bool Truncated) FindAll(SearchSnapshot snapshot, SearchOptions options,
        IProgress<int>? progress, CancellationToken token)
    {
        var matches = new List<CellMatch>();
        if (options.Find.Length == 0) return (matches, false);
        int lastPercent = -1;
        for (int r = 0; r < snapshot.Rows.Count; r++)
        {
            if (r % 500 == 0)
            {
                token.ThrowIfCancellationRequested();
                Report(progress, r, snapshot.Rows.Count, ref lastPercent);
            }
            DataRow row = snapshot.Rows[r];
            foreach (string column in snapshot.Columns)
            {
                string text = CellText(row, column);
                if (!IsMatch(text, options)) continue;
                if (matches.Count >= MaxFindAllResults) return (matches, true);
                matches.Add(new CellMatch(row, r, column, text));
            }
        }
        return (matches, false);
    }

    /// <summary>Works out every replacement without changing anything; the caller applies them on the UI thread.</summary>
    public static List<CellChange> PlanReplaceAll(SearchSnapshot snapshot, SearchOptions options,
        IProgress<int>? progress, CancellationToken token)
    {
        var changes = new List<CellChange>();
        if (options.Find.Length == 0) return changes;
        int lastPercent = -1;
        for (int r = 0; r < snapshot.Rows.Count; r++)
        {
            if (r % 500 == 0)
            {
                token.ThrowIfCancellationRequested();
                Report(progress, r, snapshot.Rows.Count, ref lastPercent);
            }
            DataRow row = snapshot.Rows[r];
            foreach (string column in snapshot.Columns)
            {
                string text = CellText(row, column);
                if (!IsMatch(text, options)) continue;
                string replaced = ReplaceIn(text, options);
                if (!string.Equals(replaced, text, StringComparison.Ordinal))
                    changes.Add(new CellChange(row, column, replaced));
            }
        }
        return changes;
    }

    private static void Report(IProgress<int>? progress, long done, long total, ref int lastPercent)
    {
        if (progress is null || total <= 0) return;
        int percent = (int)(done * 100 / total);
        if (percent == lastPercent) return;
        lastPercent = percent;
        progress.Report(percent);
    }
}
