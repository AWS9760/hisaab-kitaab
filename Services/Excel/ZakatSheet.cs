using System.Globalization;
using ClosedXML.Excel;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services.Excel;

/// <summary>
/// Reads and edits the "Zakat" worksheet of a yearly zakat workbook:
///
///   The zakat year's start and end dates (picked by the user)
///   A summary in formulas: carried from last year, set aside, total taken
///   out, given, remaining, and how much was given from the bank and in cash
///   A log of zakat set aside and given, with what's left at the end of each day
/// </summary>
internal sealed class ZakatSheet
{
    public const string SheetName = "Zakat";

    private const string StartLabel = "Zakat year starts";
    private const string EndLabel = "Zakat year ends";
    private const string CarriedLabel = "Carried from last year";
    private const string SourceLabel = "Carried set by";
    private const string ManualSource = "You";
    private const string CarriedSource = "Carried forward";

    // Log columns.
    private const int ColDate = 1, ColType = 2, ColAmount = 3, ColFrom = 4, ColRecipient = 5, ColNote = 6, ColRemaining = 7, ColId = 8;

    private const int FormulaRows = 5000;

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#0F766E");

    private readonly IXLWorksheet _ws;
    private readonly int _year;
    private int _startRow;
    private int _carriedRow;
    private int _logHeaderRow;

    private ZakatSheet(IXLWorksheet ws, int year)
    {
        _ws = ws;
        _year = year;
    }

    public bool IsModified { get; private set; }

    private int EndRow => _startRow + 1;
    private int SourceRow => _carriedRow + 1;
    private int SetAsideRow => _carriedRow + 2;
    private int TakenOutRow => _carriedRow + 3;
    private int GivenRow => _carriedRow + 4;
    private int RemainingRow => _carriedRow + 5;
    private int FromBankRow => _carriedRow + 6;
    private int FromCashRow => _carriedRow + 7;
    private int FirstLogRow => _logHeaderRow + 1;

    public static ZakatSheet? Find(XLWorkbook workbook, int year)
    {
        if (!workbook.TryGetWorksheet(SheetName, out var ws))
            return null;

        var sheet = new ZakatSheet(ws, year);
        return sheet.TryLocate() ? sheet : null;
    }

    /// <summary>
    /// Returns the sheet, creating it with <paramref name="periodIfNew"/> if
    /// missing. A sheet with that name laid out differently is kept under another name.
    /// </summary>
    public static ZakatSheet GetOrCreate(XLWorkbook workbook, int year, ZakatPeriod periodIfNew)
    {
        if (workbook.TryGetWorksheet(SheetName, out var existing))
        {
            var sheet = new ZakatSheet(existing, year);
            if (sheet.TryLocate())
                return sheet;

            existing.Name = UniqueName(workbook, "Zakat (old)");
        }

        var created = new ZakatSheet(workbook.AddWorksheet(SheetName, 1), year);
        created.BuildLayout(periodIfNew);
        return created;
    }

    // ---- Reading ------------------------------------------------------------

    public ZakatYearData Read()
    {
        var entries = new List<ZakatEntry>();
        var problems = new List<SheetProblem>();
        var seenIds = new HashSet<Guid>();

        var period = ReadPeriod();
        if (period is null)
            problems.Add(new SheetProblem(SheetName, _startRow,
                "The zakat year's start and end dates couldn't be read (the end must be on or after the start)."));

        foreach (var row in LogRowNumbers())
        {
            if (IsBlankLogRow(row))
                continue;

            if (!TryReadEntry(row, period, out var entry, out var error))
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

        var stored = ExpensesSheet.TryReadAmount(_ws.Cell(_carriedRow, 2), out var carried) ? carried : (decimal?)null;
        var manual = _ws.Cell(SourceRow, 2).GetString().Trim().ToLowerInvariant() is "you" or "manual" or "me";

        return new ZakatYearData(_year, true, period, manual ? stored ?? 0 : null, entries, problems)
        {
            StoredCarried = stored,
        };
    }

    private ZakatPeriod? ReadPeriod() =>
        ExpensesSheet.TryReadDate(_ws.Cell(_startRow, 2), out var start)
        && ExpensesSheet.TryReadDate(_ws.Cell(EndRow, 2), out var end)
        && end >= start
            ? new ZakatPeriod(start, end)
            : null;

    // ---- Writing ------------------------------------------------------------

    public void SetPeriod(ZakatPeriod period)
    {
        WriteDate(_ws.Cell(_startRow, 2), period.Start);
        WriteDate(_ws.Cell(EndRow, 2), period.End);
        IsModified = true;
    }

    /// <summary>
    /// Writes the amount carried from last year. <paramref name="isManual"/>
    /// false marks it as carried forward, so the app keeps it in step.
    /// </summary>
    public void SetCarried(decimal value, bool isManual)
    {
        var cell = _ws.Cell(_carriedRow, 2);
        cell.Value = value;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
        _ws.Cell(SourceRow, 2).Value = isManual ? ManualSource : CarriedSource;
        IsModified = true;
    }

    public void Insert(ZakatEntry entry)
    {
        CheckDate(entry);

        var row = FindInsertRow(entry.Date);
        if (row <= LastLogRow())
        {
            _ws.Range(row, 1, row, ColId).InsertRowsAbove(1);
            _ws.Range(row, 1, row, ColId).Style = _ws.Style;
        }

        WriteEntry(row, entry);
        IsModified = true;
    }

    public bool Update(ZakatEntry entry)
    {
        CheckDate(entry);

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
            DeleteLogRow(row.Value);
            Insert(entry);
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

    // ---- Formulas -----------------------------------------------------------

    /// <summary>
    /// (Re)writes the summary and running formulas so they cover the current
    /// log rows, and the dropdowns. Called before every save.
    /// </summary>
    public void RefreshFormulas()
    {
        var c = _carriedRow;
        string Sum(ZakatEntryType type, string? extra = null) =>
            $"SUMIFS({Log(ColAmount)},{Log(ColType)},\"{type.ToDisplayName()}\"{(extra is null ? "" : "," + extra)})";

        SetAmountFormula(SetAsideRow, Sum(ZakatEntryType.SetAside));
        SetAmountFormula(TakenOutRow, $"B{c}+B{SetAsideRow}");
        SetAmountFormula(GivenRow, "-" + Sum(ZakatEntryType.Given));
        SetAmountFormula(RemainingRow, $"B{TakenOutRow}+B{GivenRow}");
        SetAmountFormula(FromBankRow, Sum(ZakatEntryType.Given, $"{Log(ColFrom)},\"{Account.Bank.ToDisplayName()}\""));
        SetAmountFormula(FromCashRow, Sum(ZakatEntryType.Given, $"{Log(ColFrom)},\"{Account.Cash.ToDisplayName()}\""));

        foreach (var row in LogRowNumbers())
        {
            if (IsBlankLogRow(row))
            {
                _ws.Cell(row, ColRemaining).Clear(XLClearOptions.Contents);
                continue;
            }

            var upTo = $"{Log(ColDate)},\"<=\"&A{row}";
            SetAmountFormula(row, $"$B${c}+{Sum(ZakatEntryType.SetAside, upTo)}-{Sum(ZakatEntryType.Given, upTo)}", ColRemaining);
        }

        var lastRow = LastLogRow() + FormulaRows;
        SheetHelpers.ApplyListValidation(_ws, ColType, FirstLogRow, lastRow,
            ZakatEntryTypes.All.Select(t => t.ToDisplayName()), "Type", "Choose Set aside or Given.");
        SheetHelpers.ApplyListValidation(_ws, ColFrom, FirstLogRow, lastRow,
            new[] { Account.Bank.ToDisplayName(), Account.Cash.ToDisplayName() }, "Paid from",
            "For zakat given: Bank or Cash. Leave blank for zakat set aside.");
    }

    private string Log(int col) => $"${Letter(col)}${FirstLogRow}:${Letter(col)}${FirstLogRow + FormulaRows}";

    private void SetAmountFormula(int row, string formula, int col = 2)
    {
        var cell = _ws.Cell(row, col);
        cell.FormulaA1 = formula;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
    }

    private static string Letter(int col) => XLHelper.GetColumnLetterFromNumber(col);

    // ---- Layout -------------------------------------------------------------

    private bool TryLocate()
    {
        _startRow = 0;
        _carriedRow = 0;
        _logHeaderRow = 0;
        var last = Math.Min(_ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0, 60);

        for (var row = 1; row <= last; row++)
        {
            var label = _ws.Cell(row, 1).GetString().Trim();
            if (_startRow == 0 && label.Equals(StartLabel, StringComparison.OrdinalIgnoreCase)
                && Label(row + 1).Equals(EndLabel, StringComparison.OrdinalIgnoreCase))
            {
                _startRow = row;
            }
            else if (_carriedRow == 0 && label.Equals(CarriedLabel, StringComparison.OrdinalIgnoreCase)
                     && Label(row + 1).Equals(SourceLabel, StringComparison.OrdinalIgnoreCase))
            {
                _carriedRow = row;
            }
            else if (_startRow != 0 && _carriedRow != 0 && label.Equals("Date", StringComparison.OrdinalIgnoreCase)
                     && Label(row, ColType).Equals("Type", StringComparison.OrdinalIgnoreCase)
                     && Label(row, ColAmount).Equals("Amount", StringComparison.OrdinalIgnoreCase)
                     && Label(row, ColFrom).Equals("Paid from", StringComparison.OrdinalIgnoreCase))
            {
                _logHeaderRow = row;
                break;
            }
        }

        return _startRow != 0 && _carriedRow != 0 && _logHeaderRow != 0;
    }

    private string Label(int row, int col = 1) => _ws.Cell(row, col).GetString().Trim();

    private void BuildLayout(ZakatPeriod period)
    {
        _ws.Cell(1, 1).Value = $"Zakat {_year}";
        _ws.Cell(1, 1).Style.Font.FontSize = 16;
        _ws.Cell(1, 1).Style.Font.Bold = true;
        _ws.Cell(2, 1).Value = "Log zakat set aside and zakat given below. Zakat given from the bank or in cash comes off " +
                               "that balance on the month's Bank & Cash sheet. Whatever's left carries into next year.";
        _ws.Cell(2, 1).Style.Font.Italic = true;
        _ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        _startRow = 4;
        _ws.Cell(_startRow, 1).Value = StartLabel;
        _ws.Cell(EndRow, 1).Value = EndLabel;
        SetPeriod(period);

        _carriedRow = 7;
        string[] labels = { CarriedLabel, SourceLabel, "+ Set aside", "Total taken out", "− Given", "Remaining", "Given from bank", "Given in cash" };
        for (var i = 0; i < labels.Length; i++)
            _ws.Cell(_carriedRow + i, 1).Value = labels[i];

        SetCarried(0, isManual: false);
        _ws.Cell(SourceRow, 2).Style.Font.Italic = true;
        _ws.Range(TakenOutRow, 1, TakenOutRow, 2).Style.Font.Bold = true;
        _ws.Range(RemainingRow, 1, RemainingRow, 2).Style.Font.Bold = true;
        _ws.Range(RemainingRow, 1, RemainingRow, 2).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        _ws.Range(FromBankRow, 1, FromCashRow, 2).Style.Font.FontColor = XLColor.Gray;

        var titleRow = FromCashRow + 2;
        _ws.Cell(titleRow, 1).Value = "Zakat set aside and given";
        _ws.Cell(titleRow, 1).Style.Font.Bold = true;
        _ws.Cell(titleRow, 1).Style.Font.FontSize = 13;

        _logHeaderRow = titleRow + 1;
        string[] headers = { "Date", "Type", "Amount", "Paid from", "Recipient", "Note", "Remaining (end of day)", "ID" };
        for (var i = 0; i < headers.Length; i++)
            _ws.Cell(_logHeaderRow, i + 1).Value = headers[i];
        var header = _ws.Range(_logHeaderRow, 1, _logHeaderRow, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = HeaderFill;

        double[] widths = { 24, 12, 16, 12, 26, 32, 24, 38 };
        for (var i = 0; i < widths.Length; i++)
            _ws.Column(i + 1).Width = widths[i];
        _ws.Column(ColId).Hide();
        _ws.SheetView.FreezeRows(_logHeaderRow);

        IsModified = true;
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
        Math.Max(Enumerable.Range(ColDate, ColNote - ColDate + 1)
            .Select(c => _ws.Column(c).LastCellUsed(XLCellsUsedOptions.Contents)?.Address.RowNumber ?? 0)
            .Max(), _logHeaderRow);

    private IEnumerable<int> LogRowNumbers()
    {
        var last = LastLogRow();
        for (var row = FirstLogRow; row <= last; row++)
            yield return row;
    }

    // The running total holds a formula, so it doesn't count towards a row being filled in.
    private bool IsBlankLogRow(int row) =>
        new[] { ColDate, ColType, ColAmount, ColFrom, ColRecipient, ColNote }.All(c => _ws.Cell(row, c).Value.IsBlank);

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

    private void WriteEntry(int row, ZakatEntry e)
    {
        WriteDate(_ws.Cell(row, ColDate), e.Date);
        _ws.Cell(row, ColType).Value = e.Type.ToDisplayName();

        var amount = _ws.Cell(row, ColAmount);
        amount.Value = e.Amount;
        amount.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;

        _ws.Cell(row, ColFrom).Value = e.PaidFrom is { } from ? from.ToDisplayName() : string.Empty;
        _ws.Cell(row, ColRecipient).Value = e.Recipient;
        _ws.Cell(row, ColNote).Value = e.Note;
        _ws.Cell(row, ColId).Value = e.Id.ToString();
    }

    private static void WriteDate(IXLCell cell, DateOnly date)
    {
        cell.Value = date.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = ExpensesSheet.DateFormat;
    }

    private bool TryReadEntry(int row, ZakatPeriod? period, out ZakatEntry entry, out string error)
    {
        entry = null!;

        if (!ExpensesSheet.TryReadDate(_ws.Cell(row, ColDate), out var date))
        {
            error = $"\"{_ws.Cell(row, ColDate).GetFormattedString()}\" isn't a date.";
            return false;
        }

        if (period is { } p && !p.Contains(date))
        {
            error = $"{date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} is outside this zakat year ({p}).";
            return false;
        }

        var typeText = _ws.Cell(row, ColType).GetString().Trim();
        if (!ZakatEntryTypes.TryParse(typeText, out var type))
        {
            error = typeText.Length == 0 ? "Type is missing (Set aside or Given)." : $"\"{typeText}\" isn't Set aside or Given.";
            return false;
        }

        if (!ExpensesSheet.TryReadAmount(_ws.Cell(row, ColAmount), out var amount) || amount <= 0)
        {
            error = $"\"{_ws.Cell(row, ColAmount).GetFormattedString()}\" isn't an amount more than zero.";
            return false;
        }

        Account? from = null;
        if (type == ZakatEntryType.Given)
        {
            var fromText = _ws.Cell(row, ColFrom).GetString().Trim();
            if (!AccountNames.TryParse(fromText, out var account))
            {
                error = fromText.Length == 0 ? "\"Paid from\" is missing (Bank or Cash)." : $"\"{fromText}\" isn't Bank or Cash.";
                return false;
            }

            from = account;
        }

        Guid.TryParse(_ws.Cell(row, ColId).GetString().Trim(), out var id);
        entry = new ZakatEntry
        {
            Id = id,
            Date = date,
            Type = type,
            Amount = amount,
            PaidFrom = from,
            Recipient = type == ZakatEntryType.Given ? _ws.Cell(row, ColRecipient).GetString().Trim() : string.Empty,
            Note = _ws.Cell(row, ColNote).GetString().Trim(),
        };
        error = string.Empty;
        return true;
    }

    private void CheckDate(ZakatEntry entry)
    {
        if (ReadPeriod() is { } period && !period.Contains(entry.Date))
            throw new ArgumentException(
                $"{entry.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} is outside the {_year} zakat year ({period}).", nameof(entry));
    }
}
