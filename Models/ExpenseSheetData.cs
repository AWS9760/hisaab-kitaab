namespace HisaabKitaab.Models;

/// <summary>
/// A row in a sheet that couldn't be read. The row is left untouched in the
/// file so nothing is lost; the user can fix it in Excel.
/// </summary>
/// <param name="RowNumber">1-based Excel row number.</param>
public record SheetProblem(string SheetName, int RowNumber, string Message)
{
    public override string ToString() => $"{SheetName} row {RowNumber}: {Message}";
}

/// <summary>
/// Everything read from one month's Expenses sheet.
/// </summary>
/// <param name="Error">Set when the whole file couldn't be read (e.g. not an Excel file).</param>
public record ExpenseSheetData(
    YearMonth Month,
    bool FileExists,
    IReadOnlyList<Expense> Expenses,
    IReadOnlyList<SheetProblem> Problems,
    string? Error = null);

/// <summary>
/// Outcome of renaming a family member across a year's workbooks.
/// </summary>
public record RenameResult(int RowsUpdated, IReadOnlyList<string> FilesUpdated, IReadOnlyList<string> FilesFailed);
