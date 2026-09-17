## Things to add to HamLoggerEditor

- Remove ID column from DataGridView.
- Create DataGridView columns and column names based on the adif data field names from the adif file. This can only be done for the first file. 
- Allow columns to be deleted after loading and review.
- Allow columns to be re-named and saved with the new name.
- Allow columns to be reordered left to right.
- Order rows by Date/Time ascending/descending order
- Ability to add another file(s) ... merge .adi files one at a time. Each new row from a new file will be identified with the filename in a temporary column.
- Allow selected rows to be saved as an .adi file.
- Search and Replace and allow multiple replacements to occur if that option is selected.
- Search for duplicate rows based on QSO_Date,Time_On,Band and Mode. if duplicate rows are found, highlight the duplicate rows.
- A TEST-FT8 button will be added to check for "FT8" in the comment column of each row. If it is there FT8 will be copied to the MODE column for that row.
- A TEST-FT4 button will be added to check for "FT4" in the comment column of each row. If it is there FT4 will be copied to the SUBMODE column and MSFK will be copied to the MODE column for that row.
- A TEST-FT2 button will be added to check for "FT2" in the comment column of each row. If it is there FT2 will be copied to the SUBMODE column and MSFK will be copied to the MODE column for that row.
- Export to CSV.
- Create an Insert file for SQLite and SQLServer databases.