using System.Data;
using System.Globalization;
using HamLoggerEditor.Adif;
using HamLoggerEditor.Dialogs;
using HamLoggerEditor.Export;
using HamLoggerEditor.Rules;
using HamLoggerEditor.Services;
using HamLoggerEditor.UI;

namespace HamLoggerEditor;

public sealed class MainForm : Form, ISearchHost
{
    private const string AppTitle = "HamLogger Contact Editor  Version 1.3";

    // Internal (never saved) columns. ADIF field names cannot start with an underscore.
    private const string SourceColumn = "__SOURCE_FILE";
    private const string DupColumn = "__DUPGROUP";
    private const string SortColumn = "__SORTKEY";
    private const string OriginalNameKey = "AdifOriginalName";

    /// <summary>ADIF mode written for FT4 / FT2 contacts (FT4 and FT2 are submodes of MFSK).</summary>
    private const string MfskMode = "MFSK";

    private static readonly string[] DefaultFields =
    [
        "CALL", "QSO_DATE", "TIME_ON", "BAND", "MODE", "SUBMODE", "FREQ", "RST_SENT", "RST_RCVD",
        "NAME", "QTH", "GRIDSQUARE", "STATE", "COUNTRY", "COMMENT"
    ];

    // Fields that are loaded but never shown (the old editor's internal row ID).
    private static readonly HashSet<string> IgnoredFields = new(StringComparer.OrdinalIgnoreCase) { "APP_HAMLOGGER_ID" };

    private static readonly Color DupColorA = Color.FromArgb(255, 199, 179);
    private static readonly Color DupColorB = Color.FromArgb(255, 232, 150);
    private static readonly Color SourceColor = Color.FromArgb(235, 242, 255);

    private readonly BindingSource source = new();
    private readonly DataGridView grid = new();
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ContextMenuStrip headerMenu = new();
    private readonly ToolStripMenuItem showDupsOnlyItem = new("Show &Duplicates Only");
    private readonly ToolStripMenuItem removeSourceItem = new("Remove &Source File Column");
    private SearchReplaceDialog? searchDialog;
    private readonly MenuStrip _mainMenuStrip;
    private readonly ToolStrip _mainToolStrip;
    private readonly StatusProgress _statusProgress;
    private readonly RecentFilesService _recentFiles = new();
    private readonly ToolStripMenuItem _recentFilesItem = new("Recent &Files");
    private readonly ToolStripMenuItem _associateItem = new("Open .adi Files With This Program") { CheckOnClick = false };
    private readonly MainWindowContext? _windowContext;
    private readonly string? _initialPath;
    private bool _closeConfirmed;

    private DataTable table = new();
    private string? currentPath;
    private bool dirty;
    private bool mergedSinceSave;
    private bool suppressEvents;
    private DataGridViewColumn? headerMenuColumn;

    public MainForm() : this(null, null) { }

    /// <param name="windowContext">Owner of all editor windows; null means this window stands alone.</param>
    /// <param name="initialPath">An ADIF file to load once the window is shown (Explorer double-click).</param>
    public MainForm(MainWindowContext? windowContext, string? initialPath)
    {
        _windowContext = windowContext;
        _initialPath = initialPath;
        Text = AppTitle;
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1450;
        Height = 750;

        var menu = _mainMenuStrip = BuildMenu();
        var toolBar = _mainToolStrip = BuildToolBar();
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(status);
        _statusProgress = new StatusProgress(statusStrip);
        _statusProgress.BusyChanged += (_, busy) => OnBusyChanged(busy);
        BuildHeaderMenu();

        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToOrderColumns = true;   // drag column headers left / right
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowHeadersWidth = 28;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
        grid.DataError += Grid_DataError;
        grid.CellFormatting += Grid_CellFormatting;
        grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
        grid.ColumnDisplayIndexChanged += (_, _) => { if (!suppressEvents) MarkDirty(); };
        grid.CellBeginEdit += (_, e) => { if (_statusProgress.IsBusy) e.Cancel = true; }; // no edits while a background job reads the data
        typeof(DataGridView).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);

        // Dropping a log file on the grid opens it; hold Shift to merge it into the open log instead.
        grid.AllowDrop = true;
        grid.DragEnter += Grid_DragEnter;
        grid.DragOver += Grid_DragEnter;   // the effect must be set for every DragOver, not just on entry
        grid.DragDrop += Grid_DragDrop;

        Controls.Add(grid);
        Controls.Add(toolBar);
        Controls.Add(menu);
        Controls.Add(statusStrip);
        MainMenuStrip = menu;
        FormClosing += MainForm_FormClosing;
        Disposed += (_, _) => _statusProgress.Dispose();

        LoadTable(CreateTable(DefaultFields), autoSize: false);
        UpdateTitle();
        UpdateStatus("Ready");

        if (_initialPath is not null)
        {
            // Load the file Explorer passed us once the window is on screen (so progress is visible).
            void LoadInitialFile(object? sender, EventArgs e)
            {
                Shown -= LoadInitialFile;
                RunCommand(() => OpenPathAsync(_initialPath));
            }
            Shown += LoadInitialFile;
        }
    }

    #region UI construction

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&New", Keys.Control | Keys.N, NewFileAsync));
        file.DropDownItems.Add(Item("&Open ADIF...", Keys.Control | Keys.O, OpenFileAsync));
        file.DropDownItems.Add(Item("&Merge ADIF File...", Keys.Control | Keys.M, MergeFileAsync));
        file.DropDownItems.Add(Item("&Update...", Keys.Control | Keys.U, UpdateFileAsync));
        file.DropDownItems.Add(Item("New &Window", Keys.Control | Keys.Shift | Keys.N, OpenNewWindow));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("&Save", Keys.Control | Keys.S, () => SaveFileAsync(false)));
        file.DropDownItems.Add(Item("Save &As...", Keys.Control | Keys.Shift | Keys.S, () => SaveFileAsync(true)));
        file.DropDownItems.Add(Item("Save Se&lected Rows As...", Keys.None, SaveSelectedRowsAsync));
        file.DropDownItems.Add(new ToolStripSeparator());
        var export = new ToolStripMenuItem("&Export");
        export.DropDownItems.Add(Item("&CSV File...", Keys.None, ExportCsvAsync));
        export.DropDownItems.Add(Item("S&QLite INSERT Script...", Keys.None, () => ExportSqlAsync(SqlDialect.SQLite)));
        export.DropDownItems.Add(Item("SQL &Server INSERT Script...", Keys.None, () => ExportSqlAsync(SqlDialect.SqlServer)));
        file.DropDownItems.Add(export);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(_recentFilesItem);
        _associateItem.Click += (_, _) => RunCommand(ToggleFileAssociation);
        file.DropDownItems.Add(_associateItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&xit", Keys.None, Close));
        file.DropDownOpening += (_, _) =>
        {
            BuildRecentFilesMenu();
            bool registered = FileAssociationService.IsRegistered();
            _associateItem.Checked = registered;
            _associateItem.Text = registered
                ? "Stop Opening .adi Files With This Program"
                : "Open .adi Files With This Program";
        };

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Item("&Add Contact", Keys.Control | Keys.Insert, AddContact));
        edit.DropDownItems.Add(Item("&Delete Selected Rows", Keys.None, DeleteSelected));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("&Search and Replace...", Keys.Control | Keys.H, ShowSearchReplace));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Find D&uplicate QSOs", Keys.Control | Keys.D, FindDuplicates));
        showDupsOnlyItem.Click += (_, _) => SetDuplicateFilter(!showDupsOnlyItem.Checked);
        edit.DropDownItems.Add(showDupsOnlyItem);
        edit.DropDownItems.Add(Item("&Clear Duplicate Highlights", Keys.None, ClearDuplicates));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Apply Conditional &Rule...", Keys.Control | Keys.R, ApplyConditionalRule));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("TEST-FT&8  (COMMENT → MODE)", Keys.None, () => RunModeTest("FT8", "FT8", null)));
        edit.DropDownItems.Add(Item("TEST-FT&4  (COMMENT → MFSK / FT4)", Keys.None, () => RunModeTest("FT4", MfskMode, "FT4")));
        edit.DropDownItems.Add(Item("TEST-FT&2  (COMMENT → MFSK / FT2)", Keys.None, () => RunModeTest("FT2", MfskMode, "FT2")));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Item("Sort by Date/Time &Ascending", Keys.None, () => SortByDateTime(true)));
        view.DropDownItems.Add(Item("Sort by Date/Time &Descending", Keys.None, () => SortByDateTime(false)));
        view.DropDownItems.Add(Item("&Remove Sort", Keys.None, RemoveSort));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("Auto-size &Columns", Keys.None, AutoSizeColumns));

        var columns = new ToolStripMenuItem("&Columns");
        columns.DropDownItems.Add(Item("&Manage Columns (add / reorder / rename / delete)...", Keys.None, ManageColumns));
        removeSourceItem.Click += (_, _) => RemoveSourceColumn();
        columns.DropDownItems.Add(removeSourceItem);
        columns.DropDownItems.Add(new ToolStripSeparator());
        columns.DropDownItems.Add(new ToolStripMenuItem("Tip: right-click a column header to rename, delete or move it; drag headers to reorder.") { Enabled = false });
        columns.DropDownOpening += (_, _) => removeSourceItem.Enabled = table.Columns.Contains(SourceColumn);

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, columns });
        return menu;
    }

    private ToolStrip BuildToolBar()
    {
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        bar.Items.Add(Button("Open ADIF", "Open an ADIF file; its field names become the columns", OpenFileAsync));
        bar.Items.Add(Button("Merge File", "Add the rows of another ADIF file (tagged with its file name)", MergeFileAsync));
        bar.Items.Add(Button("Update", "Overwrite matching contacts from another ADIF file, and add contacts that are not already in the log (same QSO_DATE, TIME_ON, CALL, BAND and MODE).", UpdateFileAsync));
        bar.Items.Add(Button("Save ADIF", "Save all rows", () => SaveFileAsync(false)));
        bar.Items.Add(Button("Save Selected", "Save only the selected rows to a new ADIF file", SaveSelectedRowsAsync));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("Add Contact", "Add a new row", AddContact));
        bar.Items.Add(Button("Delete Selected", "Delete the selected rows", DeleteSelected));
        bar.Items.Add(Button("Search/Replace", "Search and replace (Ctrl+H)", ShowSearchReplace));
        bar.Items.Add(Button("Conditional Rule", "Apply an IF/THEN rule to matching rows (Ctrl+R)", ApplyConditionalRule));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("Date/Time ▲", "Sort by QSO_DATE + TIME_ON, oldest first", () => SortByDateTime(true)));
        bar.Items.Add(Button("Date/Time ▼", "Sort by QSO_DATE + TIME_ON, newest first", () => SortByDateTime(false)));
        bar.Items.Add(Button("Columns...", "Add, reorder, rename or delete columns", ManageColumns));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("Find Duplicates", "Highlight rows with the same QSO_DATE, TIME_ON, BAND, MODE and CALL", FindDuplicates));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("TEST-FT8", "Rows whose COMMENT contains FT8: MODE = FT8", () => RunModeTest("FT8", "FT8", null)));
        bar.Items.Add(Button("TEST-FT4", "Rows whose COMMENT contains FT4: MODE = MFSK, SUBMODE = FT4", () => RunModeTest("FT4", MfskMode, "FT4")));
        bar.Items.Add(Button("TEST-FT2", "Rows whose COMMENT contains FT2: MODE = MFSK, SUBMODE = FT2", () => RunModeTest("FT2", MfskMode, "FT2")));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("Export CSV", "Export all rows to a CSV file", ExportCsvAsync));
        bar.Items.Add(Button("SQLite Script", "Create a SQLite INSERT script", () => ExportSqlAsync(SqlDialect.SQLite)));
        bar.Items.Add(Button("SQL Server Script", "Create a SQL Server INSERT script", () => ExportSqlAsync(SqlDialect.SqlServer)));
        return bar;
    }

    private void BuildHeaderMenu()
    {
        var rename = new ToolStripMenuItem("&Rename Column...", null, (_, _) => RenameColumn(headerMenuColumn));
        var delete = new ToolStripMenuItem("&Delete Column", null, (_, _) => DeleteColumn(headerMenuColumn));
        var left = new ToolStripMenuItem("Move &Left", null, (_, _) => MoveColumn(headerMenuColumn, -1));
        var right = new ToolStripMenuItem("Move Righ&t", null, (_, _) => MoveColumn(headerMenuColumn, +1));
        headerMenu.Items.AddRange(new ToolStripItem[]
        {
            rename, delete, new ToolStripSeparator(), left, right,
            new ToolStripSeparator(), new ToolStripMenuItem("&Manage Columns...", null, (_, _) => ManageColumns())
        });
        headerMenu.Opening += (_, _) =>
        {
            bool isSource = headerMenuColumn?.DataPropertyName == SourceColumn;
            rename.Enabled = headerMenuColumn is not null && !isSource;
            delete.Text = isSource ? "Remove &Source File Column" : $"&Delete Column {headerMenuColumn?.HeaderText}";
        };
    }

    private ToolStripMenuItem Item(string text, Keys keys, Action action)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => RunCommand(action));
        if (keys != Keys.None) item.ShortcutKeys = keys;
        return item;
    }

    private ToolStripMenuItem Item(string text, Keys keys, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => RunCommand(action));
        if (keys != Keys.None) item.ShortcutKeys = keys;
        return item;
    }

    private ToolStripButton Button(string text, string tip, Action action) =>
        new(text, null, (_, _) => RunCommand(action)) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };

    private ToolStripButton Button(string text, string tip, Func<Task> action) =>
        new(text, null, (_, _) => RunCommand(action)) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };

    /// <summary>Top-level entry point for menu/toolbar commands: ignored while busy, errors shown instead of crashing.</summary>
    private void RunCommand(Action action)
    {
        if (_statusProgress.IsBusy) return;
        try { action(); }
        catch (Exception ex) { ShowError("Unexpected error", ex); }
    }

    /// <summary>Async version: <c>async void</c> is acceptable only here, at the UI event boundary.</summary>
    private async void RunCommand(Func<Task> action)
    {
        if (_statusProgress.IsBusy) return;
        try { await action(); }
        catch (Exception ex) { ShowError("Unexpected error", ex); }
    }

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>While a background job runs, commands that would change the log are disabled; the window stays responsive.</summary>
    private void OnBusyChanged(bool busy)
    {
        _mainMenuStrip.Enabled = !busy;
        _mainToolStrip.Enabled = !busy;
        grid.Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        if (busy) grid.EndEdit();
    }

    /// <summary>Starts a status-bar operation, or returns null (after telling the user) when one is already running.</summary>
    private StatusOperation? BeginOperation(string caption)
    {
        if (!_statusProgress.IsBusy) return _statusProgress.Begin(caption);
        UpdateStatus("Please wait for the current operation to finish");
        return null;
    }

    private static string Took(StatusOperation operation) => StatusProgress.FormatElapsed(operation.Elapsed);

    #endregion

    #region Table and grid plumbing

    private static bool IsInternal(string columnName) => columnName.StartsWith("__", StringComparison.Ordinal);

    private static DataTable CreateTable(IEnumerable<string> fieldNames)
    {
        var t = new DataTable("Contacts") { CaseSensitive = false };
        t.Columns.Add(new DataColumn(DupColumn, typeof(int)) { DefaultValue = 0 });
        t.Columns.Add(new DataColumn(SortColumn, typeof(string)));
        foreach (string field in fieldNames)
        {
            if (IgnoredFields.Contains(field) || t.Columns.Contains(field)) continue;
            AddDataColumn(t, field);
        }
        return t;
    }

    private static DataColumn AddDataColumn(DataTable t, string name)
    {
        var column = new DataColumn(name.ToUpperInvariant(), typeof(string));
        column.ExtendedProperties[OriginalNameKey] = name.ToUpperInvariant();
        t.Columns.Add(column);
        return column;
    }

    private IEnumerable<DataColumn> FieldColumns() =>
        table.Columns.Cast<DataColumn>().Where(c => !IsInternal(c.ColumnName));

    /// <summary>Finds a field column by its current name, or by the ADIF name it was loaded with (if renamed).</summary>
    private DataColumn? FindColumn(string adifName)
    {
        var columns = FieldColumns().ToList();
        return columns.FirstOrDefault(c => c.ColumnName.Equals(adifName, StringComparison.OrdinalIgnoreCase))
            ?? columns.FirstOrDefault(c => adifName.Equals(c.ExtendedProperties[OriginalNameKey] as string, StringComparison.OrdinalIgnoreCase));
    }

    private List<DataGridViewColumn> OrderedGridColumns() =>
        grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).ToList();

    /// <summary>The ADIF field columns (not the temporary Source File column) in on-screen order.</summary>
    private List<ColumnSpec> CurrentLayout() =>
        OrderedGridColumns()
            .Where(c => c.DataPropertyName != SourceColumn)
            .Select(c => new ColumnSpec(c.DataPropertyName, c.DataPropertyName, c.Width))
            .ToList();

    private void LoadTable(DataTable newTable, bool autoSize)
    {
        table.ColumnChanged -= Table_ColumnChanged;
        table = newTable;
        table.ColumnChanged += Table_ColumnChanged;
        showDupsOnlyItem.Checked = false;
        RebuildGrid(FieldColumns().Select(c => new ColumnSpec(c.ColumnName, c.ColumnName, 100)).ToList(), keepSortAndFilter: false);
        if (autoSize) AutoSizeColumns();
    }

    /// <summary>
    /// Applies a column layout: deletes table columns not listed, renames, creates new ones (Key == null),
    /// and rebuilds the grid columns in the listed order. The Source File column, if present, stays leftmost.
    /// </summary>
    private void RebuildGrid(IReadOnlyList<ColumnSpec> layout, bool keepSortAndFilter = true, Action? schemaChange = null)
    {
        GridState state = UnbindGrid(keepSortAndFilter);
        try
        {
            schemaChange?.Invoke(); // runs while nothing is bound, so bulk edits don't repaint the grid
        }
        finally
        {
            RebindGrid(layout, state);
        }
    }

    /// <summary>What <see cref="UnbindGrid"/> saved so <see cref="RebindGrid"/> can restore it.</summary>
    private sealed record GridState(string Sort, string Filter, int SourceWidth);

    /// <summary>
    /// Detaches the table from the grid. Until <see cref="RebindGrid"/> runs, nothing on screen observes
    /// the table, so it may be filled on a background thread (with <c>suppressEvents</c> still true).
    /// </summary>
    private GridState UnbindGrid(bool keepSortAndFilter)
    {
        grid.EndEdit();
        // Read from the DataView: header-click sorts update it, but not BindingSource.Sort.
        var state = new GridState(
            keepSortAndFilter ? table.DefaultView.Sort ?? string.Empty : string.Empty,
            keepSortAndFilter ? table.DefaultView.RowFilter ?? string.Empty : string.Empty,
            grid.Columns.Contains(SourceColumn) ? grid.Columns[SourceColumn].Width : 160);

        suppressEvents = true;
        grid.SuspendLayout();
        // Clear any sort/filter the BindingSource remembers: it re-applies them whenever its
        // DataSource changes, which throws when the new list lacks those columns.
        TrySet(source.RemoveSort);
        TrySet(source.RemoveFilter);
        grid.DataSource = null;
        grid.Columns.Clear();
        source.DataSource = null;
        table.DefaultView.Sort = string.Empty;
        table.DefaultView.RowFilter = string.Empty;
        return state;
    }

    /// <summary>
    /// Applies a column layout (deletes table columns not listed, renames, creates new ones where Key == null),
    /// rebuilds the grid columns in the listed order and binds the table again. Source File stays leftmost.
    /// </summary>
    private void RebindGrid(IReadOnlyList<ColumnSpec> layout, GridState state)
    {
        string sort = state.Sort, filter = state.Filter;
        int sourceWidth = state.SourceWidth;
        try
        {
            // 1. Delete columns that are no longer in the layout.
            var keep = new HashSet<string>(layout.Where(s => s.Key is not null).Select(s => s.Key!), StringComparer.Ordinal);
            foreach (DataColumn column in FieldColumns().Where(c => !keep.Contains(c.ColumnName)).ToList())
                table.Columns.Remove(column);

            // 2. Rename in two passes so swaps (A→B, B→A) never collide.
            var renames = layout.Where(s => s.Key is not null && !s.Key.Equals(s.Name, StringComparison.Ordinal)).ToList();
            for (int i = 0; i < renames.Count; i++) table.Columns[renames[i].Key!]!.ColumnName = $"__RENAME{i}";
            for (int i = 0; i < renames.Count; i++) table.Columns[$"__RENAME{i}"]!.ColumnName = renames[i].Name;

            // 3. Create new columns.
            foreach (ColumnSpec spec in layout.Where(s => s.Key is null))
                if (!table.Columns.Contains(spec.Name)) AddDataColumn(table, spec.Name);

            source.DataSource = table;
            // Sort and filter live on the DataView only (never on the BindingSource).
            TrySet(() => table.DefaultView.Sort = sort);
            TrySet(() => table.DefaultView.RowFilter = filter);
            showDupsOnlyItem.Checked = !string.IsNullOrEmpty(table.DefaultView.RowFilter);

            if (table.Columns.Contains(SourceColumn)) AddGridColumn(SourceColumn, sourceWidth);
            foreach (ColumnSpec spec in layout) AddGridColumn(spec.Name, spec.Width);
            grid.DataSource = source;
        }
        finally
        {
            grid.ResumeLayout();
            suppressEvents = false;
        }
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or DataException or IndexOutOfRangeException) { }
    }

    private void AddGridColumn(string columnName, int width)
    {
        bool isSource = columnName == SourceColumn;
        var column = new DataGridViewTextBoxColumn
        {
            Name = columnName,
            DataPropertyName = columnName,
            HeaderText = isSource ? "Source File" : columnName,
            Width = Math.Clamp(width, 40, 600),
            SortMode = DataGridViewColumnSortMode.Automatic,
            ReadOnly = isSource,
            ToolTipText = isSource ? "Temporary column: the file each row came from. Not saved to ADIF." : columnName
        };
        if (isSource) column.DefaultCellStyle.BackColor = SourceColor;
        grid.Columns.Add(column);
    }

    private void AutoSizeColumns()
    {
        grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
        foreach (DataGridViewColumn c in grid.Columns)
            c.Width = Math.Clamp(c.Width, 50, 320);
    }

    /// <summary>All rows (ignoring any filter) in the current sort order.</summary>
    private List<DataRow> AllRowsInSortOrder()
    {
        var view = new DataView(table) { Sort = table.DefaultView.Sort };
        return view.Cast<DataRowView>().Select(v => v.Row).ToList();
    }

    private static List<IReadOnlyList<string?>> RowValues(IEnumerable<DataRow> rows, IReadOnlyList<string> columns) =>
        rows.Select(r => (IReadOnlyList<string?>)columns.Select(c => r[c] as string).ToList()).ToList();

    private void Table_ColumnChanged(object? sender, DataColumnChangeEventArgs e)
    {
        if (!suppressEvents && !IsInternal(e.Column?.ColumnName ?? "__")) MarkDirty();
    }

    /// <summary>Runs a bulk change without repainting the grid for every cell.</summary>
    private void Bulk(Action change)
    {
        grid.EndEdit();
        source.RaiseListChangedEvents = false;
        try { change(); }
        finally
        {
            source.RaiseListChangedEvents = true;
            source.ResetBindings(false);
            grid.Invalidate();
        }
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.CellStyle is null) return;
        // Read through the BindingSource rather than grid.Rows[i] so rows stay shared (faster for big logs).
        if (e.RowIndex < source.Count && source[e.RowIndex] is DataRowView drv
            && drv.Row.RowState != DataRowState.Detached
            && drv.Row[DupColumn] is int group && group > 0)
        {
            e.CellStyle.BackColor = group % 2 == 1 ? DupColorA : DupColorB;
        }
    }

    private void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.ColumnIndex < 0 || _statusProgress.IsBusy) return;
        headerMenuColumn = grid.Columns[e.ColumnIndex];
        headerMenu.Show(Cursor.Position);
    }

    private void Grid_DataError(object? sender, DataGridViewDataErrorEventArgs e)
    {
        MessageBox.Show(this, e.Exception?.Message ?? "Invalid value.", "Invalid value",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        e.ThrowException = false;
    }

    #endregion

    #region File commands

    private async Task NewFileAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        LoadTable(CreateTable(DefaultFields), autoSize: false);
        currentPath = null;
        dirty = mergedSinceSave = false;
        UpdateTitle();
        UpdateStatus("New contact list");
    }

    private async Task OpenFileAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        using var dialog = new OpenFileDialog
        {
            Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
            Title = "Open ADIF Log"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) await OpenPathAsync(dialog.FileName);
    }

    /// <summary>Reads and parses the file and builds the new table on a background thread, then binds it.</summary>
    private async Task OpenPathAsync(string path)
    {
        using StatusOperation? operation = BeginOperation($"Loading {Path.GetFileName(path)}");
        if (operation is null) return;
        try
        {
            var loaded = await Task.Run(() => BuildTableFromFile(path, operation.Progress, operation.Token), operation.Token);
            LoadTable(loaded.Table, autoSize: true);
            currentPath = path;
            dirty = mergedSinceSave = false;
            _recentFiles.Add(path);
            UpdateTitle();
            UpdateStatus($"Loaded {loaded.Table.Rows.Count:N0} contacts with {loaded.FieldCount} fields from {Path.GetFileName(path)} in {Took(operation)}");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Open cancelled; the current log is unchanged");
        }
        catch (Exception ex)
        {
            ShowError("Unable to open ADIF", ex);
        }
    }

    /// <summary>Background work for Open: parse (0–80%), then build the DataTable (80–100%). Touches no UI.</summary>
    private static (DataTable Table, int FieldCount) BuildTableFromFile(string path, IProgress<int> progress, CancellationToken token)
    {
        AdifLog log = AdifFile.Load(path, new ProgressRange(progress, 0, 80), token);
        // The columns come from the field names used in this (first) file.
        var fields = log.FieldNames.Where(f => !IgnoredFields.Contains(f)).ToList();
        DataTable newTable = CreateTable(fields.Count > 0 ? fields : DefaultFields);
        var reporter = new PercentReporter(new ProgressRange(progress, 80, 100), log.Records.Count);
        newTable.BeginLoadData();
        for (int i = 0; i < log.Records.Count; i++)
        {
            if (i % 1000 == 0)
            {
                token.ThrowIfCancellationRequested();
                reporter.Report(i);
            }
            AddRecord(newTable, log.Records[i], null, null);
        }
        newTable.EndLoadData();
        return (newTable, fields.Count);
    }

    private static DataRow AddRecord(DataTable t, AdifRecord record, IReadOnlyDictionary<string, string>? fieldMap, string? sourceFile)
    {
        DataRow row = t.NewRow();
        foreach (var (name, value) in record.Fields)
        {
            string? target = fieldMap is null ? (t.Columns.Contains(name) ? name : null)
                : fieldMap.TryGetValue(name, out string? mapped) ? mapped : null;
            if (target is not null && !IsInternal(target)) row[target] = value;
        }
        row[DupColumn] = 0;
        if (sourceFile is not null && t.Columns.Contains(SourceColumn)) row[SourceColumn] = sourceFile;
        t.Rows.Add(row);
        return row;
    }

    private async Task MergeFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
            Title = "Merge ADIF File"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await MergePathAsync(dialog.FileName);
    }

    /// <summary>Merges one ADIF file into the open log (used by File &gt; Merge and by dropped files).</summary>
    private async Task MergePathAsync(string path)
    {
        // Nothing loaded yet: this is the first file, so its field names define the columns.
        if (table.Rows.Count == 0 && !dirty)
        {
            await OpenPathAsync(path);
            return;
        }

        string fileName = Path.GetFileName(path);
        AdifLog log;
        using (StatusOperation? reading = BeginOperation($"Reading {fileName}"))
        {
            if (reading is null) return;
            try
            {
                log = await Task.Run(() => AdifFile.Load(path, reading.Progress, reading.Token), reading.Token);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Merge cancelled; nothing was changed");
                return;
            }
            catch (Exception ex)
            {
                ShowError("Unable to open ADIF", ex);
                return;
            }
        }

        // Match the new file's fields to existing columns (by current name, or by original name if renamed).
        var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (string field in log.FieldNames.Where(f => !IgnoredFields.Contains(f)))
        {
            DataColumn? column = FindColumn(field);
            if (column is not null) fieldMap[field] = column.ColumnName;
            else unknown.Add(field);
        }

        var newColumns = new List<string>();
        if (unknown.Count > 0)
        {
            DialogResult answer = MessageBox.Show(this,
                $"{fileName} has {unknown.Count} field(s) that are not columns in the grid:\n\n{string.Join(", ", unknown)}\n\n" +
                "Yes = add them as new columns\nNo = skip those fields\nCancel = don't merge",
                "Merge ADIF File", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes)
            {
                foreach (string field in unknown)
                {
                    string name = field.ToUpperInvariant();
                    if (!newColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) newColumns.Add(name);
                    fieldMap[field] = name;
                }
            }
        }

        using StatusOperation? operation = BeginOperation($"Merging {fileName}");
        if (operation is null) return;

        var layout = CurrentLayout();
        layout.AddRange(newColumns.Select(n => new ColumnSpec(n, n, 100)));
        string original = currentPath is null ? "(original)" : Path.GetFileName(currentPath);
        GridState state = UnbindGrid(keepSortAndFilter: true);
        MergeResult? result = null;
        try
        {
            // The table is unbound, so it can be filled on a background thread without touching the grid.
            DataTable target = table;
            result = await Task.Run(() => MergeRecords(target, log, fieldMap, newColumns, original, fileName,
                operation.Progress, operation.Token), operation.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Merge cancelled; nothing was changed");
        }
        catch (Exception ex)
        {
            ShowError("Unable to merge ADIF", ex);
        }
        finally
        {
            // Columns removed by a cancelled merge drop out of the layout here.
            RebindGrid(layout.Where(s => table.Columns.Contains(s.Key ?? s.Name)).ToList(), state);
        }

        if (result is null) return;
        mergedSinceSave = true;
        MarkDirty();
        UpdateStatus($"Merged {result.RowsAdded:N0} contacts from {fileName} in {Took(operation)}");
    }

    private sealed record MergeResult(int RowsAdded);

    private static readonly string[] ContactKeyNames = ["QSO_DATE", "TIME_ON", "CALL", "BAND", "MODE"];

    private async Task UpdateFileAsync()
    {
        var keyColumns = ContactKeyNames.Select(FindColumn).ToList();
        var missing = ContactKeyNames.Where((_, i) => keyColumns[i] is null).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, $"Update needs these columns: {string.Join(", ", missing)}.", "Update",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new OpenFileDialog
        {
            Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
            Title = "Update from ADIF File"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await UpdatePathAsync(dialog.FileName, keyColumns!);
    }

    /// <summary>
    /// Writes fields from an ADIF file onto existing rows that share QSO_DATE, TIME_ON, CALL, BAND and MODE.
    /// A record with no match is added. A record that matches more than one open row is skipped.
    /// </summary>
    private async Task UpdatePathAsync(string path, IReadOnlyList<DataColumn> keyColumns)
    {
        string fileName = Path.GetFileName(path);
        AdifLog log;
        using (StatusOperation? reading = BeginOperation($"Reading {fileName}"))
        {
            if (reading is null) return;
            try
            {
                log = await Task.Run(() => AdifFile.Load(path, reading.Progress, reading.Token), reading.Token);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Update cancelled; nothing was changed");
                return;
            }
            catch (Exception ex)
            {
                ShowError("Unable to open ADIF", ex);
                return;
            }
        }

        var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newColumns = new List<string>();
        foreach (string field in log.FieldNames.Where(f => !IgnoredFields.Contains(f)))
        {
            DataColumn? column = FindColumn(field);
            if (column is not null)
            {
                fieldMap[field] = column.ColumnName;
                continue;
            }
            string name = field.ToUpperInvariant();
            if (!newColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) newColumns.Add(name);
            fieldMap[field] = name;
        }

        using StatusOperation? operation = BeginOperation($"Updating from {fileName}");
        if (operation is null) return;

        var layout = CurrentLayout();
        layout.AddRange(newColumns.Select(n => new ColumnSpec(n, n, 100)));
        string[] keyNames = keyColumns.Select(c => c.ColumnName).ToArray();
        GridState state = UnbindGrid(keepSortAndFilter: true);
        UpdateResult? result = null;
        try
        {
            DataTable target = table;
            result = await Task.Run(() => UpdateRecords(target, log, fieldMap, newColumns, keyNames,
                operation.Progress, operation.Token), operation.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Update cancelled; nothing was changed");
        }
        catch (Exception ex)
        {
            ShowError("Unable to update from ADIF", ex);
        }
        finally
        {
            RebindGrid(layout.Where(s => table.Columns.Contains(s.Key ?? s.Name)).ToList(), state);
        }

        if (result is null) return;
        if (result.RowsUpdated > 0 || result.RowsAdded > 0 || result.ColumnsAdded > 0) MarkDirty();
        UpdateStatus(
            $"Updated {result.RowsUpdated:N0} contact(s) from {fileName}; added {result.RowsAdded:N0}" +
            (result.ColumnsAdded > 0 ? $"; added {result.ColumnsAdded:N0} column(s)" : string.Empty) +
            $"; {result.Ambiguous:N0} skipped (more than one matching row)");
    }

    private sealed record UpdateResult(int RowsUpdated, int RowsAdded, int Ambiguous, int ColumnsAdded);

    /// <summary>
    /// Background work for Update, on an unbound table. On cancellation or error it restores every cell
    /// it changed, removes rows it added, and removes columns it added.
    /// </summary>
    private static UpdateResult UpdateRecords(DataTable target, AdifLog log, IReadOnlyDictionary<string, string> fieldMap,
        IReadOnlyList<string> newColumns, IReadOnlyList<string> keyNames, IProgress<int> progress, CancellationToken token)
    {
        var addedColumns = new List<DataColumn>();
        var addedRows = new List<DataRow>();
        var changes = new List<(DataRow Row, string Column, object? Old)>();
        try
        {
            foreach (string name in newColumns)
                if (!target.Columns.Contains(name)) addedColumns.Add(AddDataColumn(target, name));

            var index = new Dictionary<string, DataRow?>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in target.Rows)
            {
                string? key = ContactKey(row, keyNames);
                if (key is null) continue;
                index[key] = index.ContainsKey(key) ? null : row;
            }

            int updated = 0, added = 0, ambiguous = 0;
            var reporter = new PercentReporter(progress, log.Records.Count);
            target.BeginLoadData();
            try
            {
                for (int i = 0; i < log.Records.Count; i++)
                {
                    if (i % 1000 == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        reporter.Report(i);
                    }
                    AdifRecord record = log.Records[i];
                    string? key = ContactKey(record["QSO_DATE"], record["TIME_ON"], record["CALL"], record["BAND"], record["MODE"]);
                    if (key is not null && index.TryGetValue(key, out DataRow? existing) && existing is null)
                    {
                        ambiguous++;
                        continue;
                    }
                    if (key is null || !index.TryGetValue(key, out DataRow? row) || row is null)
                    {
                        DataRow created = AddRecord(target, record, fieldMap, null);
                        addedRows.Add(created);
                        if (key is not null) index[key] = created;
                        added++;
                        continue;
                    }

                    bool changed = false;
                    foreach (var (name, value) in record.Fields)
                    {
                        if (!fieldMap.TryGetValue(name, out string? column) || IsInternal(column) || !target.Columns.Contains(column))
                            continue;
                        object old = row[column];
                        string current = old as string ?? string.Empty;
                        if (current == value) continue;
                        changes.Add((row, column, old == DBNull.Value ? null : old));
                        row[column] = value;
                        changed = true;
                    }
                    if (changed) updated++;
                }
            }
            finally { target.EndLoadData(); }

            return new UpdateResult(updated, added, ambiguous, addedColumns.Count);
        }
        catch
        {
            foreach (var (row, column, old) in changes)
                if (row.RowState != DataRowState.Detached) row[column] = old ?? (object)DBNull.Value;
            foreach (DataRow row in addedRows)
                if (row.RowState != DataRowState.Detached) target.Rows.Remove(row);
            foreach (DataColumn column in addedColumns) target.Columns.Remove(column);
            throw;
        }
    }

    /// <summary>Same key Find Duplicates uses. Date, time and call must be present; band and mode may be blank.</summary>
    private static string? ContactKey(DataRow row, IReadOnlyList<string> keyNames) =>
        ContactKey(row[keyNames[0]] as string, row[keyNames[1]] as string, row[keyNames[2]] as string,
            row[keyNames[3]] as string, row[keyNames[4]] as string);

    private static string? ContactKey(string? date, string? time, string? call, string? band, string? mode)
    {
        string d = (date ?? string.Empty).Trim();
        string t = (time ?? string.Empty).Trim();
        string c = (call ?? string.Empty).Trim();
        if (d.Length == 0 || t.Length == 0 || c.Length == 0) return null;
        return string.Join("|", d, NormalizeTime(t), (band ?? string.Empty).Trim(), (mode ?? string.Empty).Trim(), c);
    }

    /// <summary>
    /// Background work for Merge, on an unbound table. On cancellation or error it removes everything it
    /// added (rows, new columns, the Source File column), so the log is exactly as it was.
    /// </summary>
    private static MergeResult MergeRecords(DataTable target, AdifLog log, IReadOnlyDictionary<string, string> fieldMap,
        IReadOnlyList<string> newColumns, string originalFileName, string fileName, IProgress<int> progress, CancellationToken token)
    {
        var addedColumns = new List<DataColumn>();
        var addedRows = new List<DataRow>(log.Records.Count);
        bool addedSource = false;
        try
        {
            foreach (string name in newColumns)
                if (!target.Columns.Contains(name)) addedColumns.Add(AddDataColumn(target, name));

            // Add the temporary Source File column and label the rows that were already loaded.
            if (!target.Columns.Contains(SourceColumn))
            {
                DataColumn column = target.Columns.Add(SourceColumn, typeof(string));
                addedSource = true;
                foreach (DataRow row in target.Rows) row[column] = originalFileName;
            }

            var reporter = new PercentReporter(progress, log.Records.Count);
            target.BeginLoadData();
            try
            {
                for (int i = 0; i < log.Records.Count; i++)
                {
                    if (i % 1000 == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        reporter.Report(i);
                    }
                    addedRows.Add(AddRecord(target, log.Records[i], fieldMap, fileName));
                }
            }
            finally { target.EndLoadData(); }
            return new MergeResult(addedRows.Count);
        }
        catch
        {
            foreach (DataRow row in addedRows) target.Rows.Remove(row);
            foreach (DataColumn column in addedColumns) target.Columns.Remove(column);
            if (addedSource) target.Columns.Remove(SourceColumn);
            throw;
        }
    }

    private async Task<bool> SaveFileAsync(bool saveAs)
    {
        grid.EndEdit();
        source.EndEdit();
        string? path = currentPath;
        // After a merge, don't silently overwrite the first file: ask where to save.
        if (saveAs || mergedSinceSave || string.IsNullOrWhiteSpace(path))
        {
            path = AskSavePath(mergedSinceSave && !saveAs ? "Save Merged Log As" : "Save ADIF Log",
                path is null ? "contacts.adi" : Path.GetFileName(path));
            if (path is null) return false;
        }

        var columns = CurrentLayout().Select(c => c.Name).ToList();
        var rows = AllRowsInSortOrder();
        bool saved = await WriteFileAsync($"Saving {Path.GetFileName(path)}", "Unable to save ADIF",
            (progress, token) => AdifFile.Save(path, columns, RowValues(rows, columns), progress, token),
            elapsed => $"Saved {rows.Count:N0} contacts to {Path.GetFileName(path)} in {elapsed}");
        if (!saved) return false;

        currentPath = path;
        dirty = mergedSinceSave = false;
        _recentFiles.Add(path);
        UpdateTitle();
        return true;
    }

    /// <summary>
    /// Runs a file write on a background thread with status-bar progress. Editing is locked meanwhile,
    /// so reading the rows there is safe. Returns true when the file was written.
    /// </summary>
    private async Task<bool> WriteFileAsync(string caption, string errorTitle,
        Action<IProgress<int>, CancellationToken> write, Func<string, string> successMessage)
    {
        using StatusOperation? operation = BeginOperation(caption);
        if (operation is null) return false;
        try
        {
            await Task.Run(() => write(operation.Progress, operation.Token), operation.Token);
            UpdateStatus(successMessage(Took(operation)));
            return true;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Cancelled; the file on disk was not changed");
            return false;
        }
        catch (Exception ex)
        {
            ShowError(errorTitle, ex);
            return false;
        }
    }

    private async Task SaveSelectedRowsAsync()
    {
        grid.EndEdit();
        var rows = grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(r => r.Index)
            .Select(r => (r.DataBoundItem as DataRowView)?.Row)
            .OfType<DataRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more rows first (click the row headers; Ctrl/Shift+click selects several).",
                "Save Selected Rows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string? path = AskSavePath($"Save {rows.Count:N0} Selected Row(s) As", "selected.adi");
        if (path is null) return;

        var columns = CurrentLayout().Select(c => c.Name).ToList();
        await WriteFileAsync($"Saving {Path.GetFileName(path)}", "Unable to save ADIF",
            (progress, token) => AdifFile.Save(path, columns, RowValues(rows, columns), progress, token),
            elapsed => $"Saved {rows.Count:N0} selected contacts to {Path.GetFileName(path)} in {elapsed}");
    }

    private string? AskSavePath(string title, string defaultName)
    {
        using var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "ADIF files (*.adi)|*.adi|ADIF files (*.adif)|*.adif|All files (*.*)|*.*",
            DefaultExt = "adi",
            AddExtension = true,
            FileName = defaultName
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private async Task ExportCsvAsync()
    {
        grid.EndEdit();
        using var dialog = new SaveFileDialog
        {
            Title = "Export to CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(currentPath ?? "contacts") + ".csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string path = dialog.FileName;
        var (headers, columns) = ExportColumns();
        var rows = AllRowsInSortOrder();
        await WriteFileAsync($"Exporting {Path.GetFileName(path)}", "Unable to export CSV",
            (progress, token) => CsvExporter.Save(path, headers, RowValues(rows, columns), progress, token),
            elapsed => $"Exported {rows.Count:N0} rows to {Path.GetFileName(path)} in {elapsed}");
    }

    private async Task ExportSqlAsync(SqlDialect dialect)
    {
        grid.EndEdit();
        string dbName = dialect == SqlDialect.SQLite ? "SQLite" : "SQL Server";
        string? tableName = PromptDialog.Show(this, $"{dbName} INSERT Script", "Table name:", "Contacts",
            v => SqlScriptExporter.IsValidIdentifier(v) ? null : "Use letters, digits and underscores only (not starting with a digit).");
        if (tableName is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = $"Save {dbName} INSERT Script",
            Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*",
            DefaultExt = "sql",
            AddExtension = true,
            FileName = $"{Path.GetFileNameWithoutExtension(currentPath ?? "contacts")}_{(dialect == SqlDialect.SQLite ? "sqlite" : "sqlserver")}.sql"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string path = dialog.FileName;
        var (headers, columns) = ExportColumns();
        var rows = AllRowsInSortOrder();
        await WriteFileAsync($"Writing {Path.GetFileName(path)}", $"Unable to create {dbName} script",
            (progress, token) => SqlScriptExporter.Save(path, dialect, tableName, headers, RowValues(rows, columns), progress, token),
            elapsed => $"Wrote {dbName} script for {rows.Count:N0} rows to {Path.GetFileName(path)} in {elapsed}");
    }

    /// <summary>Columns in on-screen order (including Source File, exported as SOURCE_FILE).</summary>
    private (List<string> Headers, List<string> Columns) ExportColumns()
    {
        var columns = OrderedGridColumns().Select(c => c.DataPropertyName).ToList();
        var headers = columns.Select(c => c == SourceColumn ? "SOURCE_FILE" : c).ToList();
        return (headers, columns);
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        grid.EndEdit();
        if (!dirty) return true;
        DialogResult result = MessageBox.Show(this, "Save changes to the contact list first?", AppTitle,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return result switch
        {
            DialogResult.Yes => await SaveFileAsync(false),
            DialogResult.No => true,
            _ => false
        };
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closeConfirmed || e.CloseReason == CloseReason.WindowsShutDown) return;
        if (_statusProgress.IsBusy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "An operation is still running. Wait for it to finish or click Cancel in the status bar.",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        grid.EndEdit();
        if (!dirty) return;

        // Saving is asynchronous, so cancel this close, ask/save, then close again.
        e.Cancel = true;
        RunCommand(async () =>
        {
            if (!await ConfirmDiscardAsync()) return;
            _closeConfirmed = true;
            Close();
        });
    }

    #endregion

    #region Opening files from Explorer, drops and the recent list

    private void OpenNewWindow()
    {
        if (_windowContext is null)
        {
            MessageBox.Show(this, "Extra windows are not available in this session.", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _windowContext.OpenWindow();
    }

    private void BuildRecentFilesMenu()
    {
        _recentFilesItem.DropDownItems.Clear();
        if (_recentFiles.Items.Count == 0)
        {
            _recentFilesItem.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
            return;
        }
        int number = 1;
        foreach (string path in _recentFiles.Items)
        {
            string caption = $"&{number++} {Path.GetFileName(path)}";
            var item = new ToolStripMenuItem(caption) { ToolTipText = path };
            string captured = path;
            item.Click += (_, _) => RunCommand(() => OpenRecentAsync(captured));
            _recentFilesItem.DropDownItems.Add(item);
        }
        _recentFilesItem.DropDownItems.Add(new ToolStripSeparator());
        _recentFilesItem.DropDownItems.Add(new ToolStripMenuItem("&Clear List", null, (_, _) => RunCommand(() =>
        {
            _recentFiles.Clear();
            UpdateStatus("Recent file list cleared");
        })));
    }

    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            if (MessageBox.Show(this, $"{path}\n\nThat file is no longer there. Remove it from the recent list?",
                    "Recent Files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _recentFiles.Remove(path);
            return;
        }
        if (!await ConfirmDiscardAsync()) return;
        await OpenPathAsync(path);
    }

    private void Grid_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = DroppedLogFiles(e).Count > 0 && !_statusProgress.IsBusy
            ? IsShiftHeld(e) ? DragDropEffects.Link : DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void Grid_DragDrop(object? sender, DragEventArgs e)
    {
        var paths = DroppedLogFiles(e);
        if (paths.Count == 0) return;
        bool mergeFirst = IsShiftHeld(e);
        RunCommand(() => OpenDroppedFilesAsync(paths, mergeFirst));
    }

    private static bool IsShiftHeld(DragEventArgs e) => (e.KeyState & 4) == 4;

    /// <summary>The .adi / .adif files in a drag, ignoring anything else that was dropped.</summary>
    private static List<string> DroppedLogFiles(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => File.Exists(f) && FileAssociationService.IsSupportedFile(f)).ToList()
            : [];

    /// <summary>Opens the first dropped file (or merges it when Shift is held); any others are merged.</summary>
    private async Task OpenDroppedFilesAsync(IReadOnlyList<string> paths, bool mergeFirst)
    {
        Activate();
        for (int i = 0; i < paths.Count; i++)
        {
            bool merge = mergeFirst || i > 0;
            if (merge) await MergePathAsync(paths[i]);
            else
            {
                if (!await ConfirmDiscardAsync()) return;
                await OpenPathAsync(paths[i]);
            }
        }
    }

    private void ToggleFileAssociation()
    {
        if (FileAssociationService.IsRegistered())
        {
            FileAssociationService.Unregister();
            UpdateStatus("This program no longer opens .adi files by double-click");
            return;
        }
        FileAssociationService.Register();
        UpdateStatus("Double-clicking an .adi file now opens this program");
        MessageBox.Show(this,
            "Windows will now open .adi and .adif files with this program.\n\n" +
            "If double-clicking still opens a different program, that means Windows is remembering an " +
            "earlier choice: right-click the file, pick \"Open with\" > \"Choose another app\", select " +
            "HamLogger Contact Editor and tick \"Always use this app\".",
            "Open .adi Files With This Program", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    #endregion

    #region Row commands

    private void AddContact()
    {
        grid.EndEdit();
        SetDuplicateFilter(false);
        DateTime utc = DateTime.UtcNow;
        DataRow row = table.NewRow();
        row[DupColumn] = 0;
        if (FindColumn("QSO_DATE") is { } date) row[date] = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (FindColumn("TIME_ON") is { } time) row[time] = utc.ToString("HHmmss", CultureInfo.InvariantCulture);
        if (table.Columns.Contains(SourceColumn)) row[SourceColumn] = "(added)";
        table.Rows.Add(row);

        int index = ViewIndexOf(row);
        if (index >= 0)
        {
            DataGridViewRow gridRow = grid.Rows[index];
            DataGridViewColumn? first = (FindColumn("CALL") is { } call ? grid.Columns[call.ColumnName] : null)
                ?? OrderedGridColumns().FirstOrDefault(c => !c.ReadOnly);
            grid.ClearSelection();
            gridRow.Selected = true;
            if (first is not null)
            {
                grid.CurrentCell = gridRow.Cells[first.Index];
                grid.BeginEdit(true);
            }
        }
        MarkDirty();
        UpdateStatus("Contact added (QSO_DATE/TIME_ON set to the current UTC time)");
    }

    private void DeleteSelected()
    {
        grid.EndEdit();
        var selected = grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (r.DataBoundItem as DataRowView)?.Row).OfType<DataRow>().ToList();
        if (selected.Count == 0) return;
        if (MessageBox.Show(this, $"Delete {selected.Count:N0} selected contact(s)?", "Confirm deletion",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Bulk(() => { foreach (DataRow row in selected) table.Rows.Remove(row); });
        MarkDirty();
        UpdateStatus($"Deleted {selected.Count:N0} contact(s)");
    }

    private void ShowSearchReplace()
    {
        searchDialog ??= new SearchReplaceDialog(this);
        searchDialog.ShowFor(this);
    }

    /// <summary>On-screen index of a row, found through the BindingSource so grid rows stay shared (fast for big logs).</summary>
    private int ViewIndexOf(DataRow row)
    {
        for (int i = 0; i < source.Count; i++)
            if (source[i] is DataRowView view && ReferenceEquals(view.Row, row)) return i;
        return -1;
    }

    #region ISearchHost (used by the Search and Replace window)

    IReadOnlyList<SearchColumn> ISearchHost.GetSearchColumns() =>
        OrderedGridColumns()
            .Where(c => c.Visible && !string.IsNullOrEmpty(c.DataPropertyName))
            .Select(c => new SearchColumn(c.HeaderText, c.DataPropertyName, c.ReadOnly))
            .ToList();

    SearchSnapshot ISearchHost.CreateSnapshot(IReadOnlyList<string> columns)
    {
        grid.EndEdit();
        // Copy only row references (fast); the values are read later on the background thread.
        var rows = new List<DataRow>(source.Count);
        for (int i = 0; i < source.Count; i++)
            if (source[i] is DataRowView view) rows.Add(view.Row);
        return new SearchSnapshot(rows, columns);
    }

    (int RowIndex, DataRow? Row, string? Column) ISearchHost.GetCurrentCell()
    {
        DataGridViewCell? cell = grid.CurrentCell;
        if (cell is null || cell.RowIndex < 0 || cell.RowIndex >= source.Count) return (-1, null, null);
        DataRow? row = (source[cell.RowIndex] as DataRowView)?.Row;
        return (cell.RowIndex, row, grid.Columns[cell.ColumnIndex].DataPropertyName);
    }

    bool ISearchHost.SelectCell(DataRow row, string column)
    {
        int index = ViewIndexOf(row);
        if (index < 0 || !grid.Columns.Contains(column)) return false;
        grid.ClearSelection();
        DataGridViewRow gridRow = grid.Rows[index];
        grid.CurrentCell = gridRow.Cells[grid.Columns[column].Index];
        gridRow.Selected = true;
        return true;
    }

    bool ISearchHost.IsBusy => _statusProgress.IsBusy;

    StatusOperation? ISearchHost.BeginOperation(string caption) => BeginOperation(caption);

    void ISearchHost.ApplyChanges(IReadOnlyList<CellChange> changes, string description)
    {
        Bulk(() =>
        {
            foreach (CellChange change in changes)
                if (change.Row.RowState != DataRowState.Detached && change.Row.Table.Columns.Contains(change.Column))
                    change.Row[change.Column] = change.NewValue;
        });
        if (changes.Count > 0) MarkDirty();
        UpdateStatus(description);
    }

    #endregion

    private static string NormalizeTime(string? value)
    {
        string digits = new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 6 ? digits[..6] : digits.PadRight(6, '0');
    }

    private void SortByDateTime(bool ascending)
    {
        DataColumn? date = FindColumn("QSO_DATE");
        DataColumn? time = FindColumn("TIME_ON");
        if (date is null || time is null)
        {
            MessageBox.Show(this, "Sorting by date/time needs QSO_DATE and TIME_ON columns.", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Bulk(() =>
        {
            table.DefaultView.Sort = string.Empty;
            foreach (DataRow row in table.Rows)
                row[SortColumn] = ((row[date] as string) ?? string.Empty).Trim() + NormalizeTime(row[time] as string);
            table.DefaultView.Sort = $"[{SortColumn}] {(ascending ? "ASC" : "DESC")}";
        });
        foreach (DataGridViewColumn c in grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        UpdateStatus($"Sorted by date/time, {(ascending ? "oldest" : "newest")} first");
    }

    private void RemoveSort()
    {
        Bulk(() => table.DefaultView.Sort = string.Empty);
        foreach (DataGridViewColumn c in grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        UpdateStatus("Sort removed");
    }

    private void FindDuplicates()
    {
        string[] keyNames = ["QSO_DATE", "TIME_ON", "BAND", "MODE", "CALL"];
        var keyColumns = keyNames.Select(FindColumn).ToList();
        var missing = keyNames.Where((_, i) => keyColumns[i] is null).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, $"Duplicate checking needs these columns: {string.Join(", ", missing)}.", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DataColumn date = keyColumns[0]!, time = keyColumns[1]!, band = keyColumns[2]!, mode = keyColumns[3]!, call = keyColumns[4]!;

        var groups = new Dictionary<string, List<DataRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in table.Rows)
        {
            string d = ((row[date] as string) ?? string.Empty).Trim();
            string t = ((row[time] as string) ?? string.Empty).Trim();
            string c = ((row[call] as string) ?? string.Empty).Trim();
            if (d.Length == 0 || t.Length == 0 || c.Length == 0) continue;
            string key = string.Join("|", d, NormalizeTime(t),
                ((row[band] as string) ?? string.Empty).Trim(),
                ((row[mode] as string) ?? string.Empty).Trim(),
                c);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(row);
        }

        var dupGroups = groups.Values.Where(g => g.Count > 1).ToList();
        int dupRows = dupGroups.Sum(g => g.Count);
        Bulk(() =>
        {
            foreach (DataRow row in table.Rows) row[DupColumn] = 0;
            int number = 0;
            foreach (var g in dupGroups)
            {
                number++;
                foreach (DataRow row in g) row[DupColumn] = number;
            }
        });

        if (dupRows == 0)
        {
            SetDuplicateFilter(false);
            UpdateStatus("No duplicates found");
            MessageBox.Show(this, "No duplicate QSOs found (matching QSO_DATE, TIME_ON, BAND, MODE and CALL).", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);  
            return;
        }

        UpdateStatus($"Found {dupRows:N0} duplicate rows in {dupGroups.Count:N0} groups (highlighted)");
        if (MessageBox.Show(this,
                $"Found {dupRows:N0} rows in {dupGroups.Count:N0} duplicate groups (same QSO_DATE, TIME_ON, BAND, MODE and CALL). " +
                "They are highlighted; alternating colors separate the groups.\n\n" +
                "Show only the duplicate rows, sorted by date/time?", "Duplicate QSOs", 
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            SortByDateTime(true);
            SetDuplicateFilter(true);
        }
    }

    private void SetDuplicateFilter(bool on)
    {
        grid.EndEdit();
        string filter = on ? $"[{DupColumn}] > 0" : string.Empty;
        if ((table.DefaultView.RowFilter ?? string.Empty) != filter) table.DefaultView.RowFilter = filter;
        showDupsOnlyItem.Checked = on;
        UpdateStatus(on ? "Showing duplicate rows only" : "Showing all rows");
    }

    private void ClearDuplicates()
    {
        SetDuplicateFilter(false);
        Bulk(() => { foreach (DataRow row in table.Rows) row[DupColumn] = 0; });
        UpdateStatus("Duplicate highlights cleared");
    }

    /// <summary>
    /// For every row whose COMMENT contains <paramref name="token"/>, sets MODE (and SUBMODE when given).
    /// </summary>
    private void RunModeTest(string token, string mode, string? submode)
    {
        string title = $"TEST-{token}";
        DataColumn? comment = FindColumn("COMMENT") ?? FindColumn("COMMENTS");
        if (comment is null)
        {
            MessageBox.Show(this, "There is no COMMENT column to check.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Create MODE / SUBMODE columns if the log doesn't have them yet.
        var layout = CurrentLayout();
        bool changedLayout = false;
        if (FindColumn("MODE") is null)
        {
            layout.Add(new ColumnSpec(null, "MODE", 80));
            changedLayout = true;
        }
        if (submode is not null && FindColumn("SUBMODE") is null)
        {
            string modeName = FindColumn("MODE")?.ColumnName ?? "MODE";
            int at = layout.FindIndex(s => s.Name.Equals(modeName, StringComparison.OrdinalIgnoreCase));
            layout.Insert(at < 0 ? layout.Count : at + 1, new ColumnSpec(null, "SUBMODE", 80));
            changedLayout = true;
        }
        if (changedLayout) RebuildGrid(layout);

        string modeCol = FindColumn("MODE")!.ColumnName;
        string? submodeCol = submode is null ? null : FindColumn("SUBMODE")!.ColumnName;

        int matched = 0, updated = 0;
        Bulk(() =>
        {
            foreach (DataRow row in table.Rows)
            {
                if (row[comment] is not string text || !text.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
                matched++;
                bool change = !string.Equals(row[modeCol] as string, mode, StringComparison.Ordinal)
                    || (submodeCol is not null && !string.Equals(row[submodeCol] as string, submode, StringComparison.Ordinal));
                if (!change) continue;
                row[modeCol] = mode;
                if (submodeCol is not null) row[submodeCol] = submode;
                updated++;
            }
        });

        string what = submode is null ? $"MODE = {mode}" : $"MODE = {mode}, SUBMODE = {submode}";
        UpdateStatus($"{title}: {matched:N0} rows mention {token}; {updated:N0} updated ({what})");
        MessageBox.Show(this,
            $"{matched:N0} row(s) have \"{token}\" in {comment.ColumnName}.\n{updated:N0} row(s) were updated to {what}." +
            (matched > updated ? $"\n{matched - updated:N0} row(s) already had those values." : string.Empty),
            title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Lets the user build an IF (ColumnA op Value1) [AND/OR (ColumnB op Value2)] THEN (ColumnC = Value3)
    /// rule from the grid's current columns, previews how many rows it matches, then applies it.
    /// </summary>
    private void ApplyConditionalRule()
    {
        try
        {
            grid.EndEdit();
            var columnNames = CurrentLayout().Select(c => c.Name).ToList();
            if (columnNames.Count == 0)
            {
                MessageBox.Show(this, "There are no columns to build a rule from.", "Apply Conditional Rule",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new ConditionalRuleDialog(table, columnNames);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } rule) return;

            int previewMatches = ConditionalRuleEngine.CountMatches(table, rule);
            if (previewMatches == 0)
            {
                MessageBox.Show(this, "No rows match this rule.", "Apply Conditional Rule",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this,
                    $"This will set {rule.ColumnC} = \"{rule.ValueC}\" on {previewMatches:N0} matching row(s). Continue?",
                    "Apply Conditional Rule", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            (int Matched, int Updated) result = default;
            Bulk(() => result = ConditionalRuleEngine.Apply(table, rule));

            MarkDirty();
            UpdateStatus($"Conditional rule: {result.Matched:N0} row(s) matched, {result.Updated:N0} updated");
            MessageBox.Show(this,
                $"{result.Matched:N0} row(s) matched the condition.\n{result.Updated:N0} row(s) were updated." +
                (result.Matched > result.Updated ? $"\n{result.Matched - result.Updated:N0} row(s) already had that value." : string.Empty),
                "Apply Conditional Rule", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Apply Conditional Rule", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    #endregion

    #region Column commands

    private void ManageColumns()
    {
        using var dialog = new ColumnManagerDialog(CurrentLayout());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var before = CurrentLayout();
        var after = dialog.Columns;
        bool same = before.Count == after.Count && before.Zip(after).All(p => p.First.Key == p.Second.Key && p.Second.Key == p.Second.Name);
        if (same) return;

        var kept = new HashSet<string>(after.Where(s => s.Key is not null).Select(s => s.Key!), StringComparer.Ordinal);
        int deleted = before.Count(s => s.Key is not null && !kept.Contains(s.Key!));
        if (deleted > 0 && table.Rows.Count > 0 && MessageBox.Show(this,
                $"Delete {deleted} column(s) and their data from all {table.Rows.Count:N0} rows?", "Manage Columns",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

        RebuildGrid(after);
        MarkDirty();
        UpdateStatus("Columns updated");
    }

    private void RenameColumn(DataGridViewColumn? column)
    {
        if (column is null || column.DataPropertyName == SourceColumn) return;
        var layout = CurrentLayout();
        ColumnSpec? spec = layout.FirstOrDefault(s => s.Key == column.DataPropertyName);
        if (spec is null) return;
        string? name = PromptDialog.Show(this, "Rename Column", $"New ADIF field name for {spec.Name}:", spec.Name, v =>
        {
            if (!AdifFile.IsValidFieldName(v))
                return "ADIF field names may contain only letters, digits and underscores, and cannot start with an underscore.";
            return layout.Any(s => s != spec && s.Name.Equals(v, StringComparison.OrdinalIgnoreCase))
                ? $"A column named {v.ToUpperInvariant()} already exists." : null;
        });
        if (name is null || name.Equals(spec.Name, StringComparison.Ordinal)) return;
        string old = spec.Name;
        spec.Name = name.ToUpperInvariant();
        RebuildGrid(layout);
        MarkDirty();
        UpdateStatus($"Renamed {old} to {spec.Name} (saved files will use the new name)");
    }

    private void DeleteColumn(DataGridViewColumn? column)
    {
        if (column is null) return;
        if (column.DataPropertyName == SourceColumn)
        {
            RemoveSourceColumn();
            return;
        }
        var layout = CurrentLayout();
        if (layout.Count <= 1)
        {
            MessageBox.Show(this, "At least one column must remain.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete column {column.HeaderText} and its data from all {table.Rows.Count:N0} rows?",
                "Delete Column", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        layout.RemoveAll(s => s.Key == column.DataPropertyName);
        RebuildGrid(layout);
        MarkDirty();
        UpdateStatus($"Deleted column {column.HeaderText}");
    }

    private void MoveColumn(DataGridViewColumn? column, int direction)
    {
        if (column is null) return;
        var ordered = OrderedGridColumns();
        int index = ordered.IndexOf(column);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        column.DisplayIndex = ordered[target].DisplayIndex;
        MarkDirty();
    }

    private void RemoveSourceColumn()
    {
        if (!table.Columns.Contains(SourceColumn)) return;
        RebuildGrid(CurrentLayout(), schemaChange: () => table.Columns.Remove(SourceColumn));
        UpdateStatus("Source File column removed");
    }

    #endregion

    #region Status

    private void MarkDirty()
    {
        if (dirty) return;
        dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Text = $"{AppTitle} - {(currentPath is null ? "Untitled" : Path.GetFileName(currentPath))}{(dirty ? " *" : string.Empty)}";

    private void UpdateStatus(string message)
    {
        string shown = !string.IsNullOrEmpty(table.DefaultView.RowFilter) ? $"  |  Showing: {source.Count:N0}" : string.Empty;
        status.Text = $"{message}  |  Contacts: {table.Rows.Count:N0}{shown}  |  Columns: {FieldColumns().Count()}";
        UpdateTitle();
    }

    #endregion
}
