using ClosedXML.Excel;
using HisaabKitaab.Models;
using HisaabKitaab.Services.Excel;

namespace HisaabKitaab.Services;

/// <summary>
/// All reading and writing of the monthly Excel workbooks. Nothing else in the
/// app touches ClosedXML.
///
/// Files live at <c>{DataFolder}/{year}/{Mon}_{year}.xlsx</c>, e.g.
/// <c>Documents/Hisaab Kitaab/2026/Sept_2026.xlsx</c>. Each zakat year has
/// its own workbook beside them, <c>{DataFolder}/{year}/Zakat_{year}.xlsx</c>.
///
/// Every operation opens the file, changes only the rows it needs to and saves,
/// so edits made directly in Excel between operations are preserved.
/// </summary>
public class ExcelService
{
    /// <summary>
    /// Bump when the workbook layout changes in a way older versions can't read.
    /// 2: Bank &amp; Cash gained the "− Zakat given" row (older versions would
    /// write their closing-balance formulas over it) and zakat workbooks exist.
    /// </summary>
    public const int SchemaVersion = 2;

    public const decimal MaxAmount = 1_000_000_000_000m;

    private const string SchemaPropertyName = "HisaabKitaab.SchemaVersion";

    private readonly Func<CardSettings>? _cardSettings;
    private readonly Func<IReadOnlyList<ResolvedBudget>>? _budgets;

    // Every operation opens a workbook, changes it and saves it. Two running at
    // once on the same file (e.g. a background sync and the user adding an
    // expense) would each save their own copy and one change would be lost, so
    // they take turns. Reentrant: operations that call others are fine.
    private readonly object _gate = new();

    /// <summary>
    /// Raised after a monthly workbook has been saved, on the thread that saved it.
    /// Later months' carried-forward figures may need updating after this.
    /// </summary>
    public event Action<YearMonth>? MonthSaved;

    /// <param name="cardSettings">
    /// Supplies the credit card's limit and due day for the Credit Card sheet.
    /// Read at each save, so changes in Settings show up the next time a month is saved.
    /// </param>
    /// <param name="budgets">Supplies the monthly budgets for the Summary sheet.</param>
    public ExcelService(string dataFolder, Func<CardSettings>? cardSettings = null,
        Func<IReadOnlyList<ResolvedBudget>>? budgets = null)
    {
        DataFolder = dataFolder;
        _cardSettings = cardSettings;
        _budgets = budgets;
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
        lock (_gate)
        {
            var path = GetMonthFilePath(month);
            if (File.Exists(path))
                return false;

            using var workbook = CreateWorkbook(month);
            Save(workbook, path);
            return true;
        }
    }

    /// <summary>
    /// Reads the month's expenses. A month with no file yet returns an empty list.
    /// Rows that couldn't be read are listed in <see cref="ExpenseSheetData.Problems"/>
    /// and left as they are in the file.
    /// </summary>
    public ExpenseSheetData LoadExpenses(YearMonth month)
    {
        lock (_gate)
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
    }

    /// <summary>
    /// Adds an expense to the workbook for its date's month, creating the
    /// workbook if needed. Returns the saved expense (with its ID and cleaned-up text).
    /// </summary>
    public Expense AddExpense(Expense expense)
    {
        lock (_gate)
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
    }

    /// <summary>
    /// Replaces an existing expense. If its date moved to another month, it is
    /// moved to that month's workbook.
    /// </summary>
    /// <param name="originalMonth">The month the expense was loaded from.</param>
    /// <exception cref="KeyNotFoundException">The expense is no longer in the file.</exception>
    public Expense UpdateExpense(YearMonth originalMonth, Expense expense)
    {
        lock (_gate)
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
    }

    /// <exception cref="KeyNotFoundException">The expense is no longer in the file.</exception>
    public void DeleteExpense(YearMonth month, Guid id)
    {
        lock (_gate)
        {
            var path = GetMonthFilePath(month);
            using var workbook = OpenExisting(path);
            if (!ExpensesSheet.GetOrCreate(workbook, month).Delete(id))
                throw NotFound(id, month);
            Save(workbook, path);
        }
    }

    // ---- Bank & Cash --------------------------------------------------------

    /// <summary>
    /// Reads the month's Bank &amp; Cash sheet. A month with no file (or a file
    /// from before this sheet existed) returns an empty log with carried-forward openings.
    /// </summary>
    public BankCashSheetData LoadBankCash(YearMonth month)
    {
        lock (_gate)
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
    }

    public BankCashEntry AddBankCashEntry(BankCashEntry entry)
    {
        lock (_gate)
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
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public BankCashEntry UpdateBankCashEntry(YearMonth originalMonth, BankCashEntry entry)
    {
        lock (_gate)
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
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public void DeleteBankCashEntry(YearMonth month, Guid id)
    {
        lock (_gate)
        {
            var path = GetMonthFilePath(month);
            using var workbook = OpenExisting(path);
            if (!BankCashSheet.GetOrCreate(workbook, month).Delete(id))
                throw EntryNotFound(id, month);
            Save(workbook, path);
        }
    }

    /// <summary>
    /// Writes an opening balance. With <paramref name="isManual"/> true it's
    /// the user's own figure; false marks it as carried forward from last month.
    /// Creates the workbook if needed.
    /// </summary>
    public void SetOpeningBalance(YearMonth month, Account account, decimal value, bool isManual)
    {
        lock (_gate)
        {
            if (Math.Abs(value) >= MaxAmount)
                throw new ArgumentException("Amount is too large.", nameof(value));

            var path = GetMonthFilePath(month);
            using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
            BankCashSheet.GetOrCreate(workbook, month).SetOpening(account, Math.Round(value, 2, MidpointRounding.AwayFromZero), isManual);
            Save(workbook, path);
        }
    }

    // ---- Currency Denominations -----------------------------------------------

    /// <summary>
    /// Reads the month's cash count. A month with no file, or a file from
    /// before this sheet existed, returns an empty count.
    /// </summary>
    public CurrencySheetData LoadCurrencyCount(YearMonth month)
    {
        lock (_gate)
        {
            var path = GetMonthFilePath(month);
            if (!File.Exists(path))
                return CurrencySheetData.None(month);

            using var workbook = Open(path);
            return CurrencySheet.Find(workbook, month)?.Read() ?? CurrencySheetData.None(month, fileExists: true);
        }
    }

    /// <summary>
    /// Saves the month's cash count, creating the workbook if needed.
    /// </summary>
    public void SaveCurrencyCount(YearMonth month, CurrencyCount count)
    {
        lock (_gate)
        {
            if (count.Coins is < 0 || count.Coins >= MaxAmount)
                throw new ArgumentException("Coins must be zero or more.", nameof(count));

            var path = GetMonthFilePath(month);
            using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
            CurrencySheet.GetOrCreate(workbook, month).Write(count with { Coins = Math.Round(count.Coins, 2, MidpointRounding.AwayFromZero) });
            Save(workbook, path);
        }
    }

    /// <summary>
    /// Re-saves a month's workbook so derived parts (formulas, the card's limit
    /// and due date, the copies of card expenses and zakat given) are brought
    /// up to date. A month with no workbook is left alone unless
    /// <paramref name="createIfMissing"/>.
    /// </summary>
    public void RefreshMonthWorkbook(YearMonth month, bool createIfMissing = false)
    {
        lock (_gate)
        {
            var path = GetMonthFilePath(month);
            if (!File.Exists(path) && !createIfMissing)
                return;

            using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
            Save(workbook, path);
        }
    }

    // ---- Credit Card --------------------------------------------------------

    /// <summary>
    /// Reads the month's Credit Card sheet. A month with no file, or a file
    /// from before this sheet existed, returns no repayments and a carried-forward opening.
    /// </summary>
    public CardSheetData LoadCard(YearMonth month)
    {
        lock (_gate)
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
    }

    public CardRepayment AddCardRepayment(CardRepayment repayment)
    {
        lock (_gate)
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
    }

    /// <exception cref="KeyNotFoundException">The repayment is no longer in the file.</exception>
    public CardRepayment UpdateCardRepayment(YearMonth originalMonth, CardRepayment repayment)
    {
        lock (_gate)
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
    }

    /// <exception cref="KeyNotFoundException">The repayment is no longer in the file.</exception>
    public void DeleteCardRepayment(YearMonth month, Guid id)
    {
        lock (_gate)
        {
            var path = GetMonthFilePath(month);
            using var workbook = OpenExisting(path);
            if (!CreditCardSheet.GetOrCreate(workbook, month).Delete(id))
                throw RepaymentNotFound(id, month);
            Save(workbook, path);
        }
    }

    /// <summary>
    /// Writes the opening outstanding. <paramref name="isManual"/> false marks
    /// it as carried forward from last month.
    /// </summary>
    public void SetCardOpening(YearMonth month, decimal value, bool isManual)
    {
        lock (_gate)
        {
            if (value < 0 || value >= MaxAmount)
                throw new ArgumentException("The opening amount owed must be zero or more.", nameof(value));

            var path = GetMonthFilePath(month);
            using var workbook = File.Exists(path) ? Open(path) : CreateWorkbook(month);
            CreditCardSheet.GetOrCreate(workbook, month).SetOpening(Math.Round(value, 2, MidpointRounding.AwayFromZero), isManual);
            Save(workbook, path);
        }
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

    // ---- Zakat (one workbook per year) ------------------------------------------

    /// <summary>
    /// Raised after a zakat workbook has been saved, on the thread that saved it.
    /// </summary>
    public event Action<int>? ZakatSaved;

    /// <summary>
    /// <c>{DataFolder}/{year}/Zakat_{year}.xlsx</c>.
    /// </summary>
    public string GetZakatFilePath(int year) => Path.Combine(GetYearFolder(year), $"Zakat_{year:D4}.xlsx");

    public bool ZakatFileExists(int year) => File.Exists(GetZakatFilePath(year));

    public static bool TryParseZakatFileName(string fileName, out int year)
    {
        year = 0;
        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase)
               && name.StartsWith("Zakat_", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(name["Zakat_".Length..], out year)
               && year is >= 1900 and <= 9999;
    }

    /// <summary>
    /// Years that have a zakat workbook on disk, oldest first.
    /// </summary>
    public IReadOnlyList<int> GetExistingZakatYears()
    {
        if (!Directory.Exists(DataFolder))
            return Array.Empty<int>();

        var years = new List<int>();
        foreach (var yearDir in Directory.EnumerateDirectories(DataFolder))
        {
            if (int.TryParse(Path.GetFileName(yearDir), out var year) && year is >= 1900 and <= 9999
                && File.Exists(GetZakatFilePath(year)))
            {
                years.Add(year);
            }
        }

        years.Sort();
        return years;
    }

    /// <summary>
    /// Reads a year's zakat workbook. A year with no file returns no entries and no period.
    /// </summary>
    public ZakatYearData LoadZakat(int year)
    {
        lock (_gate)
        {
            var path = GetZakatFilePath(year);
            if (!File.Exists(path))
                return ZakatYearData.Empty(year);

            using var workbook = Open(path);
            var sheet = ZakatSheet.Find(workbook, year);
            if (sheet is null)
                return ZakatYearData.Empty(year, fileExists: true);

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
    }

    /// <summary>
    /// Sets the dates a zakat year runs over, creating its workbook if needed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The dates aren't a valid zakat year for <paramref name="year"/>, or
    /// entries already logged would fall outside them.
    /// </exception>
    public void SetZakatPeriod(int year, ZakatPeriod period)
    {
        lock (_gate)
        {
            if (period.Start.Year != year)
                throw new ArgumentException($"The {year} zakat year must start in {year}.", nameof(period));
            if (period.End < period.Start)
                throw new ArgumentException("The zakat year must end on or after the day it starts.", nameof(period));
            if (period.End > ZakatPeriod.LatestEnd(period.Start))
                throw new ArgumentException("A zakat year can't be longer than a year.", nameof(period));

            var path = GetZakatFilePath(year);
            using var workbook = File.Exists(path) ? Open(path) : CreateZakatWorkbook(year, period);
            var sheet = ZakatSheet.GetOrCreate(workbook, year, period);
            var outside = sheet.Read().Entries.Count(e => !period.Contains(e.Date));
            if (outside > 0)
                throw new ArgumentException(
                    $"{outside} {(outside == 1 ? "entry is" : "entries are")} dated outside {period}. Change or delete {(outside == 1 ? "it" : "them")} first.",
                    nameof(period));

            sheet.SetPeriod(period);
            Save(workbook, path);
        }
    }

    /// <summary>
    /// Adds an entry to a year's zakat workbook, creating the workbook with
    /// <paramref name="periodIfNew"/> if needed.
    /// </summary>
    public ZakatEntry AddZakatEntry(int year, ZakatEntry entry, ZakatPeriod periodIfNew)
    {
        lock (_gate)
        {
            entry = Normalize(entry);
            if (entry.Id == Guid.Empty)
                entry = entry with { Id = Guid.NewGuid() };

            var path = GetZakatFilePath(year);
            using var workbook = File.Exists(path) ? Open(path) : CreateZakatWorkbook(year, periodIfNew);
            ZakatSheet.GetOrCreate(workbook, year, periodIfNew).Insert(entry);
            Save(workbook, path);
            return entry;
        }
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public ZakatEntry UpdateZakatEntry(int year, ZakatEntry entry)
    {
        lock (_gate)
        {
            entry = Normalize(entry);
            if (entry.Id == Guid.Empty)
                throw new ArgumentException("Entry has no ID.", nameof(entry));

            var path = GetZakatFilePath(year);
            using var workbook = OpenExisting(path);
            if (!ZakatSheet.GetOrCreate(workbook, year, ZakatPeriod.CalendarYear(year)).Update(entry))
                throw ZakatEntryNotFound(entry.Id, year);
            Save(workbook, path);
            return entry;
        }
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public void DeleteZakatEntry(int year, Guid id)
    {
        lock (_gate)
        {
            var path = GetZakatFilePath(year);
            using var workbook = OpenExisting(path);
            if (!ZakatSheet.GetOrCreate(workbook, year, ZakatPeriod.CalendarYear(year)).Delete(id))
                throw ZakatEntryNotFound(id, year);
            Save(workbook, path);
        }
    }

    /// <summary>
    /// Writes the amount carried from last year. <paramref name="isManual"/>
    /// false marks it as carried forward. Creates the workbook with
    /// <paramref name="periodIfNew"/> if needed.
    /// </summary>
    public void SetZakatCarried(int year, decimal value, bool isManual, ZakatPeriod periodIfNew)
    {
        lock (_gate)
        {
            if (Math.Abs(value) >= MaxAmount)
                throw new ArgumentException("Amount is too large.", nameof(value));

            var path = GetZakatFilePath(year);
            using var workbook = File.Exists(path) ? Open(path) : CreateZakatWorkbook(year, periodIfNew);
            ZakatSheet.GetOrCreate(workbook, year, periodIfNew)
                .SetCarried(Math.Round(value, 2, MidpointRounding.AwayFromZero), isManual);
            Save(workbook, path);
        }
    }

    private static ZakatEntry Normalize(ZakatEntry entry)
    {
        if (entry.Amount <= 0)
            throw new ArgumentException("Amount must be more than zero.", nameof(entry));
        if (entry.Amount >= MaxAmount)
            throw new ArgumentException("Amount is too large.", nameof(entry));
        if (!Enum.IsDefined(entry.Type))
            throw new ArgumentException("Choose Set aside or Given.", nameof(entry));

        var given = entry.Type == ZakatEntryType.Given;
        if (given && (entry.PaidFrom is not { } from || !Enum.IsDefined(from)))
            throw new ArgumentException("Choose whether it was given from the bank or in cash.", nameof(entry));

        return entry with
        {
            Amount = Math.Round(entry.Amount, 2, MidpointRounding.AwayFromZero),
            PaidFrom = given ? entry.PaidFrom : null,
            Recipient = given ? entry.Recipient?.Trim() ?? string.Empty : string.Empty,
            Note = entry.Note?.Trim() ?? string.Empty,
        };
    }

    private static KeyNotFoundException ZakatEntryNotFound(Guid id, int year) =>
        new($"That entry is no longer in Zakat_{year}.xlsx. It may have been changed in Excel; reload and try again. (ID {id})");

    /// <summary>
    /// Zakat given during <paramref name="month"/>, from whichever zakat years
    /// cover it, for the copy on its Bank &amp; Cash sheet. Null if a zakat
    /// workbook couldn't be read (the copy is then left as it was).
    /// </summary>
    private IReadOnlyList<ZakatEntry>? ZakatGivenIn(YearMonth month)
    {
        var given = new List<ZakatEntry>();
        foreach (var year in new[] { month.Year - 1, month.Year })
        {
            var path = GetZakatFilePath(year);
            if (!File.Exists(path))
                continue;

            try
            {
                using var workbook = Open(path);
                if (ZakatSheet.Find(workbook, year) is { } sheet)
                    given.AddRange(sheet.Read().Entries.Where(e => e.Type == ZakatEntryType.Given && month.Contains(e.Date)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return given;
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
        lock (_gate)
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

    private static XLWorkbook CreateZakatWorkbook(int year, ZakatPeriod period)
    {
        var workbook = new XLWorkbook();
        workbook.Properties.Title = $"Hisaab Kitaab - Zakat {year}";
        workbook.Properties.Author = "Hisaab Kitaab";
        ZakatSheet.GetOrCreate(workbook, year, period);
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
        var isZakat = TryParseZakatFileName(Path.GetFileName(path), out var zakatYear);
        if (isZakat)
            CompleteZakatWorkbook(workbook, zakatYear);
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

        WorkbookSaved?.Invoke(path);
        if (month != default)
            MonthSaved?.Invoke(month);
        if (isZakat)
            ZakatSaved?.Invoke(zakatYear);
    }

    /// <summary>
    /// Raised after any workbook (monthly or zakat) has been written, with its
    /// path, on the saving thread and still holding the lock, so a backup can
    /// copy exactly what was saved.
    /// </summary>
    public event Action<string>? WorkbookSaved;

    /// <summary>
    /// Replaces <paramref name="fileName"/> in <paramref name="year"/>'s folder
    /// with a backup copy. The backup must open as a workbook first. Raised
    /// events are the same as for a save, so later months' figures catch up.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The backup no longer exists.</exception>
    /// <exception cref="InvalidDataException">The backup isn't a readable workbook.</exception>
    public void RestoreWorkbook(int year, string fileName, string backupPath)
    {
        lock (_gate)
        {
            var isMonth = YearMonth.TryParseFileName(fileName, out var month) && month.Year == year;
            var isZakat = TryParseZakatFileName(fileName, out var zakatYear) && zakatYear == year;
            if (!isMonth && !isZakat)
                throw new ArgumentException($"{fileName} isn't one of Hisaab Kitaab's workbooks.", nameof(fileName));
            if (!File.Exists(backupPath))
                throw new KeyNotFoundException($"That backup of {fileName} no longer exists.");

            using (Open(backupPath))
            {
                // Just checking it opens (and isn't from a newer version).
            }

            var path = Path.Combine(GetYearFolder(year), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $"~{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(backupPath, tempPath);

                // A fresh timestamp, so cached copies of the old file are never mistaken for it.
                File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(tempPath);
                throw new WorkbookLockedException(path, ex);
            }

            WorkbookSaved?.Invoke(path);
            if (isMonth)
                MonthSaved?.Invoke(month);
            if (isZakat)
                ZakatSaved?.Invoke(year);
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

        var monthExpenses = expenses.ReadAll().Expenses;

        card.SetCardDetails(_cardSettings?.Invoke());
        card.Refresh(expenses, monthExpenses);
        bank.RefreshFormulas(expenses, card, ZakatGivenIn(month));
        currency.RefreshFormulas(bank, card, expenses);
        SummarySheet.Rebuild(workbook, month, expenses, bank, card, monthExpenses,
            _budgets?.Invoke() ?? Array.Empty<ResolvedBudget>());

        // Excel recalculates everything when the file is opened.
        workbook.FullCalculationOnLoad = true;
    }

    /// <summary>
    /// Makes sure a zakat workbook has its sheet and up-to-date formulas.
    /// </summary>
    private static void CompleteZakatWorkbook(XLWorkbook workbook, int year)
    {
        ZakatSheet.GetOrCreate(workbook, year, ZakatPeriod.CalendarYear(year)).RefreshFormulas();
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
