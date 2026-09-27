using System.Globalization;
using ClosedXML.Excel;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services.Excel;

/// <summary>
/// Reads and edits the "Bank &amp; Cash" worksheet:
///
///   Opening balances (typed in, or carried forward from last month)
///   A summary whose Excel formulas add up the log below and the Expenses sheet
///   A transaction log (withdrawals, deposits, income) with running balances
///
/// The formulas mean the sheet stays correct if expenses are edited in Excel.
/// The app does its own arithmetic (see BalanceCalculator) and doesn't rely on them.
/// </summary>
internal sealed class BankCashSheet
{
    public const string SheetName = "Bank & Cash";

    private const string OpeningLabel = "Opening balance";
    private const string SourceLabel = "Opening set by";
    private const string ManualSource = "You";
    private const string CarriedSource = "Carried forward";

    // Log columns.
    private const int ColDate = 1, ColType = 2, ColAmount = 3, ColNote = 4, ColBank = 5, ColCash = 6, ColId = 7;

    // How far down the formulas look, in the log and in the Expenses sheet.
    private const int FormulaRows = 5000;

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#0F766E");

    private readonly IXLWorksheet _ws;
    private readonly YearMonth _month;
    private int _openingRow;
    private int _logHeaderRow;

    private BankCashSheet(IXLWorksheet ws, YearMonth month)
    {
        _ws = ws;
        _month = month;
    }

    public bool IsModified { get; private set; }

    private int SourceRow => _openingRow + 1;

    private int FirstLogRow => _logHeaderRow + 1;

    public static BankCashSheet? Find(XLWorkbook workbook, YearMonth month)
    {
        if (!workbook.TryGetWorksheet(SheetName, out var ws))
            return null;

        var sheet = new BankCashSheet(ws, month);
        return sheet.TryLocateSections() ? sheet : null;
    }

    /// <summary>
    /// Returns the sheet, creating it (just after Expenses) if missing or unrecognisable.
    /// An unrecognisable sheet is kept, renamed, rather than overwritten.
    /// </summary>
    public static BankCashSheet GetOrCreate(XLWorkbook workbook, YearMonth month)
    {
        if (workbook.TryGetWorksheet(SheetName, out var existing))
        {
            var sheet = new BankCashSheet(existing, month);
            if (sheet.TryLocateSections())
                return sheet;

            existing.Name = UniqueName(workbook, "Bank & Cash (old)");
        }

        var position = workbook.TryGetWorksheet(ExpensesSheet.SheetName, out var expenses) ? expenses.Position + 1 : 1;
        var created = new BankCashSheet(workbook.AddWorksheet(SheetName, position), month);
        created.BuildLayout();
        return created;
    }

    // ---- Reading ------------------------------------------------------------

    public BankCashSheetData Read()
    {
        var entries = new List<BankCashEntry>();
        var problems = new List<SheetProblem>();
        var seenIds = new HashSet<Guid>();

        foreach (var row in LogRowNumbers())
        {
            if (IsBlankLogRow(row))
                continue;

            if (!TryReadEntry(row, out var entry, out var error))
            {
                problems.Add(new SheetProblem(SheetName, row, error));
                continue;
            }

            if (entry.Id == Guid.Empty || !seenIds.Add(entry.Id))
            {
                entry = entry with { Id = Guid.NewGuid() };
                seenIds.Add(entry.Id);
                _ws.Cell(row, ColId).Value = entry.Id.ToString();
                IsModified = true;
            }

            entries.Add(entry);
        }

        return new BankCashSheetData(
            _month,
            FileExists: true,
            ManualOpeningBank: IsManual(Account.Bank) ? ReadOpening(Account.Bank) ?? 0 : null,
            ManualOpeningCash: IsManual(Account.Cash) ? ReadOpening(Account.Cash) ?? 0 : null,
            entries,
            problems)
        {
            StoredOpeningBank = ReadOpening(Account.Bank),
            StoredOpeningCash = ReadOpening(Account.Cash),
        };
    }

    // ---- Opening balances ---------------------------------------------------

    /// <summary>
    /// Writes an opening balance. <paramref name="isManual"/> false marks it as
    /// carried forward, so the app keeps it in step with last month's closing.
    /// </summary>
    public void SetOpening(Account account, decimal value, bool isManual)
    {
        var col = account == Account.Bank ? 2 : 3;
        var cell = _ws.Cell(_openingRow, col);
        cell.Value = value;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
        _ws.Cell(SourceRow, col).Value = isManual ? ManualSource : CarriedSource;
        IsModified = true;
    }

    private decimal? ReadOpening(Account account) =>
        ExpensesSheet.TryReadAmount(_ws.Cell(_openingRow, account == Account.Bank ? 2 : 3), out var value) ? value : null;

    private bool IsManual(Account account)
    {
        var text = _ws.Cell(SourceRow, account == Account.Bank ? 2 : 3).GetString().Trim().ToLowerInvariant();
        return text is "you" or "manual" or "me" or "set by you";
    }

    // ---- Log edits ----------------------------------------------------------

    public void Insert(BankCashEntry entry)
    {
        CheckMonth(entry);

        var row = FindInsertRow(entry.Date);
        if (row <= LastLogRow())
        {
            _ws.Row(row).InsertRowsAbove(1);
            _ws.Range(row, 1, row, ColId).Style = _ws.Style;
        }

        WriteEntry(row, entry);
        IsModified = true;
    }

    public bool Update(BankCashEntry entry)
    {
        CheckMonth(entry);

        var row = FindRow(entry.Id);
        if (row is null)
            return false;

        if (ExpensesSheet.TryReadDate(_ws.Cell(row.Value, ColDate), out var oldDate) && oldDate == entry.Date)
        {
            WriteEntry(row.Value, entry);
            IsModified = true;
        }
        else
        {
            _ws.Row(row.Value).Delete();
            Insert(entry);
        }

        return true;
    }

    public bool Delete(Guid id)
    {
        var row = FindRow(id);
        if (row is null)
            return false;

        _ws.Row(row.Value).Delete();
        IsModified = true;
        return true;
    }

    // ---- Formulas -----------------------------------------------------------

    /// <summary>
    /// (Re)writes every formula so it points at the current log rows and the
    /// Expenses sheet's current columns. Called before every save.
    /// </summary>
    public void RefreshFormulas(ExpensesSheet expenses)
    {
        var log = FirstLogRow;
        var logEnd = log + FormulaRows;
        string Log(int col) => $"${Letter(col)}${log}:${Letter(col)}${logEnd}";
        string LogSum(BankCashEntryType type, string? dateCriteria = null) =>
            $"SUMIFS({Log(ColAmount)},{Log(ColType)},\"{type.ToDisplayName()}\"{(dateCriteria is null ? "" : $",{Log(ColDate)},{dateCriteria}")})";

        var exp = $"'{ExpensesSheet.SheetName}'!";
        string Exp(int col) => $"{exp}${Letter(col)}$2:${Letter(col)}${FormulaRows * 4}";
        string ExpSum(string method, string? dateCriteria = null) =>
            $"SUMIFS({Exp(expenses.AmountColumn)},{Exp(expenses.PaymentColumn)},\"{method}\"{(dateCriteria is null ? "" : $",{Exp(expenses.DateColumn)},{dateCriteria}")})";

        var o = _openingRow;
        var income = o + 2;
        var withdrawals = o + 3;
        var deposits = o + 4;
        var spent = o + 5;
        var closing = o + 6;

        SetFormula(income, 2, LogSum(BankCashEntryType.BankIncome));
        SetFormula(income, 3, LogSum(BankCashEntryType.CashIncome));
        SetFormula(withdrawals, 2, "-" + LogSum(BankCashEntryType.Withdrawal));
        SetFormula(withdrawals, 3, LogSum(BankCashEntryType.Withdrawal));
        SetFormula(deposits, 2, LogSum(BankCashEntryType.Deposit));
        SetFormula(deposits, 3, "-" + LogSum(BankCashEntryType.Deposit));
        SetFormula(spent, 2, "-" + ExpSum("Bank"));
        SetFormula(spent, 3, "-" + ExpSum("Cash"));
        SetFormula(closing, 2, $"B{o}+SUM(B{income}:B{spent})");
        SetFormula(closing, 3, $"C{o}+SUM(C{income}:C{spent})");

        // Running balances at the end of each logged day, expenses included.
        foreach (var row in LogRowNumbers())
        {
            if (IsBlankLogRow(row))
            {
                _ws.Cell(row, ColBank).Clear(XLClearOptions.Contents);
                _ws.Cell(row, ColCash).Clear(XLClearOptions.Contents);
                continue;
            }

            var upTo = $"\"<=\"&A{row}";
            SetFormula(row, ColBank,
                $"$B${o}+{LogSum(BankCashEntryType.BankIncome, upTo)}+{LogSum(BankCashEntryType.Deposit, upTo)}" +
                $"-{LogSum(BankCashEntryType.Withdrawal, upTo)}-{ExpSum("Bank", upTo)}");
            SetFormula(row, ColCash,
                $"$C${o}+{LogSum(BankCashEntryType.CashIncome, upTo)}+{LogSum(BankCashEntryType.Withdrawal, upTo)}" +
                $"-{LogSum(BankCashEntryType.Deposit, upTo)}-{ExpSum("Cash", upTo)}");
        }
    }

    private void SetFormula(int row, int col, string formula)
    {
        var cell = _ws.Cell(row, col);
        cell.FormulaA1 = formula;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
    }

    private static string Letter(int col) => XLHelper.GetColumnLetterFromNumber(col);

    // ---- Layout -------------------------------------------------------------

    private bool TryLocateSections()
    {
        _openingRow = 0;
        _logHeaderRow = 0;
        var last = _ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0;

        for (var row = 1; row <= Math.Min(last, 60); row++)
        {
            var label = _ws.Cell(row, 1).GetString().Trim();
            if (_openingRow == 0 && label.Equals(OpeningLabel, StringComparison.OrdinalIgnoreCase)
                && _ws.Cell(row + 1, 1).GetString().Trim().Equals(SourceLabel, StringComparison.OrdinalIgnoreCase))
            {
                _openingRow = row;
            }
            else if (_openingRow != 0 && label.Equals("Date", StringComparison.OrdinalIgnoreCase)
                     && _ws.Cell(row, ColType).GetString().Trim().Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                _logHeaderRow = row;
                break;
            }
        }

        return _openingRow != 0 && _logHeaderRow != 0;
    }

    private void BuildLayout()
    {
        _ws.Cell(1, 1).Value = "Bank & Cash";
        _ws.Cell(1, 1).Style.Font.FontSize = 16;
        _ws.Cell(1, 1).Style.Font.Bold = true;
        _ws.Cell(2, 1).Value = "Balances add up the transactions below and the Expenses sheet (Cash and Bank payments). " +
                               "Opening balances carry forward from last month unless you set them.";
        _ws.Cell(2, 1).Style.Font.Italic = true;
        _ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        _openingRow = 5;
        StyleHeader(_ws.Range(4, 1, 4, 3));
        _ws.Cell(4, 2).Value = "Bank";
        _ws.Cell(4, 3).Value = "Cash in hand";

        string[] labels = { OpeningLabel, SourceLabel, "+ Income", "± Withdrawals", "± Deposits", "− Expenses paid", "Closing balance" };
        for (var i = 0; i < labels.Length; i++)
            _ws.Cell(_openingRow + i, 1).Value = labels[i];

        SetOpening(Account.Bank, 0, isManual: false);
        SetOpening(Account.Cash, 0, isManual: false);
        _ws.Range(SourceRow, 2, SourceRow, 3).Style.Font.Italic = true;
        var closingRow = _openingRow + 6;
        _ws.Range(closingRow, 1, closingRow, 3).Style.Font.Bold = true;
        _ws.Range(closingRow, 1, closingRow, 3).Style.Border.TopBorder = XLBorderStyleValues.Thin;

        var titleRow = closingRow + 2;
        _ws.Cell(titleRow, 1).Value = "Transactions";
        _ws.Cell(titleRow, 1).Style.Font.Bold = true;
        _ws.Cell(titleRow, 1).Style.Font.FontSize = 13;

        _logHeaderRow = titleRow + 1;
        string[] headers = { "Date", "Type", "Amount", "Note", "Bank balance (end of day)", "Cash in hand (end of day)", "ID" };
        for (var i = 0; i < headers.Length; i++)
            _ws.Cell(_logHeaderRow, i + 1).Value = headers[i];
        StyleHeader(_ws.Range(_logHeaderRow, 1, _logHeaderRow, headers.Length));

        double[] widths = { 22, 18, 16, 36, 24, 24, 38 };
        for (var i = 0; i < widths.Length; i++)
            _ws.Column(i + 1).Width = widths[i];
        _ws.Column(ColId).Hide();
        _ws.SheetView.FreezeRows(_logHeaderRow);

        var validation = _ws.Range(FirstLogRow, ColType, FirstLogRow + FormulaRows, ColType).CreateDataValidation();
        validation.List("\"" + string.Join(",", BankCashEntryTypes.All.Select(t => t.ToDisplayName())) + "\"", true);
        validation.ErrorTitle = "Type";
        validation.ErrorMessage = "Choose Withdrawal, Deposit, Income to bank or Income in cash.";

        IsModified = true;
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = HeaderFill;
    }

    private static string UniqueName(XLWorkbook workbook, string name)
    {
        var candidate = name;
        for (var i = 2; workbook.Worksheets.Contains(candidate); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    // ---- Rows ---------------------------------------------------------------

    private int LastLogRow() =>
        Math.Max(_ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? _logHeaderRow, _logHeaderRow);

    private IEnumerable<int> LogRowNumbers()
    {
        var last = LastLogRow();
        for (var row = FirstLogRow; row <= last; row++)
            yield return row;
    }

    // The balance columns hold formulas, so they don't count towards a row being filled in.
    private bool IsBlankLogRow(int row) =>
        new[] { ColDate, ColType, ColAmount, ColNote }.All(c => _ws.Cell(row, c).Value.IsBlank);

    private int? FindRow(Guid id)
    {
        var text = id.ToString();
        foreach (var row in LogRowNumbers())
        {
            if (string.Equals(_ws.Cell(row, ColId).GetString().Trim(), text, StringComparison.OrdinalIgnoreCase))
                return row;
        }

        return null;
    }

    private int FindInsertRow(DateOnly date)
    {
        foreach (var row in LogRowNumbers())
        {
            if (ExpensesSheet.TryReadDate(_ws.Cell(row, ColDate), out var rowDate) && rowDate > date)
                return row;
        }

        return LastLogRow() + 1;
    }

    private void WriteEntry(int row, BankCashEntry e)
    {
        var date = _ws.Cell(row, ColDate);
        date.Value = e.Date.ToDateTime(TimeOnly.MinValue);
        date.Style.DateFormat.Format = ExpensesSheet.DateFormat;

        _ws.Cell(row, ColType).Value = e.Type.ToDisplayName();

        var amount = _ws.Cell(row, ColAmount);
        amount.Value = e.Amount;
        amount.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;

        _ws.Cell(row, ColNote).Value = e.Note;
        _ws.Cell(row, ColId).Value = e.Id.ToString();
    }

    private bool TryReadEntry(int row, out BankCashEntry entry, out string error)
    {
        entry = null!;

        if (!ExpensesSheet.TryReadDate(_ws.Cell(row, ColDate), out var date))
        {
            error = $"\"{_ws.Cell(row, ColDate).GetFormattedString()}\" isn't a date.";
            return false;
        }

        if (!_month.Contains(date))
        {
            error = $"{date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} belongs in a different month's file.";
            return false;
        }

        var typeText = _ws.Cell(row, ColType).GetString().Trim();
        if (!BankCashEntryTypes.TryParse(typeText, out var type))
        {
            error = typeText.Length == 0
                ? "Type is missing."
                : $"\"{typeText}\" isn't a type (use Withdrawal, Deposit, Income to bank or Income in cash).";
            return false;
        }

        if (!ExpensesSheet.TryReadAmount(_ws.Cell(row, ColAmount), out var amount) || amount <= 0)
        {
            error = $"\"{_ws.Cell(row, ColAmount).GetFormattedString()}\" isn't an amount more than zero.";
            return false;
        }

        Guid.TryParse(_ws.Cell(row, ColId).GetString().Trim(), out var id);
        entry = new BankCashEntry
        {
            Id = id,
            Date = date,
            Type = type,
            Amount = amount,
            Note = _ws.Cell(row, ColNote).GetString().Trim(),
        };
        error = string.Empty;
        return true;
    }

    private void CheckMonth(BankCashEntry entry)
    {
        if (!_month.Contains(entry.Date))
            throw new ArgumentException(
                $"{entry.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} is not in {_month.DisplayName}.", nameof(entry));
    }
}
