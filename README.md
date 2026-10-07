# HamLogger Contact Editor

A .NET 8 Windows Forms editor for ADIF (`.adi`) logs.

## Features

- **Columns come from the file.** Opening an ADIF file creates one grid column per ADIF field name found in that first file, in the order the fields first appear. (File > New starts with a standard set of columns.) The old internal ID column is gone; `APP_HAMLOGGER_ID` fields from files saved by the earlier version are ignored.
- **Review and change columns.** Right-click a column header to rename, delete, or move it left/right, or use **Columns > Manage Columns** to do several at once (changes apply on OK). You can also drag headers to reorder them. Saved files use the new names and the on-screen left-to-right order.
- **Sort by date/time.** The **Date/Time ▲ / ▼** buttons (or the View menu) sort by `QSO_DATE` + `TIME_ON`. Four-digit and six-digit times sort together correctly. Clicking any header still sorts by that column. Saving writes rows in the current sort order.
- **Merge files.** **Merge File** (Ctrl+M) adds the rows of another ADIF file, one file at a time. A temporary **Source File** column (shown first, shaded) shows which file each row came from. It is never written to ADIF, and you can remove it from the Columns menu. Fields are matched to existing columns by name, including columns you renamed. If the new file has fields the grid doesn't, you choose whether to add them as columns or skip them. After a merge, **Save** asks for a file name so the first file isn't overwritten by accident.
- **Save selected rows.** Select rows (Ctrl/Shift+click the row headers), then **Save Selected**.
- **Search and Replace** (Ctrl+H). Find Next and Replace work one cell at a time. **Replace All** is enabled only when **Allow multiple replacements** is checked, and asks for confirmation first. You can search all columns or just one, and turn on Match case or Match entire cell.
- **Find duplicates** (Ctrl+D). Rows with the same `QSO_DATE`, `TIME_ON`, `BAND`, `MODE` and `CALL` are highlighted (case-insensitive; `1234` and `123400` count as the same time). Pick the key in the toolbar's **Dup key** box (or **Edit > Duplicate Key**): *Date, Time, Band, Mode, Call*, or *Date, Time, Band, Mode, Submode, Call* to also require a matching `SUBMODE` (so an FT4 and an FT2 contact at the same minute are not duplicates). Changing the key clears existing highlights. Alternating colors separate the duplicate groups. You can show only the duplicates, sorted by date/time. **Clear Duplicate Highlights** removes the colors. **Remove Duplicates** (Edit menu or toolbar) deletes the duplicate rows the search highlighted (or, if nothing is highlighted, the duplicates found with the selected key): it keeps one row per group — the one with the most fields filled in — and, before deleting the others, copies into it every field it is missing from them. The confirmation shows how many values will be copied and how many differing values (where the kept row already has a value) will be discarded.
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
## Code contributor: CLAUDE CODE - OPUS 5

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

# Latest Update

Update HamLoggerEditor project to include the following funtionality:


 If (Column A) (!=, =, >, <) ( Value1 ) AND / OR
	(Column B) (!=, =, >, <) ( Value2 ) THEN
	(Column C) = (Value3)
	
	- Where Column A, B and C are dropdowns showing a dynamic list of existion columns contained by the DataGridView.
	- Value1, Value2 and Value3 are of the datatype defined by the selected column.
	

**New: `Rules/ConditionalRule.cs`** — the rule data model plus `ConditionalRuleEngine`, which infers each column's data type (Date/Time by name, Integer/Decimal by sampling every value in the column, Text otherwise) and evaluates/applies the rule.

**New: `Dialogs/ConditionalRuleDialog.cs`** — the IF/THEN builder UI. Add as many conditions as you need (up to 10), each joined with AND or OR (AND is checked before OR). Comparisons: equals, does not equal, greater/less than (or equal), contains, does not contain, starts with, ends with, is blank, is not blank. The THEN section can set several fields at once (e.g. MODE = MFSK and SUBMODE = FT4); leave a value empty to clear a field. Value boxes suggest the values already in that column and check what you type against the column type (dates YYYYMMDD or YYYY-MM-DD, times HHMM/HHMMSS, numbers). A live count at the bottom shows how many rows match before you apply.

**`MainForm.cs`** — added **Edit → Apply Conditional Rule...** (Ctrl+R) and a **Conditional Rule** toolbar button. Clicking it opens the dialog, then previews how many rows match before doing anything (asks "This will set X = Y on N matching row(s). Continue?"), applies inside the existing `Bulk()` helper so the grid doesn't repaint per-row, and reports matched/updated counts — following the same pattern as the existing TEST-FT8/FT4/FT2 commands.

Since it's an SDK-style project, the new files under `Rules/` and `Dialogs/` are picked up automatically by the project's default glob — no `.csproj` edit needed. Go ahead and rebuild in Visual Studio; let me know how it goes or if anything needs adjusting (e.g., different default operator order, or seeding Value1/2/3 with an existing cell's value when you open the dialog).


