using System.Globalization;
using ClosedXML.Excel;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services.Excel;

/// <summary>
/// Reads and edits the "Expenses" worksheet of a monthly workbook.
///
/// Rows are kept sorted by date. Columns are located by their header text, so
/// the sheet keeps working if the user reorders columns in Excel. Each expense
/// has a hidden ID column so it can be found again for edits and deletes.
/// Rows that can't be understood are reported as problems and never modified.
/// </summary>
internal sealed class ExpensesSheet
{
    public const string SheetName = "Expenses";

    internal const string DateFormat = "dd-mmm-yyyy";
    internal const string AmountFormat = "\"₨ \"#,##0.00";

    private const int HeaderRow = 1;
    private const int FirstDataRow = 2;

    // How far below the data the Payment Method dropdown reaches, so rows typed
    // by hand in Excel get it too.
    private const int DropdownRows = 5000;

    private enum Col { Date, FamilyMember, Category, Amount, Note, PaymentMethod, Id }

    private sealed record ColumnSpec(Col Key, string Header, double Width, params string[] Aliases);

    // Canonical order and header text for new sheets.
    private static readonly ColumnSpec[] Columns =
    {
        new(Col.Date, "Date", 14, "date", "day"),
        new(Col.FamilyMember, "Family Member", 19, "familymember", "member", "person", "who"),
        new(Col.Category, "Category", 18, "category"),
        new(Col.Amount, "Amount", 17, "amount", "amountpkr", "pkr", "rs"),
        new(Col.Note, "Note", 40, "note", "notes", "description", "details"),
        new(Col.PaymentMethod, "Payment Method", 19, "paymentmethod", "payment", "paidby", "method"),
        new(Col.Id, "ID", 38, "id"),
    };

    private static readonly string[] TextDateFormats =
    {
        "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d/M/yy", "d-M-yy",
        "d-MMM-yyyy", "d MMM yyyy", "d-MMM-yy", "d MMM yy",
        "d-MMMM-yyyy", "d MMMM yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd",
    };

    private readonly IXLWorksheet _ws;
    private readonly YearMonth _month;
    private readonly Dictionary<Col, int> _cols = new();

    private ExpensesSheet(IXLWorksheet ws, YearMonth month)
    {
        _ws = ws;
        _month = month;
        MapColumns();
    }

    /// <summary>
    /// True once anything in the sheet has been changed and needs saving.
    /// </summary>
    public bool IsModified { get; private set; }

    // Where these columns currently are, for formulas on other sheets.
    public int DateColumn => _cols[Col.Date];

    public int AmountColumn => _cols[Col.Amount];

    public int PaymentColumn => _cols[Col.PaymentMethod];

    public static ExpensesSheet? Find(XLWorkbook workbook, YearMonth month) =>
        workbook.TryGetWorksheet(SheetName, out var ws) ? new ExpensesSheet(ws, month) : null;

    /// <summary>
    /// Returns the Expenses sheet, creating it as the first sheet if it's missing.
    /// </summary>
    public static ExpensesSheet GetOrCreate(XLWorkbook workbook, YearMonth month)
    {
        if (workbook.TryGetWorksheet(SheetName, out var existing))
            return new ExpensesSheet(existing, month);

        var ws = workbook.AddWorksheet(SheetName, 1);
        return new ExpensesSheet(ws, month) { IsModified = true };
    }

    /// <summary>
    /// Reads every data row. Rows missing an ID (e.g. added by hand in Excel),
    /// or sharing one with an earlier row (copy-pasted), are given a new ID,
    /// which marks the sheet as modified.
    /// </summary>
    public (List<Expense> Expenses, List<SheetProblem> Problems) ReadAll()
    {
        var expenses = new List<Expense>();
        var problems = new List<SheetProblem>();
        var seenIds = new HashSet<Guid>();

        foreach (var row in DataRowNumbers())
        {
            if (IsBlankRow(row))
                continue;

            if (!TryReadRow(row, out var expense, out var error))
            {
                problems.Add(new SheetProblem(SheetName, row, error));
                continue;
            }

            if (expense.Id == Guid.Empty || !seenIds.Add(expense.Id))
            {
                expense = expense with { Id = Guid.NewGuid() };
                seenIds.Add(expense.Id);
                Cell(row, Col.Id).Value = expense.Id.ToString();
                IsModified = true;
            }

            expenses.Add(expense);
        }

        return (expenses, problems);
    }

    public void Insert(Expense expense)
    {
        if (!_month.Contains(expense.Date))
            throw new ArgumentException($"{expense.Date:d MMM yyyy} is not in {_month.DisplayName}.", nameof(expense));

        var row = FindInsertRow(expense.Date);
        if (row <= LastDataRow())
        {
            _ws.Row(row).InsertRowsAbove(1);

            // Inserted rows copy the style of the row above, which is the
            // header when inserting at the top. Reset to a plain data row.
            _ws.Range(row, 1, row, LastColumn()).Style = _ws.Style;
        }

        WriteRow(row, expense);
        AfterRowsChanged();
    }

    /// <summary>
    /// Overwrites the row with the same ID, moving it if its date changed.
    /// Returns false if no such row exists.
    /// </summary>
    public bool Update(Expense expense)
    {
        if (!_month.Contains(expense.Date))
            throw new ArgumentException($"{expense.Date:d MMM yyyy} is not in {_month.DisplayName}.", nameof(expense));

        var row = FindRow(expense.Id);
        if (row is null)
            return false;

        if (TryReadDate(Cell(row.Value, Col.Date), out var oldDate) && oldDate == expense.Date)
        {
            WriteRow(row.Value, expense);
            AfterRowsChanged();
        }
        else
        {
            _ws.Row(row.Value).Delete();
            Insert(expense);
        }

        return true;
    }

    public bool Delete(Guid id)
    {
        var row = FindRow(id);
        if (row is null)
            return false;

        _ws.Row(row.Value).Delete();
        AfterRowsChanged();
        return true;
    }

    public int RenameFamilyMember(string oldName, string newName) => RenameValue(Col.FamilyMember, oldName, newName);

    public int RenameCategory(string oldName, string newName) => RenameValue(Col.Category, oldName, newName);

    /// <summary>
    /// Replaces <paramref name="oldName"/> with <paramref name="newName"/> in one column on every
    /// row (ignoring case and surrounding spaces), including rows that otherwise have problems.
    /// </summary>
    private int RenameValue(Col col, string oldName, string newName)
    {
        var target = oldName.Trim();
        var count = 0;
        foreach (var row in DataRowNumbers())
        {
            var cell = Cell(row, col);
            if (string.Equals(ReadText(cell), target, StringComparison.CurrentCultureIgnoreCase))
            {
                cell.Value = newName.Trim();
                count++;
            }
        }

        if (count > 0)
            IsModified = true;
        return count;
    }

    // ---- Layout -------------------------------------------------------------

    private void MapColumns()
    {
        var lastHeaderCol = _ws.Row(HeaderRow).LastCellUsed(XLCellsUsedOptions.Contents)?.Address.ColumnNumber ?? 0;

        for (var c = 1; c <= lastHeaderCol; c++)
        {
            var key = NormalizeHeader(_ws.Cell(HeaderRow, c).GetString());
            var spec = Columns.FirstOrDefault(s => s.Aliases.Contains(key));
            if (spec is not null && !_cols.ContainsKey(spec.Key))
                _cols[spec.Key] = c;
        }

        // Add any missing columns after the existing ones, in canonical order.
        var nextCol = lastHeaderCol + 1;
        foreach (var spec in Columns.Where(s => !_cols.ContainsKey(s.Key)))
        {
            _cols[spec.Key] = nextCol++;
            _ws.Cell(HeaderRow, _cols[spec.Key]).Value = spec.Header;
            FormatColumn(spec);
            IsModified = true;
        }

        if (IsModified)
            FormatHeader();
    }

    private void FormatColumn(ColumnSpec spec)
    {
        var col = _cols[spec.Key];
        _ws.Column(col).Width = spec.Width;

        // Date and amount formats are applied per row as rows are written, not
        // to a block of empty rows up front: pre-formatted rows count as "used"
        // in Excel, which sends Ctrl+End to row 5000 and shrinks the scrollbar.
        switch (spec.Key)
        {
            case Col.PaymentMethod:
                ApplyPaymentValidation();
                break;
            case Col.Id:
                // Used by the app to find rows again; nothing for people to edit.
                _ws.Column(col).Hide();
                break;
        }
    }

    private void FormatHeader()
    {
        var lastCol = _cols.Values.Max();
        var header = _ws.Range(HeaderRow, 1, HeaderRow, lastCol);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");
        _ws.SheetView.FreezeRows(HeaderRow);
        RefreshAutoFilter();
    }

    /// <summary>
    /// Gives the whole Payment Method column a Cash/Bank/Credit Card dropdown.
    /// Re-applied after rows move, because inserting rows shifts Excel's
    /// validation range down and would leave the top rows without it.
    /// </summary>
    private void ApplyPaymentValidation()
    {
        var col = _cols[Col.PaymentMethod];
        _ws.DataValidations.Delete(dv => dv.Ranges.Any(r =>
            r.RangeAddress.FirstAddress.ColumnNumber <= col && r.RangeAddress.LastAddress.ColumnNumber >= col));

        var lastRow = LastDataRow() + DropdownRows;
        var validation = _ws.Range(FirstDataRow, col, lastRow, col).CreateDataValidation();
        validation.List("\"" + string.Join(",", PaymentMethodNames.All.Select(m => m.ToDisplayName())) + "\"", true);
        validation.ErrorTitle = "Payment method";
        validation.ErrorMessage = "Choose Cash, Bank or Credit Card.";
    }

    private void AfterRowsChanged()
    {
        IsModified = true;
        ApplyPaymentValidation();
        RefreshAutoFilter();
    }

    private int LastColumn() => _cols.Values.Max();

    private void RefreshAutoFilter()
    {
        var lastRow = Math.Max(LastDataRow(), HeaderRow);
        _ws.Range(HeaderRow, 1, lastRow, _cols.Values.Max()).SetAutoFilter();
    }

    private static string NormalizeHeader(string text) =>
        new string(text.Where(char.IsLetter).ToArray()).ToLowerInvariant();

    // ---- Rows ---------------------------------------------------------------

    private IXLCell Cell(int row, Col col) => _ws.Cell(row, _cols[col]);

    private int LastDataRow() =>
        Math.Max(_ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? HeaderRow, HeaderRow);

    private IEnumerable<int> DataRowNumbers()
    {
        var last = LastDataRow();
        for (var row = FirstDataRow; row <= last; row++)
            yield return row;
    }

    private bool IsBlankRow(int row) =>
        _cols.Where(kv => kv.Key != Col.Id).All(kv => _ws.Cell(row, kv.Value).Value.IsBlank);

    private int? FindRow(Guid id)
    {
        var text = id.ToString();
        foreach (var row in DataRowNumbers())
        {
            if (string.Equals(ReadText(Cell(row, Col.Id)), text, StringComparison.OrdinalIgnoreCase))
                return row;
        }

        return null;
    }

    // First row whose date is later than `date`, so same-day expenses keep the order they were added.
    private int FindInsertRow(DateOnly date)
    {
        foreach (var row in DataRowNumbers())
        {
            if (TryReadDate(Cell(row, Col.Date), out var rowDate) && rowDate > date)
                return row;
        }

        return LastDataRow() + 1;
    }

    private void WriteRow(int row, Expense e)
    {
        var date = Cell(row, Col.Date);
        date.Value = e.Date.ToDateTime(TimeOnly.MinValue);
        date.Style.DateFormat.Format = DateFormat;

        Cell(row, Col.FamilyMember).Value = e.FamilyMember;
        Cell(row, Col.Category).Value = e.Category;

        var amount = Cell(row, Col.Amount);
        amount.Value = e.Amount;
        amount.Style.NumberFormat.Format = AmountFormat;

        Cell(row, Col.Note).Value = e.Note;
        Cell(row, Col.PaymentMethod).Value = e.PaymentMethod.ToDisplayName();
        Cell(row, Col.Id).Value = e.Id.ToString();
    }

    private bool TryReadRow(int row, out Expense expense, out string error)
    {
        expense = null!;

        if (!TryReadDate(Cell(row, Col.Date), out var date))
        {
            error = $"\"{Cell(row, Col.Date).GetFormattedString()}\" isn't a date.";
            return false;
        }

        if (!_month.Contains(date))
        {
            error = $"{date:d MMM yyyy} belongs in a different month's file.";
            return false;
        }

        if (!TryReadAmount(Cell(row, Col.Amount), out var amount))
        {
            error = $"\"{Cell(row, Col.Amount).GetFormattedString()}\" isn't a valid amount.";
            return false;
        }

        if (amount <= 0)
        {
            error = "Amount must be more than zero.";
            return false;
        }

        var paymentText = ReadText(Cell(row, Col.PaymentMethod));
        if (!PaymentMethodNames.TryParse(paymentText, out var payment))
        {
            error = paymentText.Length == 0
                ? "Payment method is missing."
                : $"\"{paymentText}\" isn't a payment method (use Cash, Bank or Credit Card).";
            return false;
        }

        Guid.TryParse(ReadText(Cell(row, Col.Id)), out var id);

        expense = new Expense
        {
            Id = id,
            Date = date,
            FamilyMember = ReadText(Cell(row, Col.FamilyMember)),
            Category = ReadText(Cell(row, Col.Category)),
            Amount = amount,
            Note = ReadText(Cell(row, Col.Note)),
            PaymentMethod = payment,
        };
        error = string.Empty;
        return true;
    }

    private static string ReadText(IXLCell cell) => cell.Value.IsBlank ? string.Empty : cell.GetFormattedString().Trim();

    internal static bool TryReadDate(IXLCell cell, out DateOnly date)
    {
        var value = cell.Value;
        date = default;

        if (value.IsDateTime)
        {
            date = DateOnly.FromDateTime(value.GetDateTime());
            return true;
        }

        // A date typed into a cell without a date format arrives as its serial number.
        if (value.IsNumber)
        {
            var serial = value.GetNumber();
            if (serial is >= 1 and < 2958466)
            {
                date = DateOnly.FromDateTime(DateTime.FromOADate(serial));
                return true;
            }

            return false;
        }

        if (value.IsText)
        {
            return DateOnly.TryParseExact(value.GetText().Trim(), TextDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out date);
        }

        return false;
    }

    internal static bool TryReadAmount(IXLCell cell, out decimal amount)
    {
        var value = cell.Value;
        amount = 0;

        if (value.IsNumber)
        {
            amount = Math.Round((decimal)value.GetNumber(), 2, MidpointRounding.AwayFromZero);
            return true;
        }

        if (value.IsText)
        {
            // Accept things like "Rs 1,200", "₨1200.50" or "PKR 500".
            var text = value.GetText().ToLowerInvariant()
                .Replace("pkr", string.Empty)
                .Replace("rs.", string.Empty)
                .Replace("rs", string.Empty)
                .Replace("₨", string.Empty)
                .Replace(",", string.Empty)
                .Trim();
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            {
                amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
                return true;
            }
        }

        return false;
    }
}
