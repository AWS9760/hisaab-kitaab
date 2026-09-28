using ClosedXML.Excel;

namespace HisaabKitaab.Services.Excel;

internal static class SheetHelpers
{
    /// <summary>
    /// Gives a column an in-cell dropdown from <paramref name="firstRow"/> to
    /// <paramref name="lastRow"/>, replacing any validation already on it.
    /// Called on every save because inserting rows shifts Excel's validation
    /// range and would leave the top rows without it.
    /// </summary>
    public static void ApplyListValidation(IXLWorksheet ws, int col, int firstRow, int lastRow,
        IEnumerable<string> items, string title, string message)
    {
        ws.DataValidations.Delete(dv => dv.Ranges.Any(r =>
            r.RangeAddress.FirstAddress.ColumnNumber <= col && r.RangeAddress.LastAddress.ColumnNumber >= col));

        var validation = ws.Range(firstRow, col, lastRow, col).CreateDataValidation();
        validation.List("\"" + string.Join(",", items) + "\"", true);
        validation.ErrorTitle = title;
        validation.ErrorMessage = message;
    }
}
