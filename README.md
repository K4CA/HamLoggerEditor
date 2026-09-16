# HamLogger Contact Editor

A .NET 8 Windows Forms editor for ADIF (`.adi`) logs.

## Features

- **Columns come from the file.** Opening an ADIF file creates one grid column per ADIF field name found in that first file, in the order the fields first appear. (File > New starts with a standard set of columns.) The old internal ID column is gone; `APP_HAMLOGGER_ID` fields from files saved by the earlier version are ignored.
- **Review and change columns.** Right-click a column header to rename, delete, or move it left/right, or use **Columns > Manage Columns** to do several at once (changes apply on OK). You can also drag headers to reorder them. Saved files use the new names and the on-screen left-to-right order.
- **Sort by date/time.** The **Date/Time ▲ / ▼** buttons (or the View menu) sort by `QSO_DATE` + `TIME_ON`. Four-digit and six-digit times sort together correctly. Clicking any header still sorts by that column. Saving writes rows in the current sort order.
- **Merge files.** **Merge File** (Ctrl+M) adds the rows of another ADIF file, one file at a time. A temporary **Source File** column (shown first, shaded) shows which file each row came from. It is never written to ADIF, and you can remove it from the Columns menu. Fields are matched to existing columns by name, including columns you renamed. If the new file has fields the grid doesn't, you choose whether to add them as columns or skip them. After a merge, **Save** asks for a file name so the first file isn't overwritten by accident.
- **Save selected rows.** Select rows (Ctrl/Shift+click the row headers), then **Save Selected**.
- **Search and Replace** (Ctrl+H). Find Next and Replace work one cell at a time. **Replace All** is enabled only when **Allow multiple replacements** is checked, and asks for confirmation first. You can search all columns or just one, and turn on Match case or Match entire cell.
- **Find duplicates** (Ctrl+D). Rows with the same `QSO_DATE`, `TIME_ON`, `BAND` and `MODE` are highlighted (case-insensitive; `1234` and `123400` count as the same time). Alternating colors separate the duplicate groups. You can show only the duplicates, sorted by date/time, then delete the extra rows. **Clear Duplicate Highlights** removes the colors.
- **TEST-FT8** – for each row whose `COMMENT` contains `FT8`, sets `MODE = FT8`.
- **TEST-FT4** – for each row whose `COMMENT` contains `FT4`, sets `MODE = MFSK` and `SUBMODE = FT4`.
- **TEST-FT2** – for each row whose `COMMENT` contains `FT2`, sets `MODE = MFSK` and `SUBMODE = FT2`.
  - The text match ignores case. `MODE` / `SUBMODE` columns are created if the log doesn't have them yet. A summary shows how many rows matched and how many changed.
- **Export to CSV** – every row (current sort order) and every column (on-screen order); UTF-8 so Excel shows accented names correctly.
- **SQLite / SQL Server INSERT scripts** – asks for a table name, then writes a `.sql` file. The script creates the table if it doesn't exist (with an auto-number `Id` key and one text column per field) and inserts every row. Empty values become `NULL`. SQL Server scripts run in batches of 500 rows, separated by `GO`.
- Add and delete contacts. A new contact gets the current **UTC** date and time.
- Unsaved changes are tracked (`*` in the title bar), and you're asked to save before opening another file or closing.

## ADIF handling

- Tags are read case-insensitively. Field lengths are treated as UTF-8 byte counts (what this editor and most loggers write). Values that contain `<` or `>` are read correctly.
- A record missing its final `<EOR>` is still loaded.
- Saved files get a fresh header (`ADIF_VER 3.1.4`, `PROGRAMID`, `CREATED_TIMESTAMP`). Empty fields are left out.

## Build and run

1. Install Visual Studio 2022 with **.NET desktop development**, or install the .NET 8 SDK.
2. Open `HamLoggerEditor.slnx` (or `HamLoggerEditor.csproj`) in Visual Studio and press **F5**.
3. Or run this from a command prompt:

   ```text
   dotnet run --project HamLoggerEditor.csproj
   ```

## Project layout

| File | Purpose |
| --- | --- |
| `MainForm.cs` | Main window: grid, menus, toolbar and all commands |
| `Adif/AdifFile.cs` | ADIF reader and writer (any field names) |
| `Export/Exporters.cs` | CSV and SQLite / SQL Server script writers |
| `Dialogs/ColumnManagerDialog.cs` | Reorder / rename / delete columns |
| `Dialogs/SearchReplaceDialog.cs` | Search and Replace window |
| `Dialogs/PromptDialog.cs` | Simple text prompt (rename, table name) |
| `Models/Contact.cs` | The old fixed contact model (no longer used by the editor) |
# HamLoggerEditor
