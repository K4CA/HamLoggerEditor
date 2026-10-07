using System.Data;
using HamLoggerEditor.Rules;

namespace HamLoggerEditor.Dialogs;

/// <summary>
/// Builds one IF/THEN rule: any number of conditions joined with AND/OR, and one or more fields to set.
/// Field dropdowns list the grid's current columns; value boxes suggest the values already in that column
/// and are checked against the column's inferred type (number, date, time or text). A live count shows how
/// many rows the conditions match. Nothing is applied here -- Apply just returns <see cref="Result"/>.
/// </summary>
public sealed class ConditionalRuleDialog : Form
{
    private const int MaxConditions = 10;
    private const int MaxActions = 8;
    private const int MaxSuggestions = 300;

    // Shared column widths so the IF and THEN grids line up.
    private const int JoinWidth = 80, FieldWidth = 180, OperatorWidth = 200, ValueWidth = 200, TypeWidth = 190, RemoveWidth = 34;

    private sealed record OperatorChoice(RuleOperator Op, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly OperatorChoice[] Operators =
    [
        new(RuleOperator.Equals, "equals"),
        new(RuleOperator.NotEquals, "does not equal"),
        new(RuleOperator.GreaterThan, "is greater than"),
        new(RuleOperator.GreaterOrEqual, "is greater than or equal to"),
        new(RuleOperator.LessThan, "is less than"),
        new(RuleOperator.LessOrEqual, "is less than or equal to"),
        new(RuleOperator.Contains, "contains"),
        new(RuleOperator.NotContains, "does not contain"),
        new(RuleOperator.StartsWith, "starts with"),
        new(RuleOperator.EndsWith, "ends with"),
        new(RuleOperator.IsBlank, "is blank"),
        new(RuleOperator.IsNotBlank, "is not blank"),
    ];

    /// <summary>One IF line. The joiner is hidden on the first line.</summary>
    private sealed class ConditionRow
    {
        public required ComboBox Joiner { get; init; }
        public required ComboBox Field { get; init; }
        public required ComboBox Operator { get; init; }
        public required ComboBox Value { get; init; }
        public required Label Type { get; init; }
        public required Button Remove { get; init; }
        public ValueKind Kind { get; set; }
    }

    /// <summary>One THEN line: set Field to Value.</summary>
    private sealed class ActionRow
    {
        public required ComboBox Field { get; init; }
        public required ComboBox Value { get; init; }
        public required Label Type { get; init; }
        public required Button Remove { get; init; }
        public ValueKind Kind { get; set; }
    }

    private readonly DataTable _table;
    private readonly IReadOnlyList<string> _columnNames;
    private readonly Dictionary<string, (ValueKind Kind, string[] Values)> _columnInfo = new(StringComparer.Ordinal);

    private readonly List<ConditionRow> _conditions = [];
    private readonly List<ActionRow> _actions = [];
    private readonly TableLayoutPanel _conditionsTable;
    private readonly TableLayoutPanel _actionsTable;
    private readonly Button _addConditionButton = new() { Text = "+ Add condition", AutoSize = true };
    private readonly Button _addActionButton = new() { Text = "+ Add field to set", AutoSize = true };
    private readonly Label _precedenceLabel = new()
    {
        Text = "When AND and OR are mixed, AND is checked first: \"A OR B AND C\" means \"A, or both B and C\".",
        AutoSize = true, Margin = new Padding(3, 6, 3, 0), Visible = false
    };
    private readonly Label _matchLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) };
    private readonly Label _ifLabel = new() { Text = "IF", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(4) };
    private readonly List<Control> _actionLabels = [];
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 250 };

    /// <summary>The rule the user built, set only when the dialog closes with DialogResult.OK.</summary>
    public ConditionalRule? Result { get; private set; }

    public ConditionalRuleDialog(DataTable table, IReadOnlyList<string> columnNames)
    {
        _table = table;
        _columnNames = columnNames;

        // Pixel sizes below are written for 100% scaling and converted with S() for the current DPI,
        // so rows added later (Add condition / Add field) line up with the ones created here.
        SuspendLayout();
        _conditionsTable = NewGrid();
        _actionsTable = NewGrid();
        _precedenceLabel.MaximumSize = new Size(S(JoinWidth + FieldWidth + OperatorWidth + ValueWidth + TypeWidth), 0);
        _matchLabel.MaximumSize = new Size(S(620), 0);
        Text = "Apply Conditional Rule";
        Font = new Font("Segoe UI", 10F);
        _ifLabel.Font = new Font(Font, FontStyle.Bold);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 12, 16, 12);

        var apply = new Button { Text = "Apply...", AutoSize = true, MinimumSize = new Size(S(100), S(32)), Margin = new Padding(8, 3, 0, 3) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(S(100), S(32)), Margin = new Padding(8, 3, 0, 3), DialogResult = DialogResult.Cancel };
        apply.Click += (_, _) => OnApply();
        AcceptButton = apply;
        CancelButton = cancel;

        _addConditionButton.Click += (_, _) => { AddCondition(); RebuildConditions(); SchedulePreview(); };
        _addActionButton.Click += (_, _) => { AddAction(); RebuildActions(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        buttons.Controls.AddRange([cancel, apply]);

        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_matchLabel, 0, 0);
        footer.Controls.Add(buttons, 1, 0);

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Location = new Point(Padding.Left, Padding.Top) };
        void AddRoot(Control c) { root.RowCount++; root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(c, 0, root.RowCount - 1); }

        AddRoot(Heading("IF these conditions are true"));
        AddRoot(_conditionsTable);
        AddRoot(_addConditionButton);
        AddRoot(_precedenceLabel);
        AddRoot(Separator());
        AddRoot(Heading("THEN set these fields"));
        AddRoot(_actionsTable);
        AddRoot(new Label { Text = "Leave a new value empty to clear that field.", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        AddRoot(_addActionButton);
        AddRoot(Separator());
        AddRoot(footer);
        Controls.Add(root);

        AddCondition();
        AddAction();
        RebuildConditions();
        RebuildActions();
        UpdatePreview();
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewTimer.Dispose();
            _toolTip.Dispose();
            // Controls not currently in the dialog (none normally) are not disposed by the form.
            if (_ifLabel.Parent is null) _ifLabel.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- layout helpers -----------------------------------------------------------------------

    /// <summary>Converts a size designed at 96 DPI to this window's DPI.</summary>
    private int S(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96.0);

    private TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 6, Margin = new Padding(0, 4, 0, 4) };
        foreach (int width in new[] { JoinWidth, FieldWidth, OperatorWidth, ValueWidth, TypeWidth, RemoveWidth })
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(width + 8)));
        return grid;
    }

    private Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Margin = new Padding(0, 6, 0, 2) };

    private static Label Separator() =>
        new() { BorderStyle = BorderStyle.Fixed3D, AutoSize = false, Height = 2, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 10, 0, 6) };

    private static Label ColumnCaption(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(4, 0, 4, 0), Anchor = AnchorStyles.Left | AnchorStyles.Bottom };

    private static Label CellLabel(string text, ContentAlignment align = ContentAlignment.MiddleLeft) =>
        new() { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = align, Margin = new Padding(4) };

    private ComboBox FieldBox()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(FieldWidth), Margin = new Padding(4), MaxDropDownItems = 20 };
        foreach (string name in _columnNames) box.Items.Add(name);
        if (box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }

    private ComboBox ValueBox() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDown, Width = S(ValueWidth), Margin = new Padding(4), MaxDropDownItems = 20,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems
    };

    private Button RemoveButton(string tip)
    {
        var button = new Button { Text = "×", Width = S(RemoveWidth), Height = S(28), Margin = new Padding(4) };
        _toolTip.SetToolTip(button, tip);
        return button;
    }

    // ---- condition rows -----------------------------------------------------------------------

    private void AddCondition()
    {
        var joiner = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(JoinWidth), Margin = new Padding(4) };
        joiner.Items.AddRange(["AND", "OR"]);
        joiner.SelectedIndex = 0;
        var op = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(OperatorWidth), Margin = new Padding(4), MaxDropDownItems = Operators.Length };
        op.Items.AddRange(Operators);
        op.SelectedIndex = 0;

        var row = new ConditionRow
        {
            Joiner = joiner, Field = FieldBox(), Operator = op, Value = ValueBox(),
            Type = CellLabel(string.Empty), Remove = RemoveButton("Remove this condition")
        };
        _conditions.Add(row);

        row.Field.SelectedIndexChanged += (_, _) => { RefreshCondition(row, loadValues: true); SchedulePreview(); };
        row.Operator.SelectedIndexChanged += (_, _) => { RefreshCondition(row, loadValues: false); SchedulePreview(); };
        row.Joiner.SelectedIndexChanged += (_, _) => { UpdatePrecedenceNote(); SchedulePreview(); };
        row.Value.TextChanged += (_, _) => SchedulePreview();
        row.Remove.Click += (_, _) =>
        {
            _conditions.Remove(row);
            RebuildConditions();
            BeginInvoke(() => DisposeAll(row.Joiner, row.Field, row.Operator, row.Value, row.Type, row.Remove));
            SchedulePreview();
        };
        RefreshCondition(row, loadValues: true);
    }

    private void RefreshCondition(ConditionRow row, bool loadValues)
    {
        var (kind, values) = ColumnInfo(row.Field.SelectedItem as string);
        row.Kind = kind;
        if (loadValues) LoadSuggestions(row.Value, values);

        RuleOperator op = SelectedOperator(row);
        bool needsValue = ConditionalRuleEngine.NeedsValue(op);
        row.Value.Enabled = needsValue;
        if (!needsValue) row.Value.Text = string.Empty;
        row.Type.Text = !needsValue ? string.Empty
            : ConditionalRuleEngine.IsTextOperator(op) ? "any text"
            : KindText(kind);
    }

    private void RebuildConditions()
    {
        _conditionsTable.SuspendLayout();
        foreach (Control c in _conditionsTable.Controls.Cast<Control>().Where(c => c is Label && c != _ifLabel && !_conditions.Any(r => r.Type == c)).ToList())
            c.Dispose();
        _conditionsTable.Controls.Clear();
        _conditionsTable.RowStyles.Clear();
        _conditionsTable.RowCount = 0;
        AddGridRow(_conditionsTable, [null, ColumnCaption("Field"), ColumnCaption("Comparison"), ColumnCaption("Value"), ColumnCaption("Expected format"), null]);
        for (int i = 0; i < _conditions.Count; i++)
        {
            ConditionRow row = _conditions[i];
            Control first = i == 0 ? _ifLabel : row.Joiner;
            row.Remove.Enabled = _conditions.Count > 1;
            AddGridRow(_conditionsTable, [first, row.Field, row.Operator, row.Value, row.Type, row.Remove]);
        }
        _conditionsTable.ResumeLayout();
        _addConditionButton.Enabled = _conditions.Count < MaxConditions;
        UpdatePrecedenceNote();
    }

    private void UpdatePrecedenceNote()
    {
        var joins = _conditions.Skip(1).Select(c => c.Joiner.SelectedIndex).Distinct().Count();
        _precedenceLabel.Visible = joins > 1;
    }

    private static RuleOperator SelectedOperator(ConditionRow row) =>
        (row.Operator.SelectedItem as OperatorChoice)?.Op ?? RuleOperator.Equals;

    // ---- action rows --------------------------------------------------------------------------

    private void AddAction()
    {
        var row = new ActionRow
        {
            Field = FieldBox(), Value = ValueBox(), Type = CellLabel(string.Empty), Remove = RemoveButton("Remove this field")
        };
        // Default each new line to a field not already being set.
        var used = _actions.Select(a => a.Field.SelectedItem as string).ToHashSet();
        string? free = _columnNames.FirstOrDefault(n => !used.Contains(n));
        if (free is not null) row.Field.SelectedItem = free;
        _actions.Add(row);

        row.Field.SelectedIndexChanged += (_, _) => RefreshAction(row);
        row.Remove.Click += (_, _) =>
        {
            _actions.Remove(row);
            RebuildActions();
            BeginInvoke(() => DisposeAll(row.Field, row.Value, row.Type, row.Remove));
        };
        RefreshAction(row);
    }

    private void RefreshAction(ActionRow row)
    {
        var (kind, values) = ColumnInfo(row.Field.SelectedItem as string);
        row.Kind = kind;
        LoadSuggestions(row.Value, values);
        row.Type.Text = KindText(kind);
    }

    private void RebuildActions()
    {
        _actionsTable.SuspendLayout();
        _actionsTable.Controls.Clear();
        foreach (Control label in _actionLabels) label.Dispose();
        _actionLabels.Clear();
        _actionsTable.RowStyles.Clear();
        _actionsTable.RowCount = 0;
        AddGridRow(_actionsTable, Track([null, ColumnCaption("Field"), null, ColumnCaption("New value"), ColumnCaption("Expected format"), null]));
        for (int i = 0; i < _actions.Count; i++)
        {
            ActionRow row = _actions[i];
            Label set = CellLabel(i == 0 ? "SET" : "and", ContentAlignment.MiddleRight);
            if (i == 0) set.Font = new Font(Font, FontStyle.Bold);
            row.Remove.Enabled = _actions.Count > 1;
            Label to = CellLabel("to", ContentAlignment.MiddleCenter);
            _actionLabels.Add(set);
            _actionLabels.Add(to);
            AddGridRow(_actionsTable, [set, row.Field, to, row.Value, row.Type, row.Remove]);
        }
        _actionsTable.ResumeLayout();
        _addActionButton.Enabled = _actions.Count < MaxActions;
    }

    private Control?[] Track(Control?[] cells)
    {
        _actionLabels.AddRange(cells.OfType<Control>());
        return cells;
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private static void AddGridRow(TableLayoutPanel grid, Control?[] cells)
    {
        int r = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int c = 0; c < cells.Length; c++)
            if (cells[c] is { } control) grid.Controls.Add(control, c, r);
    }

    private static void DisposeAll(params Control[] controls)
    {
        foreach (Control c in controls) c.Dispose();
    }

    /// <summary>Column type plus its distinct existing values (for the suggestion list), cached per column.</summary>
    private (ValueKind Kind, string[] Values) ColumnInfo(string? column)
    {
        if (column is null || !_table.Columns.Contains(column)) return (ValueKind.Text, Array.Empty<string>());
        if (_columnInfo.TryGetValue(column, out var info)) return info;

        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in _table.Rows)
        {
            if (row[column] is string s && (s = s.Trim()).Length > 0) values.Add(s);
            if (values.Count > MaxSuggestions) break;
        }
        info = (ConditionalRuleEngine.InferValueKind(_table, column), values.Count > MaxSuggestions ? Array.Empty<string>() : values.ToArray());
        _columnInfo[column] = info;
        return info;
    }

    private static void LoadSuggestions(ComboBox box, string[] values)
    {
        string text = box.Text;
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(values);
        box.EndUpdate();
        box.Text = text;
    }

    private static string KindText(ValueKind kind) => kind switch
    {
        ValueKind.Integer => "whole number",
        ValueKind.Decimal => "number, e.g. 14.074",
        ValueKind.Date => "date, YYYYMMDD",
        ValueKind.Time => "UTC time, HHMM or HHMMSS",
        _ => "text"
    };

    // ---- building the rule --------------------------------------------------------------------

    /// <summary>Builds the rule from the current inputs, or returns an error message and the control to fix.</summary>
    private ConditionalRule? TryBuildRule(out string error, out Control? culprit)
    {
        error = string.Empty;
        culprit = null;

        var conditions = new List<RuleCondition>();
        foreach (ConditionRow row in _conditions)
        {
            if (row.Field.SelectedItem is not string field) { error = "Choose a field for every condition."; culprit = row.Field; return null; }
            RuleOperator op = SelectedOperator(row);
            string value = string.Empty;
            if (ConditionalRuleEngine.NeedsValue(op))
            {
                if (row.Value.Text.Trim().Length == 0)
                { error = $"Enter a value for the {field} condition (or use \"is blank\")."; culprit = row.Value; return null; }
                if (ConditionalRuleEngine.IsTextOperator(op)) value = row.Value.Text.Trim();
                else if (!ConditionalRuleEngine.TryNormalizeValue(row.Value.Text, row.Kind, out value, out string expected))
                { error = $"The {field} value should be {expected}."; culprit = row.Value; return null; }
            }
            var join = row.Joiner.SelectedIndex == 1 ? RuleCombinator.Or : RuleCombinator.And;
            conditions.Add(new RuleCondition(join, field, op, value));
        }

        var actions = new List<RuleAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ActionRow row in _actions)
        {
            if (row.Field.SelectedItem is not string field) { error = "Choose a field for every SET line."; culprit = row.Field; return null; }
            if (!seen.Add(field)) { error = $"{field} is set more than once. Remove the extra line."; culprit = row.Field; return null; }
            if (!ConditionalRuleEngine.TryNormalizeValue(row.Value.Text, row.Kind, out string value, out string expected))
            { error = $"The new {field} value should be {expected}."; culprit = row.Value; return null; }
            actions.Add(new RuleAction(field, value));
        }

        return new ConditionalRule(conditions, actions);
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void UpdatePreview()
    {
        ConditionalRule? rule = TryBuildRule(out string error, out Control? culprit);
        if (rule is null)
        {
            // A value not typed yet is normal while building the rule; a wrong value is shown in red.
            bool justEmpty = culprit is ComboBox box && box.Text.Trim().Length == 0;
            _matchLabel.ForeColor = justEmpty ? SystemColors.ControlText : Color.Firebrick;
            _matchLabel.Text = error;
            return;
        }
        try
        {
            int matches = ConditionalRuleEngine.CountMatches(_table, rule);
            _matchLabel.ForeColor = SystemColors.ControlText;
            _matchLabel.Text = $"{matches:N0} of {_table.Rows.Count:N0} rows match";
        }
        catch (Exception ex)
        {
            _matchLabel.ForeColor = Color.Firebrick;
            _matchLabel.Text = ex.Message;
        }
    }

    private void OnApply()
    {
        _previewTimer.Stop();
        ConditionalRule? rule = TryBuildRule(out string error, out Control? culprit);
        if (rule is null)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            culprit?.Focus();
            return;
        }
        Result = rule;
        DialogResult = DialogResult.OK;
    }
}
