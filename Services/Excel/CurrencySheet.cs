using ClosedXML.Excel;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services.Excel;

/// <summary>
/// Reads and writes the "Currency Denominations" worksheet: a count of each
/// PKR note plus coins, the total, and formulas comparing it with cash in hand
/// (from the Bank &amp; Cash sheet) on the day of the count.
/// </summary>
internal sealed class CurrencySheet
{
    public const string SheetName = "Currency Denominations";

    private const string HeaderLabel = "Note";
    private const string CoinsLabel = "Coins & loose change";
    private const string TotalLabel = "Total counted";
    private const string DateLabel = "Counted on";
    private const string ExpectedLabel = "Cash in hand that day (Bank & Cash)";
    private const string DifferenceLabel = "Difference";
    private const string StatusLabel = "Result";

    private const int ColNote = 1, ColCount = 2, ColValue = 3;

    private readonly IXLWorksheet _ws;
    private readonly YearMonth _month;
    private int _headerRow;

    private CurrencySheet(IXLWorksheet ws, YearMonth month)
    {
        _ws = ws;
        _month = month;
    }

    private int FirstNoteRow => _headerRow + 1;

    private int CoinsRow => FirstNoteRow + PkrNotes.Denominations.Count;

    private int TotalRow => CoinsRow + 1;

    private int DateRow => TotalRow + 2;

    private int ExpectedRow => DateRow + 1;

    private int DifferenceRow => ExpectedRow + 1;

    private int StatusRow => DifferenceRow + 1;

    public static CurrencySheet? Find(XLWorkbook workbook, YearMonth month)
    {
        if (!workbook.TryGetWorksheet(SheetName, out var ws))
            return null;

        var sheet = new CurrencySheet(ws, month);
        return sheet.TryLocate() ? sheet : null;
    }

    /// <summary>
    /// Returns the sheet, creating it (after Bank &amp; Cash) if missing. A sheet
    /// with that name that isn't laid out as expected is kept under another name.
    /// </summary>
    public static CurrencySheet GetOrCreate(XLWorkbook workbook, YearMonth month)
    {
        if (workbook.TryGetWorksheet(SheetName, out var existing))
        {
            var sheet = new CurrencySheet(existing, month);
            if (sheet.TryLocate())
                return sheet;

            existing.Name = UniqueName(workbook, "Currency (old)");
        }

        var after = workbook.TryGetWorksheet(BankCashSheet.SheetName, out var bank) ? bank.Position
            : workbook.TryGetWorksheet(ExpensesSheet.SheetName, out var exp) ? exp.Position : 0;
        var created = new CurrencySheet(workbook.AddWorksheet(SheetName, after + 1), month);
        created.BuildLayout();
        return created;
    }

    public CurrencySheetData Read()
    {
        var problems = new List<SheetProblem>();
        var notes = new Dictionary<int, int>();

        for (var i = 0; i < PkrNotes.Denominations.Count; i++)
        {
            var row = FirstNoteRow + i;
            var cell = _ws.Cell(row, ColCount);
            if (cell.Value.IsBlank)
                continue;

            if (cell.Value.IsNumber && cell.Value.GetNumber() is var n && n >= 0 && n <= PkrNotes.MaxCount && n == Math.Floor(n))
                notes[PkrNotes.Denominations[i]] = (int)n;
            else
                problems.Add(new SheetProblem(SheetName, row, $"\"{cell.GetFormattedString()}\" isn't a number of notes."));
        }

        decimal coins = 0;
        var coinsCell = _ws.Cell(CoinsRow, ColValue);
        if (!coinsCell.Value.IsBlank && (!ExpensesSheet.TryReadAmount(coinsCell, out coins) || coins < 0))
        {
            problems.Add(new SheetProblem(SheetName, CoinsRow, $"\"{coinsCell.GetFormattedString()}\" isn't an amount."));
            coins = 0;
        }

        DateOnly? countedOn = null;
        var dateCell = _ws.Cell(DateRow, 2);
        if (!dateCell.Value.IsBlank)
        {
            if (ExpensesSheet.TryReadDate(dateCell, out var date) && _month.Contains(date))
                countedOn = date;
            else
                problems.Add(new SheetProblem(SheetName, DateRow, $"\"{dateCell.GetFormattedString()}\" isn't a date in {_month.DisplayName}."));
        }

        var count = new CurrencyCount { CountedOn = countedOn, Notes = notes, Coins = coins };
        return new CurrencySheetData(_month, true, count, problems);
    }

    public void Write(CurrencyCount count)
    {
        if (count.CountedOn is { } date && !_month.Contains(date))
            throw new ArgumentException($"The count date must be in {_month.DisplayName}.", nameof(count));

        for (var i = 0; i < PkrNotes.Denominations.Count; i++)
        {
            var n = count.CountOf(PkrNotes.Denominations[i]);
            if (n is < 0 or > PkrNotes.MaxCount)
                throw new ArgumentException($"Note counts must be between 0 and {PkrNotes.MaxCount:N0}.", nameof(count));
            _ws.Cell(FirstNoteRow + i, ColCount).Value = n;
        }

        var coins = _ws.Cell(CoinsRow, ColValue);
        coins.Value = count.Coins;
        coins.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;

        var dateCell = _ws.Cell(DateRow, 2);
        if (count.CountedOn is { } d)
        {
            dateCell.Value = d.ToDateTime(TimeOnly.MinValue);
            dateCell.Style.DateFormat.Format = ExpensesSheet.DateFormat;
        }
        else
        {
            dateCell.Clear(XLClearOptions.Contents);
        }
    }

    /// <summary>
    /// (Re)writes the totals and the comparison with Bank &amp; Cash.
    /// </summary>
    public void RefreshFormulas(BankCashSheet bank, CreditCardSheet card, ExpensesSheet expenses)
    {
        for (var i = 0; i < PkrNotes.Denominations.Count; i++)
        {
            var row = FirstNoteRow + i;
            SetFormula(row, ColValue, $"A{row}*B{row}");
        }

        SetFormula(TotalRow, ColValue, $"SUM(C{FirstNoteRow}:C{CoinsRow})");
        SetFormula(ExpectedRow, ColValue, bank.CashOnDateFormula(expenses, card, $"$B${DateRow}"));
        SetFormula(DifferenceRow, ColValue, $"C{TotalRow}-C{ExpectedRow}");

        var status = _ws.Cell(StatusRow, ColValue);
        status.FormulaA1 =
            $"IF(C{TotalRow}=0,\"Not counted yet\",IF(ABS(C{DifferenceRow})<1,\"Matches\"," +
            $"IF(C{DifferenceRow}>0,\"More cash than recorded\",\"Less cash than recorded\")))";
        status.Style.Font.Bold = true;
    }

    private void SetFormula(int row, int col, string formula)
    {
        var cell = _ws.Cell(row, col);
        cell.FormulaA1 = formula;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
    }

    private bool TryLocate()
    {
        var last = Math.Min(_ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0, 40);
        for (var row = 1; row <= last; row++)
        {
            if (!_ws.Cell(row, ColNote).GetString().Trim().Equals(HeaderLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            _headerRow = row;

            // The note rows must hold the denominations in the expected order.
            for (var i = 0; i < PkrNotes.Denominations.Count; i++)
            {
                var cell = _ws.Cell(FirstNoteRow + i, ColNote);
                if (!cell.Value.IsNumber || (int)cell.Value.GetNumber() != PkrNotes.Denominations[i])
                    return false;
            }

            return _ws.Cell(DateRow, ColNote).GetString().Trim().Equals(DateLabel, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void BuildLayout()
    {
        _ws.Cell(1, 1).Value = "Currency Denominations";
        _ws.Cell(1, 1).Style.Font.FontSize = 16;
        _ws.Cell(1, 1).Style.Font.Bold = true;
        _ws.Cell(2, 1).Value = "Count your notes and enter how many of each. The total is compared with cash in hand " +
                               "on the Bank & Cash sheet for the day you counted.";
        _ws.Cell(2, 1).Style.Font.Italic = true;
        _ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        _headerRow = 4;
        _ws.Cell(_headerRow, ColNote).Value = HeaderLabel;
        _ws.Cell(_headerRow, ColCount).Value = "Count";
        _ws.Cell(_headerRow, ColValue).Value = "Value";
        var header = _ws.Range(_headerRow, 1, _headerRow, 3);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");

        for (var i = 0; i < PkrNotes.Denominations.Count; i++)
        {
            var note = _ws.Cell(FirstNoteRow + i, ColNote);
            note.Value = PkrNotes.Denominations[i];
            note.Style.NumberFormat.Format = "\"₨ \"#,##0";
            note.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            _ws.Cell(FirstNoteRow + i, ColCount).Value = 0;
        }

        var counts = _ws.Range(FirstNoteRow, ColCount, CoinsRow - 1, ColCount).CreateDataValidation();
        counts.WholeNumber.Between(0, PkrNotes.MaxCount);
        counts.ErrorMessage = "Enter how many notes, e.g. 12.";

        _ws.Cell(CoinsRow, ColNote).Value = CoinsLabel;
        _ws.Cell(TotalRow, ColNote).Value = TotalLabel;
        _ws.Range(TotalRow, 1, TotalRow, 3).Style.Font.Bold = true;
        _ws.Range(TotalRow, 1, TotalRow, 3).Style.Border.TopBorder = XLBorderStyleValues.Thin;

        _ws.Cell(DateRow, ColNote).Value = DateLabel;
        _ws.Cell(ExpectedRow, ColNote).Value = ExpectedLabel;
        _ws.Cell(DifferenceRow, ColNote).Value = DifferenceLabel;
        _ws.Cell(StatusRow, ColNote).Value = StatusLabel;

        _ws.Column(1).Width = 36;
        _ws.Column(2).Width = 16;
        _ws.Column(3).Width = 18;
    }

    private static string UniqueName(XLWorkbook workbook, string name)
    {
        var candidate = name;
        for (var i = 2; workbook.Worksheets.Contains(candidate); i++)
            candidate = $"{name} {i}";
        return candidate;
    }
}
