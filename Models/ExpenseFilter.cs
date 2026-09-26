using System.Globalization;

namespace HisaabKitaab.Models;

/// <summary>
/// A combination of conditions an expense must meet to be shown. Unset
/// conditions (null or empty) match everything; set ones must all match.
/// </summary>
public sealed record ExpenseFilter
{
    private string[]? _words;

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    /// <summary>
    /// Family member names to include. An empty string stands for "no member".
    /// </summary>
    public IReadOnlyCollection<string>? Members { get; init; }

    /// <summary>
    /// Category names to include. An empty string stands for "no category".
    /// </summary>
    public IReadOnlyCollection<string>? Categories { get; init; }

    public IReadOnlyCollection<PaymentMethod>? PaymentMethods { get; init; }

    public decimal? MinAmount { get; init; }

    public decimal? MaxAmount { get; init; }

    /// <summary>
    /// Free text. Every word must appear somewhere in the note, category,
    /// member, payment method or amount (ignoring case).
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// How many conditions besides the date range are set.
    /// </summary>
    public int ActiveFilterCount =>
        (Members is { Count: > 0 } ? 1 : 0)
        + (Categories is { Count: > 0 } ? 1 : 0)
        + (PaymentMethods is { Count: > 0 } ? 1 : 0)
        + (MinAmount is not null || MaxAmount is not null ? 1 : 0)
        + (Words.Length > 0 ? 1 : 0);

    private string[] Words => _words ??=
        (Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Matches(Expense e)
    {
        if (From is { } from && e.Date < from)
            return false;
        if (To is { } to && e.Date > to)
            return false;
        if (MinAmount is { } min && e.Amount < min)
            return false;
        if (MaxAmount is { } max && e.Amount > max)
            return false;
        if (Members is { Count: > 0 } && !Members.Contains(e.FamilyMember.Trim(), StringComparer.CurrentCultureIgnoreCase))
            return false;
        if (Categories is { Count: > 0 } && !Categories.Contains(e.Category.Trim(), StringComparer.CurrentCultureIgnoreCase))
            return false;
        if (PaymentMethods is { Count: > 0 } && !PaymentMethods.Contains(e.PaymentMethod))
            return false;

        if (Words.Length > 0)
        {
            var haystack = string.Join('\n',
                e.Note, e.Category, e.FamilyMember, e.PaymentMethod.ToDisplayName(),
                e.Amount.ToString("0.##", CultureInfo.InvariantCulture),
                e.Amount.ToString("#,##0.##", CultureInfo.InvariantCulture));
            foreach (var word in Words)
            {
                if (haystack.IndexOf(word, StringComparison.CurrentCultureIgnoreCase) < 0)
                    return false;
            }
        }

        return true;
    }
}
