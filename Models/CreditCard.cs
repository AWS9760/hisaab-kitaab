namespace HisaabKitaab.Models;

/// <summary>
/// The card's fixed details, kept in settings rather than in each month's workbook.
/// </summary>
public class CardSettings
{
    public string Name { get; set; } = "Credit card";

    /// <summary>
    /// Null when not set.
    /// </summary>
    public decimal? Limit { get; set; }

    /// <summary>
    /// Day of the month the bill is due (1-31). Null when not set.
    /// Months shorter than this use their last day.
    /// </summary>
    public int? DueDay { get; set; }

    /// <summary>
    /// The first due date on or after <paramref name="from"/>.
    /// </summary>
    public DateOnly? NextDueDate(DateOnly from)
    {
        if (DueDay is not { } day)
            return null;

        var thisMonth = DueIn(YearMonth.Of(from), day);
        return thisMonth >= from ? thisMonth : DueIn(YearMonth.Of(from).AddMonths(1), day);
    }

    /// <summary>
    /// When a month's card spending is due: the due day in the following month.
    /// </summary>
    public DateOnly? DueDateFor(YearMonth month) =>
        DueDay is { } day ? DueIn(month.AddMonths(1), day) : null;

    private static DateOnly DueIn(YearMonth month, int day) =>
        new(month.Year, month.Month, Math.Min(day, month.LastDay.Day));
}

/// <summary>
/// Money paid towards the card bill.
/// </summary>
public record CardRepayment
{
    public Guid Id { get; init; }

    public DateOnly Date { get; init; }

    public decimal Amount { get; init; }

    /// <summary>
    /// Where the money came from; it comes off that balance on Bank &amp; Cash.
    /// </summary>
    public Account PaidFrom { get; init; } = Account.Bank;

    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Everything read from one month's Credit Card sheet.
/// </summary>
/// <param name="ManualOpening">Opening outstanding the user typed in, or null to carry forward.</param>
public record CardSheetData(
    YearMonth Month,
    bool FileExists,
    decimal? ManualOpening,
    IReadOnlyList<CardRepayment> Repayments,
    IReadOnlyList<SheetProblem> Problems,
    string? Error = null)
{
    /// <summary>
    /// The opening figure currently written in the sheet, typed or carried.
    /// </summary>
    public decimal? StoredOpening { get; init; }

    public static CardSheetData Empty(YearMonth month, bool fileExists = false, string? error = null) =>
        new(month, fileExists, null, Array.Empty<CardRepayment>(), Array.Empty<SheetProblem>(), error);
}

public static class AccountNames
{
    public static string ToDisplayName(this Account account) => account == Account.Bank ? "Bank" : "Cash";

    public static bool TryParse(string? text, out Account account)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "bank" or "bank transfer" or "online" or "transfer":
                account = Account.Bank;
                return true;
            case "cash":
                account = Account.Cash;
                return true;
            default:
                account = default;
                return false;
        }
    }
}
