using System.Globalization;
using ClosedXML.Excel;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services.Excel;

/// <summary>
/// Reads and edits the "Credit Card" worksheet:
///
///   A summary (opening outstanding, card spending from the Expenses sheet,
///   repayments, outstanding, credit limit, % used, due date) in formulas
///   A repayment log with the outstanding amount at the end of each day
///   To the right, a copy of the month's card expenses, rewritten on every
///   save from the Expenses sheet (for reading only; they're edited there)
/// </summary>
internal sealed class CreditCardSheet
{
    public const string SheetName = "Credit Card";

    private const string OpeningLabel = "Opening outstanding";
    private const string SourceLabel = "Opening set by";
    private const string ManualSource = "You";
    private const string CarriedSource = "Carried forward";

    // Repayment log columns.
    private const int ColDate = 1, ColAmount = 2, ColFrom = 3, ColNote = 4, ColOwed = 5, ColId = 6;

    // Copy of card expenses, to the right of the log.
    private const int MirrorFirstCol = 8, MirrorLastCol = 12;

    private const int FormulaRows = 5000;

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#0F766E");

    private readonly IXLWorksheet _ws;
    private readonly YearMonth _month;
    private int _openingRow;
    private int _logHeaderRow;

    private CreditCardSheet(IXLWorksheet ws, YearMonth month)
    {
        _ws = ws;
        _month = month;
    }

    public bool IsModified { get; private set; }

    private int SourceRow => _openingRow + 1;
    private int SpentRow => _openingRow + 2;
    private int RepaidRow => _openingRow + 3;
    private int OwedRow => _openingRow + 4;
    private int LimitRow => _openingRow + 6;
    private int UsedRow => _openingRow + 7;
    private int AvailableRow => _openingRow + 8;
    private int DueRow => _openingRow + 9;
    private int FirstLogRow => _logHeaderRow + 1;

    public static CreditCardSheet? Find(XLWorkbook workbook, YearMonth month)
    {
        if (!workbook.TryGetWorksheet(SheetName, out var ws))
            return null;

        var sheet = new CreditCardSheet(ws, month);
        return sheet.TryLocate() ? sheet : null;
    }

    /// <summary>
    /// Returns the sheet, creating it (after Currency Denominations) if missing.
    /// A sheet with that name laid out differently is kept under another name.
    /// </summary>
    public static CreditCardSheet GetOrCreate(XLWorkbook workbook, YearMonth month)
    {
        if (workbook.TryGetWorksheet(SheetName, out var existing))
        {
            var sheet = new CreditCardSheet(existing, month);
            if (sheet.TryLocate())
                return sheet;

            existing.Name = UniqueName(workbook, "Credit Card (old)");
        }

        var after = new[] { CurrencySheet.SheetName, BankCashSheet.SheetName, ExpensesSheet.SheetName }
            .Select(name => workbook.TryGetWorksheet(name, out var ws) ? ws.Position : 0)
            .Max();
        var created = new CreditCardSheet(workbook.AddWorksheet(SheetName, after + 1), month);
        created.BuildLayout();
        return created;
    }

    // ---- Reading ------------------------------------------------------------

    public CardSheetData Read()
    {
        var repayments = new List<CardRepayment>();
        var problems = new List<SheetProblem>();
        var seenIds = new HashSet<Guid>();

        foreach (var row in LogRowNumbers())
        {
            if (IsBlankLogRow(row))
                continue;

            if (!TryReadRepayment(row, out var repayment, out var error))
            {
                problems.Add(new SheetProblem(SheetName, row, error));
                continue;
            }

            if (repayment.Id == Guid.Empty || !seenIds.Add(repayment.Id))
            {
                repayment = repayment with { Id = Guid.NewGuid() };
                seenIds.Add(repayment.Id);
                _ws.Cell(row, ColId).Value = repayment.Id.ToString();
                IsModified = true;
            }

            repayments.Add(repayment);
        }

        var stored = ExpensesSheet.TryReadAmount(_ws.Cell(_openingRow, 2), out var opening) ? opening : (decimal?)null;
        var manual = _ws.Cell(SourceRow, 2).GetString().Trim().ToLowerInvariant() is "you" or "manual" or "me";

        return new CardSheetData(_month, true, manual ? stored ?? 0 : null, repayments, problems)
        {
            StoredOpening = stored,
        };
    }

    // ---- Writing ------------------------------------------------------------

    public void SetOpening(decimal value, bool isManual)
    {
        var cell = _ws.Cell(_openingRow, 2);
        cell.Value = value;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
        _ws.Cell(SourceRow, 2).Value = isManual ? ManualSource : CarriedSource;
        IsModified = true;
    }

    /// <summary>
    /// Writes the card's limit and this month's due date, which come from settings.
    /// </summary>
    public void SetCardDetails(CardSettings? card)
    {
        var limit = _ws.Cell(LimitRow, 2);
        if (card?.Limit is { } l)
        {
            limit.Value = l;
            limit.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
        }
        else
        {
            limit.Clear(XLClearOptions.Contents);
        }

        var due = _ws.Cell(DueRow, 2);
        if (card?.DueDateFor(_month) is { } d)
        {
            due.Value = d.ToDateTime(TimeOnly.MinValue);
            due.Style.DateFormat.Format = ExpensesSheet.DateFormat;
        }
        else
        {
            due.Clear(XLClearOptions.Contents);
        }
    }

    public void Insert(CardRepayment repayment)
    {
        CheckMonth(repayment);

        var row = FindInsertRow(repayment.Date);
        if (row <= LastLogRow())
        {
            // Only shift the log's own columns, not the expense copy beside it.
            _ws.Range(row, 1, row, ColId).InsertRowsAbove(1);
            _ws.Range(row, 1, row, ColId).Style = _ws.Style;
        }

        WriteRepayment(row, repayment);
        IsModified = true;
    }

    public bool Update(CardRepayment repayment)
    {
        CheckMonth(repayment);

        var row = FindRow(repayment.Id);
        if (row is null)
            return false;

        if (ExpensesSheet.TryReadDate(_ws.Cell(row.Value, ColDate), out var oldDate) && oldDate == repayment.Date)
        {
            WriteRepayment(row.Value, repayment);
            IsModified = true;
        }
        else
        {
            DeleteLogRow(row.Value);
            Insert(repayment);
        }

        return true;
    }

    public bool Delete(Guid id)
    {
        var row = FindRow(id);
        if (row is null)
            return false;

        DeleteLogRow(row.Value);
        IsModified = true;
        return true;
    }

    private void DeleteLogRow(int row) => _ws.Range(row, 1, row, ColId).Delete(XLShiftDeletedCells.ShiftCellsUp);

    // ---- Formulas and the expense copy ----------------------------------------

    /// <summary>
    /// (Re)writes the summary and running-balance formulas, and refreshes the
    /// copy of this month's card expenses. Called before every save.
    /// </summary>
    public void Refresh(ExpensesSheet expenses, IReadOnlyList<Expense> monthExpenses)
    {
        var f = new FormulaParts(this, expenses, qualified: false);
        var o = _openingRow;

        SetAmountFormula(SpentRow, f.CardSpending());
        SetAmountFormula(RepaidRow, $"-SUM({f.Log(ColAmount)})");
        SetAmountFormula(OwedRow, $"B{o}+B{SpentRow}+B{RepaidRow}");

        var used = _ws.Cell(UsedRow, 2);
        used.FormulaA1 = $"IF(ISNUMBER(B{LimitRow}),IF(B{LimitRow}>0,B{OwedRow}/B{LimitRow},\"\"),\"\")";
        used.Style.NumberFormat.Format = "0%";
        SetAmountFormula(AvailableRow, $"IF(ISNUMBER(B{LimitRow}),B{LimitRow}-B{OwedRow},\"\")");

        foreach (var row in LogRowNumbers())
        {
            if (IsBlankLogRow(row))
            {
                _ws.Cell(row, ColOwed).Clear(XLClearOptions.Contents);
                continue;
            }

            var upTo = $"\"<=\"&A{row}";
            SetAmountFormula(row, $"$B${o}+{f.CardSpending(upTo)}-SUMIFS({f.Log(ColAmount)},{f.Log(ColDate)},{upTo})", ColOwed);
        }

        WriteExpenseCopy(monthExpenses.Where(e => e.PaymentMethod == PaymentMethod.CreditCard).ToList());

        SheetHelpers.ApplyListValidation(_ws, ColFrom, FirstLogRow, LastLogRow() + FormulaRows,
            new[] { "Bank", "Cash" }, "Paid from", "Choose Bank or Cash.");
    }

    /// <summary>
    /// For formulas on Bank &amp; Cash: total repaid from <paramref name="account"/>,
    /// optionally only up to a date.
    /// </summary>
    public string RepaidFromFormula(Account account, string? dateCriteria = null)
    {
        var f = new FormulaParts(this, expenses: null, qualified: true);
        return $"SUMIFS({f.Log(ColAmount)},{f.Log(ColFrom)},\"{account.ToDisplayName()}\"" +
               (dateCriteria is null ? ")" : $",{f.Log(ColDate)},{dateCriteria})");
    }

    private void SetAmountFormula(int row, string formula, int col = 2)
    {
        var cell = _ws.Cell(row, col);
        cell.FormulaA1 = formula;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
    }

    private void WriteExpenseCopy(IReadOnlyList<Expense> cardExpenses)
    {
        var first = FirstLogRow;
        var last = Math.Max(LastUsedRow(MirrorFirstCol, MirrorLastCol), first);
        _ws.Range(first, MirrorFirstCol, last, MirrorLastCol).Clear(XLClearOptions.Contents);

        for (var i = 0; i < cardExpenses.Count; i++)
        {
            var e = cardExpenses[i];
            var row = first + i;
            var date = _ws.Cell(row, MirrorFirstCol);
            date.Value = e.Date.ToDateTime(TimeOnly.MinValue);
            date.Style.DateFormat.Format = ExpensesSheet.DateFormat;
            _ws.Cell(row, MirrorFirstCol + 1).Value = e.Category;
            _ws.Cell(row, MirrorFirstCol + 2).Value = e.FamilyMember;
            var amount = _ws.Cell(row, MirrorFirstCol + 3);
            amount.Value = e.Amount;
            amount.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
            _ws.Cell(row, MirrorFirstCol + 4).Value = e.Note;
        }
    }

    /// <summary>
    /// Builds formula pieces. With <c>qualified</c>, references carry this
    /// sheet's name so they work from other sheets.
    /// </summary>
    private sealed class FormulaParts(CreditCardSheet sheet, ExpensesSheet? expenses, bool qualified)
    {
        private string Prefix { get; } = qualified ? $"'{SheetName}'!" : string.Empty;

        public string Log(int col) =>
            $"{Prefix}${Letter(col)}${sheet.FirstLogRow}:${Letter(col)}${sheet.FirstLogRow + FormulaRows}";

        private static string Exp(int col) => $"'{ExpensesSheet.SheetName}'!${Letter(col)}$2:${Letter(col)}${FormulaRows * 4}";

        public string CardSpending(string? dateCriteria = null) =>
            $"SUMIFS({Exp(expenses!.AmountColumn)},{Exp(expenses.PaymentColumn)},\"{PaymentMethod.CreditCard.ToDisplayName()}\"" +
            (dateCriteria is null ? ")" : $",{Exp(expenses.DateColumn)},{dateCriteria})");
    }

    private static string Letter(int col) => XLHelper.GetColumnLetterFromNumber(col);

    // ---- Layout -------------------------------------------------------------

    private bool TryLocate()
    {
        _openingRow = 0;
        _logHeaderRow = 0;
        var last = Math.Min(_ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0, 60);

        for (var row = 1; row <= last; row++)
        {
            var label = _ws.Cell(row, 1).GetString().Trim();
            if (_openingRow == 0 && label.Equals(OpeningLabel, StringComparison.OrdinalIgnoreCase)
                && _ws.Cell(row + 1, 1).GetString().Trim().Equals(SourceLabel, StringComparison.OrdinalIgnoreCase))
            {
                _openingRow = row;
            }
            else if (_openingRow != 0 && label.Equals("Date", StringComparison.OrdinalIgnoreCase)
                     && _ws.Cell(row, ColAmount).GetString().Trim().Equals("Amount", StringComparison.OrdinalIgnoreCase)
                     && _ws.Cell(row, ColFrom).GetString().Trim().Equals("Paid from", StringComparison.OrdinalIgnoreCase))
            {
                _logHeaderRow = row;
                break;
            }
        }

        return _openingRow != 0 && _logHeaderRow != 0;
    }

    private void BuildLayout()
    {
        _ws.Cell(1, 1).Value = "Credit Card";
        _ws.Cell(1, 1).Style.Font.FontSize = 16;
        _ws.Cell(1, 1).Style.Font.Bold = true;
        _ws.Cell(2, 1).Value = "Card spending comes from the Expenses sheet (Payment Method = Credit Card). " +
                               "Log repayments below; the credit limit and due date are set in Hisaab Kitaab.";
        _ws.Cell(2, 1).Style.Font.Italic = true;
        _ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        _openingRow = 4;
        string[] labels =
        {
            OpeningLabel, SourceLabel, "+ Card expenses", "− Repayments", "Outstanding", "",
            "Credit limit", "Limit used", "Available", "This month's bill due",
        };
        for (var i = 0; i < labels.Length; i++)
            _ws.Cell(_openingRow + i, 1).Value = labels[i];

        SetOpening(0, isManual: false);
        _ws.Cell(SourceRow, 2).Style.Font.Italic = true;
        _ws.Range(OwedRow, 1, OwedRow, 2).Style.Font.Bold = true;
        _ws.Range(OwedRow, 1, OwedRow, 2).Style.Border.TopBorder = XLBorderStyleValues.Thin;

        var titleRow = DueRow + 2;
        _ws.Cell(titleRow, 1).Value = "Repayments";
        _ws.Cell(titleRow, 1).Style.Font.Bold = true;
        _ws.Cell(titleRow, 1).Style.Font.FontSize = 13;
        _ws.Cell(titleRow, MirrorFirstCol).Value = "Card expenses this month (copied from the Expenses sheet; change them there)";
        _ws.Cell(titleRow, MirrorFirstCol).Style.Font.Bold = true;
        _ws.Cell(titleRow, MirrorFirstCol).Style.Font.FontSize = 13;

        _logHeaderRow = titleRow + 1;
        string[] headers = { "Date", "Amount", "Paid from", "Note", "Outstanding (end of day)", "ID" };
        for (var i = 0; i < headers.Length; i++)
            _ws.Cell(_logHeaderRow, i + 1).Value = headers[i];
        StyleHeader(_ws.Range(_logHeaderRow, 1, _logHeaderRow, headers.Length));

        string[] mirrorHeaders = { "Date", "Category", "Family Member", "Amount", "Note" };
        for (var i = 0; i < mirrorHeaders.Length; i++)
            _ws.Cell(_logHeaderRow, MirrorFirstCol + i).Value = mirrorHeaders[i];
        StyleHeader(_ws.Range(_logHeaderRow, MirrorFirstCol, _logHeaderRow, MirrorLastCol));

        double[] widths = { 22, 16, 12, 30, 24, 38, 3, 14, 18, 16, 14, 30 };
        for (var i = 0; i < widths.Length; i++)
            _ws.Column(i + 1).Width = widths[i];
        _ws.Column(ColId).Hide();
        _ws.SheetView.FreezeRows(_logHeaderRow);

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

    // The expense copy sits beside the log, so only the log's own columns decide where it ends.
    private int LastLogRow() => Math.Max(LastUsedRow(ColDate, ColNote), _logHeaderRow);

    private int LastUsedRow(int firstCol, int lastCol) =>
        Enumerable.Range(firstCol, lastCol - firstCol + 1)
            .Select(c => _ws.Column(c).LastCellUsed(XLCellsUsedOptions.Contents)?.Address.RowNumber ?? 0)
            .Max();

    private IEnumerable<int> LogRowNumbers()
    {
        var last = LastLogRow();
        for (var row = FirstLogRow; row <= last; row++)
            yield return row;
    }

    private bool IsBlankLogRow(int row) =>
        new[] { ColDate, ColAmount, ColFrom, ColNote }.All(c => _ws.Cell(row, c).Value.IsBlank);

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

    private void WriteRepayment(int row, CardRepayment r)
    {
        var date = _ws.Cell(row, ColDate);
        date.Value = r.Date.ToDateTime(TimeOnly.MinValue);
        date.Style.DateFormat.Format = ExpensesSheet.DateFormat;

        var amount = _ws.Cell(row, ColAmount);
        amount.Value = r.Amount;
        amount.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;

        _ws.Cell(row, ColFrom).Value = r.PaidFrom.ToDisplayName();
        _ws.Cell(row, ColNote).Value = r.Note;
        _ws.Cell(row, ColId).Value = r.Id.ToString();
    }

    private bool TryReadRepayment(int row, out CardRepayment repayment, out string error)
    {
        repayment = null!;

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

        if (!ExpensesSheet.TryReadAmount(_ws.Cell(row, ColAmount), out var amount) || amount <= 0)
        {
            error = $"\"{_ws.Cell(row, ColAmount).GetFormattedString()}\" isn't an amount more than zero.";
            return false;
        }

        var fromText = _ws.Cell(row, ColFrom).GetString().Trim();
        if (!AccountNames.TryParse(fromText, out var from))
        {
            error = fromText.Length == 0 ? "\"Paid from\" is missing (Bank or Cash)." : $"\"{fromText}\" isn't Bank or Cash.";
            return false;
        }

        Guid.TryParse(_ws.Cell(row, ColId).GetString().Trim(), out var id);
        repayment = new CardRepayment
        {
            Id = id,
            Date = date,
            Amount = amount,
            PaidFrom = from,
            Note = _ws.Cell(row, ColNote).GetString().Trim(),
        };
        error = string.Empty;
        return true;
    }

    private void CheckMonth(CardRepayment repayment)
    {
        if (!_month.Contains(repayment.Date))
            throw new ArgumentException(
                $"{repayment.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} is not in {_month.DisplayName}.", nameof(repayment));
    }
}
