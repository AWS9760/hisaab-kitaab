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

    private readonly Func<CardSettings>? _cardSettings;

    /// <param name="cardSettings">
    /// Supplies the credit card's limit and due day for the Credit Card sheet.
    /// Read at each save, so changes in Settings show up the next time a month is saved.
    /// </param>
    public ExcelService(string dataFolder, Func<CardSettings>? cardSettings = null)
    {
        DataFolder = dataFolder;
        _cardSettings = cardSettings;
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

    // ---- Bank & Cash --------------------------------------------------------

    /// <summary>
    /// Reads the month's Bank &amp; Cash sheet. A month with no file (or a file
    /// from before this sheet existed) returns an empty log with carried-forward openings.
    /// </summary>
    public BankCashSheetData LoadBankCash(YearMonth month)
    {
        var path = GetMonthFilePath(month);
        if (!File.Exists(path))
            return BankCashSheetData.Empty(month);

        using var workbook = Open(path);
        var sheet = BankCashSheet.Find(workbook, month);
        if (sheet is null)
            return BankCashSheetData.Empty(month, fileExists: true);

        var data = sheet.Read();
        if (sheet.IsModified)
        {
            // New IDs for rows added by hand; see LoadExpenses.
            try
            {
                Save(workbook, path);
            }
            catch (WorkbookLockedException)
            {
            }
        }

        return data;
    }

    public BankCashEntry AddBankCashEntry(BankCashEntry entry)
    {
        entry = Normalize(entry);
        if (entry.Id == Guid.Empty)
            entry = entry with { Id = Guid.NewGuid() };

        var month = YearMonth.Of(entry.Date);
        var path = GetMonthFilePath(month);
        using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
        BankCashSheet.GetOrCreate(workbook, month).Insert(entry);
        Save(workbook, path);
        return entry;
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public BankCashEntry UpdateBankCashEntry(YearMonth originalMonth, BankCashEntry entry)
    {
        entry = Normalize(entry);
        if (entry.Id == Guid.Empty)
            throw new ArgumentException("Entry has no ID.", nameof(entry));

        var newMonth = YearMonth.Of(entry.Date);
        if (newMonth == originalMonth)
        {
            var path = GetMonthFilePath(originalMonth);
            using var workbook = OpenExisting(path);
            if (!BankCashSheet.GetOrCreate(workbook, originalMonth).Update(entry))
                throw EntryNotFound(entry.Id, originalMonth);
            Save(workbook, path);
            return entry;
        }

        // Same approach as moving an expense between months.
        if (LoadBankCash(originalMonth).Entries.All(e => e.Id != entry.Id))
            throw EntryNotFound(entry.Id, originalMonth);
        AddBankCashEntry(entry);
        try
        {
            DeleteBankCashEntry(originalMonth, entry.Id);
        }
        catch
        {
            try
            {
                DeleteBankCashEntry(newMonth, entry.Id);
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException)
            {
            }

            throw;
        }

        return entry;
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public void DeleteBankCashEntry(YearMonth month, Guid id)
    {
        var path = GetMonthFilePath(month);
        using var workbook = OpenExisting(path);
        if (!BankCashSheet.GetOrCreate(workbook, month).Delete(id))
            throw EntryNotFound(id, month);
        Save(workbook, path);
    }

    /// <summary>
    /// Writes an opening balance. With <paramref name="isManual"/> true it's
    /// the user's own figure; false marks it as carried forward from last month.
    /// Creates the workbook if needed.
    /// </summary>
    public void SetOpeningBalance(YearMonth month, Account account, decimal value, bool isManual)
    {
        if (Math.Abs(value) >= MaxAmount)
            throw new ArgumentException("Amount is too large.", nameof(value));

        var path = GetMonthFilePath(month);
        using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
        BankCashSheet.GetOrCreate(workbook, month).SetOpening(account, Math.Round(value, 2, MidpointRounding.AwayFromZero), isManual);
        Save(workbook, path);
    }

    // ---- Currency Denominations -----------------------------------------------

    /// <summary>
    /// Reads the month's cash count. A month with no file, or a file from
    /// before this sheet existed, returns an empty count.
    /// </summary>
    public CurrencySheetData LoadCurrencyCount(YearMonth month)
    {
        var path = GetMonthFilePath(month);
        if (!File.Exists(path))
            return CurrencySheetData.None(month);

        using var workbook = Open(path);
        return CurrencySheet.Find(workbook, month)?.Read() ?? CurrencySheetData.None(month, fileExists: true);
    }

    /// <summary>
    /// Saves the month's cash count, creating the workbook if needed.
    /// </summary>
    public void SaveCurrencyCount(YearMonth month, CurrencyCount count)
    {
        if (count.Coins is < 0 || count.Coins >= MaxAmount)
            throw new ArgumentException("Coins must be zero or more.", nameof(count));

        var path = GetMonthFilePath(month);
        using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
        CurrencySheet.GetOrCreate(workbook, month).Write(count with { Coins = Math.Round(count.Coins, 2, MidpointRounding.AwayFromZero) });
        Save(workbook, path);
    }

    /// <summary>
    /// Re-saves a month's workbook so derived parts (formulas, the card's limit
    /// and due date, the copy of card expenses) are brought up to date.
    /// Does nothing for a month with no workbook.
    /// </summary>
    public void RefreshMonthWorkbook(YearMonth month)
    {
        var path = GetMonthFilePath(month);
        if (!File.Exists(path))
            return;

        using var workbook = Open(path);
        Save(workbook, path);
    }

    // ---- Credit Card --------------------------------------------------------

    /// <summary>
    /// Reads the month's Credit Card sheet. A month with no file, or a file
    /// from before this sheet existed, returns no repayments and a carried-forward opening.
    /// </summary>
    public CardSheetData LoadCard(YearMonth month)
    {
        var path = GetMonthFilePath(month);
        if (!File.Exists(path))
            return CardSheetData.Empty(month);

        using var workbook = Open(path);
        var sheet = CreditCardSheet.Find(workbook, month);
        if (sheet is null)
            return CardSheetData.Empty(month, fileExists: true);

        var data = sheet.Read();
        if (sheet.IsModified)
        {
            // New IDs for rows added by hand; see LoadExpenses.
            try
            {
                Save(workbook, path);
            }
            catch (WorkbookLockedException)
            {
            }
        }

        return data;
    }

    public CardRepayment AddCardRepayment(CardRepayment repayment)
    {
        repayment = Normalize(repayment);
        if (repayment.Id == Guid.Empty)
            repayment = repayment with { Id = Guid.NewGuid() };

        var month = YearMonth.Of(repayment.Date);
        var path = GetMonthFilePath(month);
        using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
        CreditCardSheet.GetOrCreate(workbook, month).Insert(repayment);
        Save(workbook, path);
        return repayment;
    }

    /// <exception cref="KeyNotFoundException">The repayment is no longer in the file.</exception>
    public CardRepayment UpdateCardRepayment(YearMonth originalMonth, CardRepayment repayment)
    {
        repayment = Normalize(repayment);
        if (repayment.Id == Guid.Empty)
            throw new ArgumentException("Repayment has no ID.", nameof(repayment));

        var newMonth = YearMonth.Of(repayment.Date);
        if (newMonth == originalMonth)
        {
            var path = GetMonthFilePath(originalMonth);
            using var workbook = OpenExisting(path);
            if (!CreditCardSheet.GetOrCreate(workbook, originalMonth).Update(repayment))
                throw RepaymentNotFound(repayment.Id, originalMonth);
            Save(workbook, path);
            return repayment;
        }

        if (LoadCard(originalMonth).Repayments.All(r => r.Id != repayment.Id))
            throw RepaymentNotFound(repayment.Id, originalMonth);
        AddCardRepayment(repayment);
        try
        {
            DeleteCardRepayment(originalMonth, repayment.Id);
        }
        catch
        {
            try
            {
                DeleteCardRepayment(newMonth, repayment.Id);
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException)
            {
            }

            throw;
        }

        return repayment;
    }

    /// <exception cref="KeyNotFoundException">The repayment is no longer in the file.</exception>
    public void DeleteCardRepayment(YearMonth month, Guid id)
    {
        var path = GetMonthFilePath(month);
        using var workbook = OpenExisting(path);
        if (!CreditCardSheet.GetOrCreate(workbook, month).Delete(id))
            throw RepaymentNotFound(id, month);
        Save(workbook, path);
    }

    /// <summary>
    /// Writes the opening outstanding. <paramref name="isManual"/> false marks
    /// it as carried forward from last month.
    /// </summary>
    public void SetCardOpening(YearMonth month, decimal value, bool isManual)
    {
        if (value < 0 || value >= MaxAmount)
            throw new ArgumentException("The opening amount owed must be zero or more.", nameof(value));

        var path = GetMonthFilePath(month);
        using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
        CreditCardSheet.GetOrCreate(workbook, month).SetOpening(Math.Round(value, 2, MidpointRounding.AwayFromZero), isManual);
        Save(workbook, path);
    }

    private static CardRepayment Normalize(CardRepayment repayment)
    {
        if (repayment.Amount <= 0)
            throw new ArgumentException("Amount must be more than zero.", nameof(repayment));
        if (repayment.Amount >= MaxAmount)
            throw new ArgumentException("Amount is too large.", nameof(repayment));
        if (!Enum.IsDefined(repayment.PaidFrom))
            throw new ArgumentException("Choose Bank or Cash.", nameof(repayment));

        return repayment with
        {
            Amount = Math.Round(repayment.Amount, 2, MidpointRounding.AwayFromZero),
            Note = repayment.Note?.Trim() ?? string.Empty,
        };
    }

    private static KeyNotFoundException RepaymentNotFound(Guid id, YearMonth month) =>
        new($"That repayment is no longer in {month.FileName}. It may have been changed in Excel; reload and try again. (ID {id})");

    private static BankCashEntry Normalize(BankCashEntry entry)
    {
        if (entry.Amount <= 0)
            throw new ArgumentException("Amount must be more than zero.", nameof(entry));
        if (entry.Amount >= MaxAmount)
            throw new ArgumentException("Amount is too large.", nameof(entry));
        if (!Enum.IsDefined(entry.Type))
            throw new ArgumentException("Unknown entry type.", nameof(entry));

        return entry with
        {
            Amount = Math.Round(entry.Amount, 2, MidpointRounding.AwayFromZero),
            Note = entry.Note?.Trim() ?? string.Empty,
        };
    }

    private static KeyNotFoundException EntryNotFound(Guid id, YearMonth month) =>
        new($"That transaction is no longer in {month.FileName}. It may have been changed in Excel; reload and try again. (ID {id})");

    /// <summary>
    /// Changes the family member name on every expense in <paramref name="year"/>'s
    /// workbooks. Files that can't be updated (e.g. open in Excel) are reported
    /// in the result rather than stopping the others.
    /// </summary>
    public RenameResult RenameFamilyMember(int year, string oldName, string newName) =>
        RenameInYear(year, (sheet) => sheet.RenameFamilyMember(oldName, newName));

    /// <summary>
    /// Same as <see cref="RenameFamilyMember"/>, for the Category column.
    /// </summary>
    public RenameResult RenameCategory(int year, string oldName, string newName) =>
        RenameInYear(year, (sheet) => sheet.RenameCategory(oldName, newName));

    private RenameResult RenameInYear(int year, Func<ExpensesSheet, int> rename)
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
                var count = sheet is null ? 0 : rename(sheet);
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

    private void Save(XLWorkbook workbook, string path)
    {
        WriteSchemaVersion(workbook);
        if (YearMonth.TryParseFileName(Path.GetFileName(path), out var month))
            CompleteMonthWorkbook(workbook, month);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Save beside the real file, then swap it in, so a failed save never
        // leaves a half-written workbook behind.
        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $"~{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                // Store the formulas' results too, so programs that don't
                // recalculate on open (LibreOffice by default) show real numbers.
                WriteTo(tempPath, workbook, evaluateFormulas: true);
            }
            catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
            {
                // A formula ClosedXML can't evaluate shouldn't stop the save; Excel recalculates on open anyway.
                TryDelete(tempPath);
                WriteTo(tempPath, workbook, evaluateFormulas: false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new WorkbookLockedException(path, ex);
        }
    }

    private static void WriteTo(string tempPath, XLWorkbook workbook, bool evaluateFormulas)
    {
        // Via a stream because ClosedXML picks the format from the file extension.
        using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        workbook.SaveAs(stream, new SaveOptions { EvaluateFormulasBeforeSaving = evaluateFormulas });
    }

    /// <summary>
    /// Makes sure a monthly workbook has all its sheets, that every formula
    /// points at the other sheets' current rows and columns, and that the
    /// Credit Card sheet shows the card's current limit, due date and expenses.
    /// </summary>
    private void CompleteMonthWorkbook(XLWorkbook workbook, YearMonth month)
    {
        var expenses = ExpensesSheet.GetOrCreate(workbook, month);
        var bank = BankCashSheet.GetOrCreate(workbook, month);
        var currency = CurrencySheet.GetOrCreate(workbook, month);
        var card = CreditCardSheet.GetOrCreate(workbook, month);

        card.SetCardDetails(_cardSettings?.Invoke());
        card.Refresh(expenses, expenses.ReadAll().Expenses);
        bank.RefreshFormulas(expenses, card);
        currency.RefreshFormulas(bank, card, expenses);

        // Excel recalculates everything when the file is opened.
        workbook.FullCalculationOnLoad = true;
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
