using System.Data;
using HamLoggerEditor.Services;
using HamLoggerEditor.UI;

namespace HamLoggerEditor.Dialogs;

/// <summary>A column the user can search, in on-screen order.</summary>
public sealed record SearchColumn(string Header, string Column, bool ReadOnly);

/// <summary>What the Search and Replace window needs from the main window. Every member is called on the UI thread.</summary>
public interface ISearchHost
{
    /// <summary>True while a background operation (load, save, search…) is running; the data must not be changed then.</summary>
    bool IsBusy { get; }

    IReadOnlyList<SearchColumn> GetSearchColumns();

    /// <summary>The rows currently shown, in on-screen order, and the given columns.</summary>
    SearchSnapshot CreateSnapshot(IReadOnlyList<string> columns);

    /// <summary>The current cell: its on-screen row index, its DataRow and its DataTable column name.</summary>
    (int RowIndex, DataRow? Row, string? Column) GetCurrentCell();

    /// <summary>Selects and scrolls to a cell. Returns false when the row is no longer shown.</summary>
    bool SelectCell(DataRow row, string column);

    /// <summary>Starts a background operation and locks editing; null when another operation is running.</summary>
    StatusOperation? BeginOperation(string caption);

    /// <summary>Writes the values (bulk, one repaint) and marks the log as changed.</summary>
    void ApplyChanges(IReadOnlyList<CellChange> changes, string description);
}

/// <summary>
/// Modeless Find / Replace window. Searching runs on a background thread over a snapshot of the rows,
/// so the window and the grid stay responsive, with a progress bar and a Stop button for long logs.
/// "Replace" changes one cell at a time; "Replace All" is enabled only when "Allow multiple replacements" is checked.
/// </summary>
public sealed class SearchReplaceDialog : Form
{
    private const string AllColumns = "(All columns)";

    private readonly ISearchHost _host;
    private readonly TextBox _findTextBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _replaceTextBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _columnComboBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _matchCaseCheckBox = new() { Text = "Match case", AutoSize = true };
    private readonly CheckBox _wholeCellCheckBox = new() { Text = "Match entire cell contents", AutoSize = true };
    private readonly CheckBox _allowMultipleCheckBox = new() { Text = "Allow multiple replacements (Replace All)", AutoSize = true };
    private readonly Button _findNextButton = NewButton("Find Next");
    private readonly Button _replaceButton = NewButton("Replace");
    private readonly Button _replaceAllButton = NewButton("Replace All");
    private readonly Button _findAllButton = NewButton("Find All");
    private readonly Button _closeButton = NewButton("Close");
    private readonly Button _stopButton = NewButton("Stop");
    private readonly ProgressBar _searchProgressBar = new() { Dock = DockStyle.Fill, Height = 16, Visible = false };
    private readonly Label _resultLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ListView _resultsListView = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, Visible = false, MinimumSize = new Size(0, 120)
    };

    private StatusOperation? _operation;
    private List<CellMatch> _findAllMatches = [];

    public SearchReplaceDialog(ISearchHost host)
    {
        _host = host;

        Text = "Search and Replace";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 300);
        MinimumSize = new Size(480, 320);

        _resultsListView.Columns.Add("Row", 60, HorizontalAlignment.Right);
        _resultsListView.Columns.Add("Column", 110);
        _resultsListView.Columns.Add("Value", 300);
        _resultsListView.ItemActivate += (_, _) => GoToSelectedResult();
        _resultsListView.SelectedIndexChanged += (_, _) => GoToSelectedResult();

        _replaceAllButton.Enabled = false;
        _stopButton.Enabled = false;
        _allowMultipleCheckBox.CheckedChanged += (_, _) => UpdateButtons();

        _findNextButton.Click += async (_, _) => await RunSafelyAsync(() => FindNextAsync(beepIfMissing: true));
        _replaceButton.Click += async (_, _) => await RunSafelyAsync(ReplaceCurrentAsync);
        _replaceAllButton.Click += async (_, _) => await RunSafelyAsync(ReplaceAllAsync);
        _findAllButton.Click += async (_, _) => await RunSafelyAsync(FindAllAsync);
        _stopButton.Click += (_, _) => _operation?.Cancel();
        _closeButton.Click += (_, _) => Hide();

        Controls.Add(BuildLayout());
        AcceptButton = _findNextButton;
        CancelButton = _closeButton;

        // Keep the window alive so the options are remembered; closing just hides it.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        VisibleChanged += (_, _) => { if (Visible) RefreshColumns(); };
    }

    public void ShowFor(IWin32Window owner)
    {
        if (!Visible) Show(owner); else Activate();
        _findTextBox.Focus();
        _findTextBox.SelectAll();
    }

    private static Button NewButton(string text) =>
        new() { Text = text, Dock = DockStyle.Fill, MinimumSize = new Size(100, 26), Margin = new Padding(6, 2, 0, 2) };

    private TableLayoutPanel BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10), AutoSize = false };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };

        void AddRow(Control? first, Control? second, Control? third, SizeType sizeType = SizeType.AutoSize, float height = 0)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(sizeType, height));
            if (first is not null) layout.Controls.Add(first, 0, row);
            if (second is not null) layout.Controls.Add(second, 1, row);
            if (third is not null) layout.Controls.Add(third, 2, row);
        }

        AddRow(Caption("Find what:"), _findTextBox, _findNextButton);
        AddRow(Caption("Replace with:"), _replaceTextBox, _replaceButton);
        AddRow(Caption("Look in:"), _columnComboBox, _replaceAllButton);
        AddRow(null, _matchCaseCheckBox, _findAllButton);
        AddRow(null, _wholeCellCheckBox, _stopButton);
        AddRow(null, _allowMultipleCheckBox, _closeButton);
        AddRow(null, _searchProgressBar, null);
        AddRow(_resultLabel, null, null, SizeType.Absolute, 24);
        layout.SetColumnSpan(_resultLabel, 3);
        AddRow(_resultsListView, null, null, SizeType.Percent, 100);
        layout.SetColumnSpan(_resultsListView, 3);
        return layout;
    }

    private void RefreshColumns()
    {
        string? selected = _columnComboBox.SelectedItem as string;
        _columnComboBox.Items.Clear();
        _columnComboBox.Items.Add(AllColumns);
        foreach (SearchColumn column in _host.GetSearchColumns()) _columnComboBox.Items.Add(column.Header);
        _columnComboBox.SelectedItem = selected is not null && _columnComboBox.Items.Contains(selected) ? selected : AllColumns;
    }

    private List<SearchColumn> SelectedColumns(bool writableOnly)
    {
        var columns = _host.GetSearchColumns().Where(c => !writableOnly || !c.ReadOnly).ToList();
        return _columnComboBox.SelectedItem is string header && header != AllColumns
            ? columns.Where(c => c.Header == header).ToList()
            : columns;
    }

    private bool TryGetOptions(out SearchOptions options)
    {
        options = new SearchOptions(_findTextBox.Text, _replaceTextBox.Text, _matchCaseCheckBox.Checked, _wholeCellCheckBox.Checked);
        if (options.Find.Length > 0) return true;
        _resultLabel.Text = "Enter the text to find.";
        _findTextBox.Focus();
        return false;
    }

    // ---- background operation plumbing ------------------------------------------------------

    private async Task RunSafelyAsync(Func<Task> action)
    {
        if (_host.IsBusy)
        {
            _resultLabel.Text = "Another operation is running. Try again when it finishes.";
            return;
        }
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Runs <paramref name="work"/> on a background thread with progress in this window and the status bar.</summary>
    private async Task<(bool Completed, T? Result)> RunInBackgroundAsync<T>(string caption, Func<IProgress<int>, CancellationToken, T> work)
    {
        StatusOperation? operation = _host.BeginOperation(caption);
        if (operation is null)
        {
            _resultLabel.Text = "Another operation is running. Try again when it finishes.";
            return (false, default);
        }

        _operation = operation;
        SetBusy(true);
        var progress = new Progress<int>(percent =>
        {
            _searchProgressBar.Value = Math.Clamp(percent, 0, 100);
            operation.Progress.Report(percent);
        });
        try
        {
            T result = await Task.Run(() => work(progress, operation.Token), operation.Token);
            return (true, result);
        }
        catch (OperationCanceledException)
        {
            _resultLabel.Text = "Search stopped.";
            return (false, default);
        }
        finally
        {
            _operation = null;
            operation.Dispose();
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _searchProgressBar.Value = 0;
        _searchProgressBar.Visible = busy;
        _stopButton.Enabled = busy;
        _findNextButton.Enabled = _replaceButton.Enabled = _findAllButton.Enabled = !busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    private void UpdateButtons() => _replaceAllButton.Enabled = _operation is null && _allowMultipleCheckBox.Checked;

    // ---- commands -----------------------------------------------------------------------------

    private async Task<bool> FindNextAsync(bool beepIfMissing)
    {
        if (!TryGetOptions(out SearchOptions options)) return false;
        var columns = SelectedColumns(writableOnly: false).Select(c => c.Column).ToList();
        SearchSnapshot snapshot = _host.CreateSnapshot(columns);
        if (snapshot.CellCount == 0)
        {
            _resultLabel.Text = "Nothing to search.";
            return false;
        }

        // Start just after the current cell (or at the start of its row if it's in a column not searched).
        var (rowIndex, _, currentColumn) = _host.GetCurrentCell();
        long start = -1;
        if (rowIndex >= 0 && rowIndex < snapshot.Rows.Count)
        {
            int columnIndex = currentColumn is null ? -1 : columns.IndexOf(currentColumn);
            start = (long)rowIndex * columns.Count + columnIndex;
        }

        _resultLabel.Text = "Searching…";
        var (completed, match) = await RunInBackgroundAsync("Searching",
            (progress, token) => GridSearchService.FindNext(snapshot, start, options, progress, token));
        if (!completed) return false;

        if (match is null)
        {
            _resultLabel.Text = $"\"{options.Find}\" was not found.";
            if (beepIfMissing) System.Media.SystemSounds.Beep.Play();
            return false;
        }
        if (!_host.SelectCell(match.Row, match.Column))
        {
            _resultLabel.Text = "The match is no longer shown in the grid. Search again.";
            return false;
        }
        _resultLabel.Text = $"Found in row {match.RowIndex + 1:N0}, column {HeaderFor(match.Column)}.";
        return true;
    }

    private async Task ReplaceCurrentAsync()
    {
        if (!TryGetOptions(out SearchOptions options)) return;
        var (_, row, column) = _host.GetCurrentCell();
        var writable = SelectedColumns(writableOnly: true);

        if (row is not null && column is not null && writable.Any(c => c.Column == column))
        {
            string text = GridSearchService.CellText(row, column);
            if (GridSearchService.IsMatch(text, options))
            {
                _host.ApplyChanges([new CellChange(row, column, GridSearchService.ReplaceIn(text, options))], "Replaced 1 value");
                // Move on to the next match so repeated clicks walk through the log one cell at a time.
                bool more = await FindNextAsync(beepIfMissing: false);
                _resultLabel.Text = more ? "Replaced. " + _resultLabel.Text : "Replaced. No more matches.";
                return;
            }
        }
        await FindNextAsync(beepIfMissing: true);
    }

    private async Task ReplaceAllAsync()
    {
        if (!_allowMultipleCheckBox.Checked || !TryGetOptions(out SearchOptions options)) return;
        var columns = SelectedColumns(writableOnly: true).Select(c => c.Column).ToList();
        SearchSnapshot snapshot = _host.CreateSnapshot(columns);

        _resultLabel.Text = "Finding values to replace…";
        var (completed, changes) = await RunInBackgroundAsync("Preparing Replace All",
            (progress, token) => GridSearchService.PlanReplaceAll(snapshot, options, progress, token));
        if (!completed || changes is null) return;

        if (changes.Count == 0)
        {
            _resultLabel.Text = $"\"{options.Find}\" was not found.";
            return;
        }
        if (MessageBox.Show(this, $"Replace \"{options.Find}\" in {changes.Count:N0} cell(s)?", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            _resultLabel.Text = "Replace All cancelled.";
            return;
        }

        _host.ApplyChanges(changes, $"Replaced {changes.Count:N0} value(s)");
        _resultLabel.Text = $"Replaced {changes.Count:N0} cell(s).";
    }

    private async Task FindAllAsync()
    {
        if (!TryGetOptions(out SearchOptions options)) return;
        var columns = SelectedColumns(writableOnly: false).Select(c => c.Column).ToList();
        SearchSnapshot snapshot = _host.CreateSnapshot(columns);

        _resultLabel.Text = "Searching…";
        var (completed, result) = await RunInBackgroundAsync("Finding all",
            (progress, token) => GridSearchService.FindAll(snapshot, options, progress, token));
        if (!completed) return;

        _findAllMatches = result.Matches;
        var headers = _host.GetSearchColumns().ToDictionary(c => c.Column, c => c.Header);
        _resultsListView.BeginUpdate();
        _resultsListView.Items.Clear();
        _resultsListView.Items.AddRange(_findAllMatches.Select(m => new ListViewItem(
        [
            (m.RowIndex + 1).ToString("N0"),
            headers.TryGetValue(m.Column, out string? h) ? h : m.Column,
            m.Value.ReplaceLineEndings(" ")
        ])).ToArray());
        _resultsListView.EndUpdate();

        if (!_resultsListView.Visible)
        {
            _resultsListView.Visible = true;
            if (ClientSize.Height < 420) ClientSize = new Size(ClientSize.Width, 420);
        }
        _resultLabel.Text = result.Matches.Count == 0
            ? $"\"{options.Find}\" was not found."
            : result.Truncated
                ? $"Showing the first {result.Matches.Count:N0} matches. Narrow the search to see the rest."
                : $"{result.Matches.Count:N0} match(es). Click one to go to it.";
    }

    private void GoToSelectedResult()
    {
        if (_resultsListView.SelectedIndices.Count == 0) return;
        int index = _resultsListView.SelectedIndices[0];
        if (index < 0 || index >= _findAllMatches.Count) return;
        CellMatch match = _findAllMatches[index];
        if (!_host.SelectCell(match.Row, match.Column))
            _resultLabel.Text = "That row is no longer shown (deleted or filtered out).";
    }

    private string HeaderFor(string column) =>
        _host.GetSearchColumns().FirstOrDefault(c => c.Column == column)?.Header ?? column;
}
