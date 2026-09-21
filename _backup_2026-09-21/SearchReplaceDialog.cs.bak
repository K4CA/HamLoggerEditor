using System.Data;

namespace HamLoggerEditor.Dialogs;

/// <summary>
/// Modeless Find / Replace window for the contact grid. "Replace" changes one cell at a time;
/// "Replace All" is only enabled when "Allow multiple replacements" is checked.
/// </summary>
public sealed class SearchReplaceDialog : Form
{
    private const string AllColumns = "(All columns)";

    private readonly DataGridView grid;
    private readonly Action<string> report;
    private readonly TextBox findBox = new();
    private readonly TextBox replaceBox = new();
    private readonly ComboBox columnBox = new();
    private readonly CheckBox matchCase = new();
    private readonly CheckBox wholeCell = new();
    private readonly CheckBox allowMultiple = new();
    private readonly Button replaceAllButton = new();
    private readonly Label result = new();

    public SearchReplaceDialog(DataGridView grid, Action<string> report)
    {
        this.grid = grid;
        this.report = report;

        Text = "Search and Replace";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 230);

        Controls.Add(new Label { Text = "Find what:", Left = 12, Top = 16, Width = 90 });
        findBox.SetBounds(104, 12, 230, 23);
        Controls.Add(new Label { Text = "Replace with:", Left = 12, Top = 48, Width = 90 });
        replaceBox.SetBounds(104, 44, 230, 23);
        Controls.Add(new Label { Text = "Look in:", Left = 12, Top = 80, Width = 90 });
        columnBox.SetBounds(104, 76, 230, 23);
        columnBox.DropDownStyle = ComboBoxStyle.DropDownList;

        matchCase.Text = "Match case";
        matchCase.SetBounds(104, 108, 230, 22);
        wholeCell.Text = "Match entire cell contents";
        wholeCell.SetBounds(104, 132, 230, 22);
        allowMultiple.Text = "Allow multiple replacements (Replace All)";
        allowMultiple.SetBounds(104, 156, 240, 22);
        allowMultiple.CheckedChanged += (_, _) => replaceAllButton.Enabled = allowMultiple.Checked;

        result.SetBounds(12, 196, 330, 22);
        result.ForeColor = SystemColors.GrayText;

        var findNext = new Button { Text = "Find Next", Left = 350, Top = 11, Width = 108, Height = 26 };
        var replace = new Button { Text = "Replace", Left = 350, Top = 43, Width = 108, Height = 26 };
        replaceAllButton.Text = "Replace All";
        replaceAllButton.SetBounds(350, 75, 108, 26);
        replaceAllButton.Enabled = false;
        var close = new Button { Text = "Close", Left = 350, Top = 192, Width = 108, Height = 26 };

        findNext.Click += (_, _) => FindNext(showNotFound: true);
        replace.Click += (_, _) => ReplaceCurrent();
        replaceAllButton.Click += (_, _) => ReplaceAll();
        close.Click += (_, _) => Hide();

        Controls.AddRange([findBox, replaceBox, columnBox, matchCase, wholeCell, allowMultiple, result,
            findNext, replace, replaceAllButton, close]);
        AcceptButton = findNext;
        CancelButton = close;

        // Keep the window alive so the options are remembered; closing just hides it.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        VisibleChanged += (_, _) => { if (Visible) RefreshColumns(); };
    }

    public void ShowFor(IWin32Window owner, string? initialFind)
    {
        if (!string.IsNullOrEmpty(initialFind)) findBox.Text = initialFind;
        if (!Visible) Show(owner); else Activate();
        findBox.Focus();
        findBox.SelectAll();
    }

    private void RefreshColumns()
    {
        string? selected = columnBox.SelectedItem as string;
        columnBox.Items.Clear();
        columnBox.Items.Add(AllColumns);
        foreach (DataGridViewColumn c in OrderedColumns()) columnBox.Items.Add(c.HeaderText);
        columnBox.SelectedItem = selected is not null && columnBox.Items.Contains(selected) ? selected : AllColumns;
    }

    private List<DataGridViewColumn> OrderedColumns() =>
        grid.Columns.Cast<DataGridViewColumn>()
            .Where(c => c.Visible && !string.IsNullOrEmpty(c.DataPropertyName))
            .OrderBy(c => c.DisplayIndex).ToList();

    private List<DataGridViewColumn> SearchColumns()
    {
        var columns = OrderedColumns();
        return columnBox.SelectedItem is string header && header != AllColumns
            ? columns.Where(c => c.HeaderText == header).ToList()
            : columns;
    }

    private StringComparison Comparison => matchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private bool IsMatch(string value, string find) =>
        wholeCell.Checked ? value.Trim().Equals(find, Comparison) : value.Contains(find, Comparison);

    private string ReplaceIn(string value, string find, string replacement) =>
        wholeCell.Checked ? replacement : value.Replace(find, replacement, Comparison);

    private static string CellText(DataGridViewRow row, DataGridViewColumn column) =>
        row.DataBoundItem is DataRowView drv && drv.Row[column.DataPropertyName] is string s ? s : string.Empty;

    private bool ValidateFind(out string find)
    {
        find = findBox.Text;
        if (find.Length > 0) return true;
        result.Text = "Enter the text to find.";
        findBox.Focus();
        return false;
    }

    private bool FindNext(bool showNotFound)
    {
        if (!ValidateFind(out string find)) return false;
        grid.EndEdit();
        var columns = SearchColumns();
        int rowCount = grid.Rows.Count;
        if (rowCount == 0 || columns.Count == 0) { result.Text = "Nothing to search."; return false; }

        int startRow = grid.CurrentCell?.RowIndex ?? 0;
        int startCol = grid.CurrentCell is null ? -1 : columns.FindIndex(c => c.Index == grid.CurrentCell.ColumnIndex);
        if (startRow < 0) startRow = 0;

        int total = rowCount * columns.Count;
        int startPos = startRow * columns.Count + startCol; // position of the current cell (-1 = before first)
        for (int step = 1; step <= total; step++)
        {
            int pos = ((startPos + step) % total + total) % total;
            DataGridViewRow row = grid.Rows[pos / columns.Count];
            DataGridViewColumn column = columns[pos % columns.Count];
            if (!IsMatch(CellText(row, column), find)) continue;

            grid.ClearSelection();
            grid.CurrentCell = row.Cells[column.Index];
            row.Selected = true;
            result.Text = $"Found in row {row.Index + 1:N0}, column {column.HeaderText}.";
            return true;
        }

        result.Text = $"\"{find}\" was not found.";
        if (showNotFound) System.Media.SystemSounds.Beep.Play();
        return false;
    }

    private void ReplaceCurrent()
    {
        if (!ValidateFind(out string find)) return;
        grid.EndEdit();
        DataGridViewCell? cell = grid.CurrentCell;
        var columns = SearchColumns();
        bool currentIsCandidate = cell is not null && columns.Any(c => c.Index == cell.ColumnIndex);

        if (currentIsCandidate && cell!.OwningRow.DataBoundItem is DataRowView drv)
        {
            DataGridViewColumn column = grid.Columns[cell.ColumnIndex];
            string text = CellText(cell.OwningRow, column);
            if (!column.ReadOnly && IsMatch(text, find))
            {
                drv.Row[column.DataPropertyName] = ReplaceIn(text, find, replaceBox.Text);
                report("Replaced 1 value");
                // Move on to the next match so repeated clicks walk through the log one cell at a time.
                if (!FindNext(showNotFound: false)) result.Text = "Replaced. No more matches.";
                else result.Text = "Replaced. " + result.Text;
                return;
            }
        }
        FindNext(showNotFound: true);
    }

    private void ReplaceAll()
    {
        if (!allowMultiple.Checked || !ValidateFind(out string find)) return;
        grid.EndEdit();

        // Collect first: editing a sorted column would reorder the rows while we walk them.
        var targets = new List<(DataRow Row, string Column, string Value)>();
        var columns = SearchColumns().Where(c => !c.ReadOnly).ToList();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.DataBoundItem is not DataRowView drv) continue;
            foreach (DataGridViewColumn column in columns)
            {
                string text = CellText(row, column);
                if (IsMatch(text, find))
                    targets.Add((drv.Row, column.DataPropertyName, ReplaceIn(text, find, replaceBox.Text)));
            }
        }

        if (targets.Count == 0)
        {
            result.Text = $"\"{find}\" was not found.";
            return;
        }
        if (MessageBox.Show(this, $"Replace \"{find}\" in {targets.Count:N0} cell(s)?", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        foreach (var (row, column, value) in targets) row[column] = value;
        result.Text = $"Replaced {targets.Count:N0} cell(s).";
        report($"Replaced {targets.Count:N0} value(s)");
    }
}
