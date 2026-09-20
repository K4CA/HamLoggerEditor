using System.Data;
using System.Globalization;
using HamLoggerEditor.Rules;

namespace HamLoggerEditor.Dialogs;

/// <summary>
/// Builds one IF/THEN rule: IF (ColumnA op Value1) [AND/OR (ColumnB op Value2)] THEN (ColumnC = Value3).
/// Column dropdowns list the grid's current columns; each value editor's control type (text box, number,
/// date or time picker) follows the data type inferred for the column just picked. Nothing is applied to
/// the grid here -- OK just returns the built <see cref="Result"/> for the caller to run and confirm.
/// </summary>
public sealed class ConditionalRuleDialog : Form
{
    private enum ColumnSlot { A, B, C }

    private readonly DataTable table;

    private readonly ComboBox columnABox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox operatorABox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label kindALabel = new() { ForeColor = SystemColors.GrayText };

    private readonly CheckBox addSecondCondition = new() { Text = "Add a second condition" };
    private readonly RadioButton andRadio = new() { Text = "AND", Checked = true };
    private readonly RadioButton orRadio = new() { Text = "OR" };

    private readonly ComboBox columnBBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
    private readonly ComboBox operatorBBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
    private readonly Label kindBLabel = new() { ForeColor = SystemColors.GrayText };

    private readonly ComboBox columnCBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label kindCLabel = new() { ForeColor = SystemColors.GrayText };

    private static readonly Rectangle ValueABounds = new(275, 12, 150, 23);
    private static readonly Rectangle ValueBBounds = new(275, 84, 150, 23);
    private static readonly Rectangle ValueCBounds = new(275, 156, 150, 23);
    private static readonly string[] OperatorSymbols = ["=", "!=", ">", "<"];

    private ValueKind kindA = ValueKind.Text, kindB = ValueKind.Text, kindC = ValueKind.Text;
    private Control? valueAControl, valueBControl, valueCControl;

    /// <summary>The rule the user built, set only when the dialog closes with DialogResult.OK.</summary>
    public ConditionalRule? Result { get; private set; }

    public ConditionalRuleDialog(DataTable table, IReadOnlyList<string> columnNames)
    {
        this.table = table;

        Text = "Apply Conditional Rule";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(660, 330);

        var ifLabel = new Label { Text = "IF", Font = new Font(Font, FontStyle.Bold), Left = 12, Top = 15, Width = 30 };
        columnABox.SetBounds(50, 12, 150, 23);
        operatorABox.SetBounds(210, 12, 55, 23);
        operatorABox.Items.AddRange(OperatorSymbols);
        operatorABox.SelectedIndex = 0;
        kindALabel.SetBounds(435, 15, 210, 20);

        addSecondCondition.SetBounds(50, 48, 170, 22);
        andRadio.SetBounds(230, 48, 55, 22);
        orRadio.SetBounds(285, 48, 50, 22);

        columnBBox.SetBounds(50, 84, 150, 23);
        operatorBBox.SetBounds(210, 84, 55, 23);
        operatorBBox.Items.AddRange(OperatorSymbols);
        operatorBBox.SelectedIndex = 0;
        kindBLabel.SetBounds(435, 87, 210, 20);

        var thenLabel = new Label { Text = "THEN", Font = new Font(Font, FontStyle.Bold), Left = 12, Top = 159, Width = 50 };
        columnCBox.SetBounds(66, 156, 150, 23);
        var equalsLabel = new Label { Text = "=", Left = 226, Top = 159, Width = 20 };
        kindCLabel.SetBounds(435, 159, 210, 20);

        var hint = new Label
        {
            Text = "Rows where the condition is true will have the THEN column set to the value shown.\n" +
                   "Comparisons use the column's data type: numbers, dates and times compare by value; everything else compares as text.",
            Left = 12, Top = 200, Width = 630, Height = 40, ForeColor = SystemColors.GrayText
        };

        var apply = new Button { Text = "Apply", Left = 480, Top = 290, Width = 80, Height = 28 };
        var cancel = new Button { Text = "Cancel", Left = 566, Top = 290, Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        apply.Click += (_, _) => OnApply();

        Controls.AddRange([
            ifLabel, columnABox, operatorABox, kindALabel,
            addSecondCondition, andRadio, orRadio,
            columnBBox, operatorBBox, kindBLabel,
            thenLabel, columnCBox, equalsLabel, kindCLabel,
            hint, apply, cancel
        ]);
        AcceptButton = apply;
        CancelButton = cancel;

        foreach (string name in columnNames)
        {
            columnABox.Items.Add(name);
            columnBBox.Items.Add(name);
            columnCBox.Items.Add(name);
        }
        if (columnABox.Items.Count > 0) columnABox.SelectedIndex = 0;
        if (columnBBox.Items.Count > 0) columnBBox.SelectedIndex = 0;
        if (columnCBox.Items.Count > 0) columnCBox.SelectedIndex = 0;

        columnABox.SelectedIndexChanged += (_, _) => RefreshValueControl(ColumnSlot.A);
        columnBBox.SelectedIndexChanged += (_, _) => RefreshValueControl(ColumnSlot.B);
        columnCBox.SelectedIndexChanged += (_, _) => RefreshValueControl(ColumnSlot.C);

        addSecondCondition.CheckedChanged += (_, _) =>
        {
            bool on = addSecondCondition.Checked;
            columnBBox.Enabled = operatorBBox.Enabled = andRadio.Enabled = orRadio.Enabled = on;
            if (valueBControl is not null) valueBControl.Enabled = on;
        };

        RefreshValueControl(ColumnSlot.A);
        RefreshValueControl(ColumnSlot.B);
        RefreshValueControl(ColumnSlot.C);
        if (valueBControl is not null) valueBControl.Enabled = false;
    }

    /// <summary>Re-infers the column's data type and swaps in the matching value editor control.</summary>
    private void RefreshValueControl(ColumnSlot slot)
    {
        ComboBox box = slot switch { ColumnSlot.A => columnABox, ColumnSlot.B => columnBBox, _ => columnCBox };
        Rectangle bounds = slot switch { ColumnSlot.A => ValueABounds, ColumnSlot.B => ValueBBounds, _ => ValueCBounds };
        Label label = slot switch { ColumnSlot.A => kindALabel, ColumnSlot.B => kindBLabel, _ => kindCLabel };

        string? column = box.SelectedItem as string;
        ValueKind kind = column is null ? ValueKind.Text : ConditionalRuleEngine.InferValueKind(table, column);
        label.Text = KindText(kind);

        Control newControl = CreateEditor(kind, bounds);
        switch (slot)
        {
            case ColumnSlot.A:
                if (valueAControl is not null) { Controls.Remove(valueAControl); valueAControl.Dispose(); }
                valueAControl = newControl;
                kindA = kind;
                break;
            case ColumnSlot.B:
                if (valueBControl is not null) { Controls.Remove(valueBControl); valueBControl.Dispose(); }
                valueBControl = newControl;
                kindB = kind;
                newControl.Enabled = addSecondCondition.Checked;
                break;
            default:
                if (valueCControl is not null) { Controls.Remove(valueCControl); valueCControl.Dispose(); }
                valueCControl = newControl;
                kindC = kind;
                break;
        }
        Controls.Add(newControl);
    }

    private static string KindText(ValueKind kind) => kind switch
    {
        ValueKind.Integer => "(whole number)",
        ValueKind.Decimal => "(decimal number)",
        ValueKind.Date => "(date, YYYYMMDD)",
        ValueKind.Time => "(time, HHMMSS)",
        _ => "(text)"
    };

    private static Control CreateEditor(ValueKind kind, Rectangle bounds)
    {
        Control control = kind switch
        {
            ValueKind.Integer => new NumericUpDown { Minimum = -999_999_999, Maximum = 999_999_999, DecimalPlaces = 0 },
            ValueKind.Decimal => new NumericUpDown { Minimum = -1_000_000_000, Maximum = 1_000_000_000, DecimalPlaces = 6, Increment = 0.000001M },
            ValueKind.Date => new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" },
            ValueKind.Time => new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm:ss", ShowUpDown = true },
            _ => new TextBox()
        };
        control.Bounds = bounds;
        return control;
    }

    private static string ExtractValue(Control control, ValueKind kind) => kind switch
    {
        ValueKind.Integer => ((NumericUpDown)control).Value.ToString("0", CultureInfo.InvariantCulture),
        ValueKind.Decimal => ((NumericUpDown)control).Value.ToString(CultureInfo.InvariantCulture),
        ValueKind.Date => ((DateTimePicker)control).Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        ValueKind.Time => ((DateTimePicker)control).Value.ToString("HHmmss", CultureInfo.InvariantCulture),
        _ => ((TextBox)control).Text.Trim()
    };

    private static RuleOperator ParseOperator(string symbol) => symbol switch
    {
        "!=" => RuleOperator.NotEquals,
        ">" => RuleOperator.GreaterThan,
        "<" => RuleOperator.LessThan,
        _ => RuleOperator.Equals
    };

    private void OnApply()
    {
        if (columnABox.SelectedItem is not string colA || valueAControl is null)
        {
            Warn("Choose a column for the IF condition.");
            return;
        }
        if (columnCBox.SelectedItem is not string colC || valueCControl is null)
        {
            Warn("Choose a column for the THEN action.");
            return;
        }

        RuleOperator opA = ParseOperator((string)operatorABox.SelectedItem!);
        string valA = ExtractValue(valueAControl, kindA);

        bool second = addSecondCondition.Checked;
        string? colB = null;
        RuleOperator? opB = null;
        string? valB = null;
        if (second)
        {
            if (columnBBox.SelectedItem is not string cb || valueBControl is null)
            {
                Warn("Choose a column for the second condition, or uncheck \"Add a second condition\".");
                return;
            }
            colB = cb;
            opB = ParseOperator((string)operatorBBox.SelectedItem!);
            valB = ExtractValue(valueBControl, kindB);
        }

        string valC = ExtractValue(valueCControl, kindC);
        RuleCombinator combinator = andRadio.Checked ? RuleCombinator.And : RuleCombinator.Or;

        Result = new ConditionalRule(colA, opA, valA, second, combinator, colB, opB, valB, colC, valC);
        DialogResult = DialogResult.OK;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
