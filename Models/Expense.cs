namespace HisaabKitaab.Models;

/// <summary>
/// One row of the Expenses sheet.
/// </summary>
public record Expense
{
    public Guid Id { get; init; }

    public DateOnly Date { get; init; }

    public string FamilyMember { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// Amount in PKR. Always positive.
    /// </summary>
    public decimal Amount { get; init; }

    public string Note { get; init; } = string.Empty;

    public PaymentMethod PaymentMethod { get; init; }
}
