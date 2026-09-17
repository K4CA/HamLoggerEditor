namespace HamLoggerEditor.Dialogs;

/// <summary>A small single-line text prompt.</summary>
public static class PromptDialog
{
    /// <param name="validate">Returns an error message, or null when the value is acceptable.</param>
    public static string? Show(IWin32Window owner, string title, string label, string defaultValue,
        Func<string, string?>? validate = null)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 120)
        };
        var prompt = new Label { Text = label, Left = 12, Top = 12, Width = 356, AutoSize = false, Height = 20 };
        var input = new TextBox { Text = defaultValue, Left = 12, Top = 36, Width = 356 };
        var ok = new Button { Text = "OK", Left = 212, Top = 78, Width = 75 };
        var cancel = new Button { Text = "Cancel", Left = 293, Top = 78, Width = 75, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            string? error = validate?.Invoke(input.Text.Trim());
            if (error is not null)
            {
                MessageBox.Show(form, error, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                input.Focus();
                return;
            }
            form.DialogResult = DialogResult.OK;
        };
        form.Controls.AddRange([prompt, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => input.SelectAll();

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }
}
