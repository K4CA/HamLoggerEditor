### CLAUDE.md - WinForms (.NET 8/9) Guidelines

### Build & Test Commands

* Build solution: dotnet build
* Clean solution: dotnet clean
* Run unit tests: dotnet test
* Run application: dotnet run --project <Path-To-WinForms-Project>.csproj

### Framework & UI Architecture

* **Designer Files:** NEVER manually modify <FormName>.Designer.cs or InitializeComponent() unless explicitly requested. Claude should write UI wire-up logic in the main <FormName>.cs file or via programmatically registered events.
* **Separation of Concerns:** Keep code-behind clean. UI event handlers should strictly capture input and delegate business/data logic to independent service classes or controllers.
* **Modern C# Usage:** Leverage .NET 8/9 paradigms: use file-scoped namespaces, pattern matching, and primary constructors where appropriate.
* **High DPI and Fonts:** Ensure UI scaling compatibility by using modern application-wide settings in Program.cs (ApplicationConfiguration.Initialize()). Use layout panels (TableLayoutPanel, FlowLayoutPanel) instead of hardcoded coordinate bounds where possible to ensure smooth resizing.

### Asynchronous UI & Responsiveness

* **Keep UI Responsive:** Always use async/await for long-running processes (I/O, database queries, network requests).
* **Thread Safety:** Do not manipulate UI controls from a background thread. Always check InvokeRequired or use Invoke / BeginInvoke to marshal actions back to the main UI thread.
* **Avoid Deadlocks:** Never call .Result or .Wait() on an async Task.

### Naming & Style Conventions

* **Classes/Forms/Methods:** PascalCase (e.g., MainForm, ProcessData).
* **UI Control Instances:** Use explicit type suffixes (e.g., SubmitButton, MainDataGridView, UsernameTextBox, StatusStrip) rather than legacy Hungarian prefixes.
* **Private Fields:** camelCase with a leading underscore (e.g., _customerService).

### Error Handling & Diagnostics

* Wrap top-level UI entry points (like button click events) in try-catch blocks to prevent catastrophic desktop crashes. Show structured user errors using MessageBox.Show.
* Use professional logging providers (such as Serilog or Microsoft.Extensions.Logging) targeting file/event logs rather than using Console.WriteLine.