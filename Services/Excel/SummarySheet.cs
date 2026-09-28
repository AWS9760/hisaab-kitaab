using ClosedXML.Excel;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services.Excel;

/// <summary>
/// The "Summary" worksheet: income, spending, what's left, balances, and
/// spending per family member and per category. It's rebuilt from scratch on
/// every save (the rows depend on who spent what), with live formulas over
/// the other sheets so it stays right if they're edited in Excel.
/// </summary>
internal static class SummarySheet
{
    public const string SheetName = "Summary";

    // Identifies a Summary sheet made by the app, as opposed to one the user made.
    private const string Marker = "Built by Hisaab Kitaab";

    private const int TableHeaderRow = 13;

    public static void Rebuild(XLWorkbook workbook, YearMonth month, ExpensesSheet expenses, BankCashSheet bank,
        CreditCardSheet card, IReadOnlyList<Expense> monthExpenses)
    {
        var ws = GetOrCreate(workbook);
        ws.Clear();

        ws.Cell(1, 1).Value = $"Summary · {month.DisplayName}";
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = $"{Marker} each time it saves this workbook, so changes here are replaced. " +
                              "The figures are formulas over the other sheets.";
        ws.Cell(2, 1).Style.Font.Italic = true;
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        var exp = $"'{ExpensesSheet.SheetName}'!";
        string ExpRange(int col) => $"{exp}${Letter(col)}$2:${Letter(col)}$20000";

        Label(ws, 4, "Income (from Bank & Cash)");
        Amount(ws, 4, bank.IncomeFormula());
        Label(ws, 5, "Spent (all expenses)");
        Amount(ws, 5, $"SUM({ExpRange(expenses.AmountColumn)})");
        Label(ws, 6, "Remaining (saved)");
        Amount(ws, 6, "B4-B5");
        ws.Range(6, 1, 6, 2).Style.Font.Bold = true;
        Label(ws, 7, "Saved, as a share of income");
        ws.Cell(7, 2).FormulaA1 = "IF(B4>0,B6/B4,\"\")";
        ws.Cell(7, 2).Style.NumberFormat.Format = "0%";

        Label(ws, 9, "Bank balance at month end");
        Amount(ws, 9, bank.ClosingCell(Account.Bank));
        Label(ws, 10, "Cash in hand at month end");
        Amount(ws, 10, bank.ClosingCell(Account.Cash));
        Label(ws, 11, "Owed on credit card");
        Amount(ws, 11, card.OwedCell());

        // Two tables side by side: A-C by member, E-G by category.
        var byMember = DashboardData.Group(monthExpenses, e => e.FamilyMember);
        var byCategory = DashboardData.Group(monthExpenses, e => e.Category);
        WriteTable(ws, 1, "Family Member", "(no member)", byMember, ExpRange(expenses.MemberColumn), ExpRange(expenses.AmountColumn));
        WriteTable(ws, 5, "Category", "(no category)", byCategory, ExpRange(expenses.CategoryColumn), ExpRange(expenses.AmountColumn));

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 10;
        ws.Column(4).Width = 4;
        ws.Column(5).Width = 24;
        ws.Column(6).Width = 18;
        ws.Column(7).Width = 10;
    }

    private static IXLWorksheet GetOrCreate(XLWorkbook workbook)
    {
        if (workbook.TryGetWorksheet(SheetName, out var existing))
        {
            if (existing.Cell(2, 1).GetString().StartsWith(Marker, StringComparison.Ordinal))
                return existing;

            // The user's own sheet called "Summary": keep it under another name.
            existing.Name = UniqueName(workbook, "Summary (old)");
        }

        var position = workbook.TryGetWorksheet(ExpensesSheet.SheetName, out var expenses) ? expenses.Position + 1 : 1;
        return workbook.AddWorksheet(SheetName, position);
    }

    private static void WriteTable(IXLWorksheet ws, int firstCol, string heading, string noneLabel,
        IReadOnlyList<SpendingShare> rows, string criteriaRange, string amountRange)
    {
        ws.Cell(TableHeaderRow, firstCol).Value = heading;
        ws.Cell(TableHeaderRow, firstCol + 1).Value = "Spent";
        ws.Cell(TableHeaderRow, firstCol + 2).Value = "Share";
        var header = ws.Range(TableHeaderRow, firstCol, TableHeaderRow, firstCol + 2);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");

        for (var i = 0; i < rows.Count; i++)
        {
            var row = TableHeaderRow + 1 + i;
            var name = rows[i].Name;
            ws.Cell(row, firstCol).Value = name.Length == 0 ? noneLabel : name;

            var amount = ws.Cell(row, firstCol + 1);
            amount.FormulaA1 = $"SUMIFS({amountRange},{criteriaRange},{Criteria(name)})";
            amount.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;

            var share = ws.Cell(row, firstCol + 2);
            share.FormulaA1 = $"IF($B$5>0,{Letter(firstCol + 1)}{row}/$B$5,\"\")";
            share.Style.NumberFormat.Format = "0%";
        }
    }

    /// <summary>
    /// A SUMIFS criterion matching exactly this text (or blank cells for ""),
    /// with wildcards escaped so names like "Misc*" aren't treated as patterns.
    /// </summary>
    internal static string Criteria(string name)
    {
        if (name.Length == 0)
            return "\"\"";

        var escaped = name.Replace("~", "~~").Replace("*", "~*").Replace("?", "~?").Replace("\"", "\"\"");
        return $"\"={escaped}\"";
    }

    private static void Label(IXLWorksheet ws, int row, string text) => ws.Cell(row, 1).Value = text;

    private static void Amount(IXLWorksheet ws, int row, string formula)
    {
        var cell = ws.Cell(row, 2);
        cell.FormulaA1 = formula;
        cell.Style.NumberFormat.Format = ExpensesSheet.AmountFormat;
    }

    private static string Letter(int col) => XLHelper.GetColumnLetterFromNumber(col);

    private static string UniqueName(XLWorkbook workbook, string name)
    {
        var candidate = name;
        for (var i = 2; workbook.Worksheets.Contains(candidate); i++)
            candidate = $"{name} {i}";
        return candidate;
    }
}
