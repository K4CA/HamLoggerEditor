namespace HamLoggerEditor.UI;

/// <summary>
/// Keeps the program running while any editor window is open, so a second log can be opened in its
/// own window (File &gt; New Window) and closing one window doesn't close the others.
/// </summary>
public sealed class MainWindowContext : ApplicationContext
{
    private int _openWindows;

    /// <param name="path">An ADIF file to load once the window is on screen, or null for an empty one.</param>
    public MainForm OpenWindow(string? path = null)
    {
        var form = new MainForm(this, path);
        // Offset each extra window so a new one doesn't hide the previous one exactly.
        if (_openWindows > 0)
        {
            form.StartPosition = FormStartPosition.Manual;
            Form? previous = Application.OpenForms.OfType<MainForm>().LastOrDefault();
            Point origin = previous?.Location ?? new Point(80, 80);
            form.Location = new Point(origin.X + 28, origin.Y + 28);
        }
        _openWindows++;
        form.FormClosed += (_, _) =>
        {
            if (--_openWindows <= 0) ExitThread();
        };
        form.Show();
        return form;
    }
}
