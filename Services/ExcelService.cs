using ClosedXML.Excel;
using HisaabKitaab.Models;
using HisaabKitaab.Services.Excel;

namespace HisaabKitaab.Services;

/// <summary>
/// All reading and writing of the monthly Excel workbooks. Nothing else in the
/// app touches ClosedXML.
///
/// Files live at <c>{DataFolder}/{year}/{Mon}_{year}.xlsx</c>, e.g.
/// <c>Documents/Hisaab Kitaab/2026/Sept_2026.xlsx</c>.
///
/// Every operation opens the file, changes only the rows it needs to and saves,
/// so edits made directly in Excel between operations are preserved.
/// </summary>
public class ExcelService
{
    /// <summary>
    /// Bump when the workbook layout changes in a way older versions can't read.
    /// </summary>
    public const int SchemaVersion = 1;

    public const decimal MaxAmount = 1_000_000_000_000m;

    private const string SchemaPropertyName = "HisaabKitaab.SchemaVersion";

    public ExcelService(string dataFolder)
    {
        DataFolder = dataFolder;
    }

    public string DataFolder { get; }

    public string GetYearFolder(int year) => Path.Combine(DataFolder, year.ToString("D4"));

    public string GetMonthFilePath(YearMonth month) => Path.Combine(GetYearFolder(month.Year), month.FileName);

    public bool MonthFileExists(YearMonth month) => File.Exists(GetMonthFilePath(month));

    /// <summary>
    /// Months that have a workbook on disk, oldest first.
    /// </summary>
    public IReadOnlyList<YearMonth> GetExistingMonths()
    {
        if (!Directory.Exists(DataFolder))
            return Array.Empty<YearMonth>();

        var months = new List<YearMonth>();
        foreach (var yearDir in Directory.EnumerateDirectories(DataFolder))
        {
            if (!int.TryParse(Path.GetFileName(yearDir), out var year))
                continue;

            foreach (var file in Directory.EnumerateFiles(yearDir, "*.xlsx"))
            {
                if (YearMonth.TryParseFileName(Path.GetFileName(file), out var month) && month.Year == year)
                    months.Add(month);
            }
        }

        months.Sort();
        return months;
    }

    /// <summary>
    /// Creates the month's workbook if it doesn't exist yet. Returns true if a file was created.
    /// </summary>
    public bool EnsureMonthWorkbook(YearMonth month)
    {
        var path = GetMonthFilePath(month);
        if (File.Exists(path))
            return false;

        using var workbook = CreateWorkbook(month);
        Save(workbook, path);
        return true;
    }

    /// <summary>
    /// Reads the month's expenses. A month with no file yet returns an empty list.
    /// Rows that couldn't be read are listed in <see cref="ExpenseSheetData.Problems"/>
    /// and left as they are in the file.
    /// </summary>
    public ExpenseSheetData LoadExpenses(YearMonth month)
    {
        var path = GetMonthFilePath(month);
        if (!File.Exists(path))
            return new ExpenseSheetData(month, false, Array.Empty<Expense>(), Array.Empty<SheetProblem>());

        using var workbook = Open(path);
        var sheet = ExpensesSheet.GetOrCreate(workbook, month);
        var (expenses, problems) = sheet.ReadAll();

        if (sheet.IsModified)
        {
            // New IDs were assigned to rows added by hand. Save them so later
            // edits can find those rows. If the file is open in Excel we carry on;
            // the IDs are just temporary until the next successful save.
            try
            {
                Save(workbook, path);
            }
            catch (WorkbookLockedException)
            {
            }
        }

        return new ExpenseSheetData(month, true, expenses, problems);
    }

    /// <summary>
    /// Adds an expense to the workbook for its date's month, creating the
    /// workbook if needed. Returns the saved expense (with its ID and cleaned-up text).
    /// </summary>
    public Expense AddExpense(Expense expense)
    {
        expense = Normalize(expense);
        if (expense.Id == Guid.Empty)
            expense = expense with { Id = Guid.NewGuid() };

        var month = YearMonth.Of(expense.Date);
        var path = GetMonthFilePath(month);

        using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
        ExpensesSheet.GetOrCreate(workbook, month).Insert(expense);
        Save(workbook, path);
        return expense;
    }

    /// <summary>
    /// Replaces an existing expense. If its date moved to another month, it is
    /// moved to that month's workbook.
    /// </summary>
    /// <param name="originalMonth">The month the expense was loaded from.</param>
    /// <exception cref="KeyNotFoundException">The expense is no longer in the file.</exception>
    public Expense UpdateExpense(YearMonth originalMonth, Expense expense)
    {
        expense = Normalize(expense);
        if (expense.Id == Guid.Empty)
            throw new ArgumentException("Expense has no ID.", nameof(expense));

        var newMonth = YearMonth.Of(expense.Date);
        if (newMonth == originalMonth)
        {
            var path = GetMonthFilePath(originalMonth);
            using var workbook = OpenExisting(path);
            if (!ExpensesSheet.GetOrCreate(workbook, originalMonth).Update(expense))
                throw NotFound(expense.Id, originalMonth);
            Save(workbook, path);
            return expense;
        }

        // Moving between files: add to the new month first, then remove from the old one.
        // If the removal fails we undo the add, so the expense is never lost or doubled.
        EnsureExists(originalMonth, expense.Id);
        AddExpense(expense);
        try
        {
            DeleteExpense(originalMonth, expense.Id);
        }
        catch
        {
            try
            {
                DeleteExpense(newMonth, expense.Id);
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException)
            {
                // Report the original failure; the copy in the new month will show up on reload.
            }

            throw;
        }

        return expense;
    }

    /// <exception cref="KeyNotFoundException">The expense is no longer in the file.</exception>
    public void DeleteExpense(YearMonth month, Guid id)
    {
        var path = GetMonthFilePath(month);
        using var workbook = OpenExisting(path);
        if (!ExpensesSheet.GetOrCreate(workbook, month).Delete(id))
            throw NotFound(id, month);
        Save(workbook, path);
    }

    /// <summary>
    /// Changes the family member name on every expense in <paramref name="year"/>'s
    /// workbooks. Files that can't be updated (e.g. open in Excel) are reported
    /// in the result rather than stopping the others.
    /// </summary>
    public RenameResult RenameFamilyMember(int year, string oldName, string newName)
    {
        var updated = new List<string>();
        var failed = new List<string>();
        var rows = 0;

        foreach (var month in GetExistingMonths().Where(m => m.Year == year))
        {
            var path = GetMonthFilePath(month);
            try
            {
                using var workbook = Open(path);
                var sheet = ExpensesSheet.Find(workbook, month);
                var count = sheet?.RenameFamilyMember(oldName, newName) ?? 0;
                if (count == 0)
                    continue;

                Save(workbook, path);
                rows += count;
                updated.Add(month.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                failed.Add(month.FileName);
            }
        }

        return new RenameResult(rows, updated, failed);
    }

    // ---- Validation ---------------------------------------------------------

    private static Expense Normalize(Expense expense)
    {
        if (expense.Amount <= 0)
            throw new ArgumentException("Amount must be more than zero.", nameof(expense));
        if (expense.Amount >= MaxAmount)
            throw new ArgumentException("Amount is too large.", nameof(expense));
        if (!Enum.IsDefined(expense.PaymentMethod))
            throw new ArgumentException("Unknown payment method.", nameof(expense));

        return expense with
        {
            Amount = Math.Round(expense.Amount, 2, MidpointRounding.AwayFromZero),
            FamilyMember = expense.FamilyMember?.Trim() ?? string.Empty,
            Category = expense.Category?.Trim() ?? string.Empty,
            Note = expense.Note?.Trim() ?? string.Empty,
        };
    }

    private void EnsureExists(YearMonth month, Guid id)
    {
        var data = LoadExpenses(month);
        if (data.Expenses.All(e => e.Id != id))
            throw NotFound(id, month);
    }

    private static KeyNotFoundException NotFound(Guid id, YearMonth month) =>
        new($"That expense is no longer in {month.FileName}. It may have been changed in Excel; reload and try again. (ID {id})");

    // ---- Files --------------------------------------------------------------

    private static XLWorkbook CreateWorkbook(YearMonth month)
    {
        var workbook = new XLWorkbook();
        workbook.Properties.Title = $"Hisaab Kitaab - {month.DisplayName}";
        workbook.Properties.Author = "Hisaab Kitaab";
        ExpensesSheet.GetOrCreate(workbook, month);
        return workbook;
    }

    private static XLWorkbook OpenExisting(string path)
    {
        if (!File.Exists(path))
            throw new KeyNotFoundException($"{Path.GetFileName(path)} doesn't exist.");
        return Open(path);
    }

    private static XLWorkbook Open(string path)
    {
        // Read through a shared stream so this works even while Excel has the file open.
        var buffer = new MemoryStream();
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            file.CopyTo(buffer);
        buffer.Position = 0;

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(buffer);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} couldn't be opened as an Excel workbook.", ex);
        }

        var version = ReadSchemaVersion(workbook);
        if (version > SchemaVersion)
        {
            workbook.Dispose();
            throw new NotSupportedException(
                $"{Path.GetFileName(path)} was saved by a newer version of Hisaab Kitaab. Update the app to open it.");
        }

        return workbook;
    }

    private static void Save(XLWorkbook workbook, string path)
    {
        WriteSchemaVersion(workbook);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Save beside the real file, then swap it in, so a failed save never
        // leaves a half-written workbook behind.
        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $"~{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Via a stream because ClosedXML picks the format from the file extension.
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                workbook.SaveAs(stream);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new WorkbookLockedException(path, ex);
        }
    }

    private static int ReadSchemaVersion(XLWorkbook workbook)
    {
        var property = workbook.CustomProperties.FirstOrDefault(p => p.Name == SchemaPropertyName);
        if (property is null)
            return 0;

        try
        {
            return Convert.ToInt32(property.Value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static void WriteSchemaVersion(XLWorkbook workbook)
    {
        if (workbook.CustomProperties.Any(p => p.Name == SchemaPropertyName))
            workbook.CustomProperties.Delete(SchemaPropertyName);
        workbook.CustomProperties.Add(SchemaPropertyName, SchemaVersion);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
