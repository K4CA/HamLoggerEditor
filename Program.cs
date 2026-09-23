using HamLoggerEditor.UI;

namespace HamLoggerEditor;

internal static class Program
{
    /// <param name="args">
    /// Optional ADIF file to open. Windows passes this when a .adi file is double-clicked
    /// (see <see cref="Services.FileAssociationService"/>).
    /// </param>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        string? path = args.FirstOrDefault(a => a.Length > 0 && a[0] != '/' && a[0] != '-');
        var context = new MainWindowContext();
        context.OpenWindow(path);
        Application.Run(context);
    }

    private static void ReportFatal(Exception? exception) =>
        MessageBox.Show(exception?.ToString() ?? "Unknown error.", "HamLogger Contact Editor",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
}
