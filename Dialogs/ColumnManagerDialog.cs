using HamLoggerEditor.Adif;

namespace HamLoggerEditor.Dialogs;

/// <summary>A grid column as it will be laid out. <see cref="Key"/> is the existing column name (null for a new column).</summary>
public sealed class ColumnSpec(string? key, string name, int width)
{
    public string? Key { get; } = key;
    public string Name { get; set; } = name;
    public int Width { get; set; } = width;

    public override string ToString() =>
        Key is not null && !Key.Equals(Name, StringComparison.Ordinal) ? $"{Name}    (was {Key})" : Name;
}

/// <summary>Review the columns: reorder (top = leftmost), rename, or delete them. Nothing changes until OK.</summary>
public sealed class ColumnManagerDialog : Form
{
    private readonly ListBox list = new();
    private readonly Label summary = new();
    private readonly List<string> deleted = [];

    public List<ColumnSpec> Columns { get; }

    public ColumnManagerDialog(IEnumerable<ColumnSpec> columns)
    {
        Columns = columns.Select(c => new ColumnSpec(c.Key, c.Name, c.Width)).ToList();

        Text = "Manage Columns";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 460);
        MinimumSize = new Size(420, 360);

        var hint = new Label
        {
            Text = "Top of the list is the leftmost column. Changes are applied when you click OK.",
            Left = 12, Top = 10, Width = 446, Height = 20,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        list.SetBounds(12, 34, 320, 370);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.SelectionMode = SelectionMode.MultiExtended;
        list.IntegralHeight = false;
        list.DoubleClick += (_, _) => RenameSelected();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };

        Button MakeButton(string text, int top, EventHandler click)
        {
            var b = new Button { Text = text, Left = 342, Top = top, Width = 116, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            b.Click += click;
            Controls.Add(b);
            return b;
        }
        MakeButton("Move Left (Up)", 34, (_, _) => MoveSelected(-1));
        MakeButton("Move Right (Down)", 68, (_, _) => MoveSelected(+1));
        MakeButton("Rename...", 112, (_, _) => RenameSelected());
        MakeButton("Delete", 146, (_, _) => DeleteSelected());

        summary.SetBounds(12, 420, 250, 30);
        summary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        var ok = new Button { Text = "OK", Left = 302, Top = 420, Width = 75, Height = 28, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        var cancel = new Button { Text = "Cancel", Left = 383, Top = 420, Width = 75, Height = 28, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        ok.Click += (_, _) =>
        {
            if (Columns.Count == 0)
            {
                MessageBox.Show(this, "At least one column must remain.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        Controls.AddRange([hint, list, summary, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
        RefreshList();
    }

    private void RefreshList(IEnumerable<int>? select = null)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (ColumnSpec spec in Columns) list.Items.Add(spec);
        foreach (int i in select ?? []) if (i >= 0 && i < list.Items.Count) list.SetSelected(i, true);
        list.EndUpdate();
        summary.Text = deleted.Count == 0 ? $"{Columns.Count} column(s)" : $"{Columns.Count} column(s); deleting: {string.Join(", ", deleted)}";
    }

    private void MoveSelected(int direction)
    {
        var indices = list.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        if (indices.Count == 0) return;
        if (direction < 0 && indices[0] == 0) return;
        if (direction > 0 && indices[^1] == Columns.Count - 1) return;
        if (direction > 0) indices.Reverse();
        foreach (int i in indices)
            (Columns[i], Columns[i + direction]) = (Columns[i + direction], Columns[i]);
        RefreshList(indices.Select(i => i + direction));
    }

    private void RenameSelected()
    {
        if (list.SelectedIndex < 0) return;
        int index = list.SelectedIndex;
        ColumnSpec spec = Columns[index];
        string? name = PromptDialog.Show(this, "Rename Column", $"New ADIF field name for {spec.Name}:", spec.Name,
            value => ValidateName(value, spec));
        if (name is null) return;
        spec.Name = name.ToUpperInvariant();
        RefreshList([index]);
    }

    private string? ValidateName(string value, ColumnSpec self)
    {
        if (!AdifFile.IsValidFieldName(value))
            return "ADIF field names may contain only letters, digits and underscores, and cannot start with an underscore.";
        if (Columns.Any(s => !ReferenceEquals(s, self) && s.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))
            return $"A column named {value.ToUpperInvariant()} already exists.";
        return null;
    }

    private void DeleteSelected()
    {
        var indices = list.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        if (indices.Count == 0) return;
        foreach (int i in indices)
        {
            deleted.Add(Columns[i].Key ?? Columns[i].Name);
            Columns.RemoveAt(i);
        }
        RefreshList([Math.Min(indices[^1], Columns.Count - 1)]);
    }
}
