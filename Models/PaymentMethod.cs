namespace HisaabKitaab.Models;

public enum PaymentMethod
{
    Cash,
    Bank,
    CreditCard,
}

public static class PaymentMethodNames
{
    public static readonly IReadOnlyList<PaymentMethod> All =
        new[] { PaymentMethod.Cash, PaymentMethod.Bank, PaymentMethod.CreditCard };

    /// <summary>
    /// The text written to (and shown in) the Excel file.
    /// </summary>
    public static string ToDisplayName(this PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Bank => "Bank",
        PaymentMethod.CreditCard => "Credit Card",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    /// <summary>
    /// Parses the display name, plus a few spellings people commonly type by
    /// hand in Excel ("card", "CC", "creditcard", …). Case and spaces are ignored.
    /// </summary>
    public static bool TryParse(string? text, out PaymentMethod method)
    {
        var key = new string((text ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();
        switch (key)
        {
            case "cash":
                method = PaymentMethod.Cash;
                return true;
            case "bank":
            case "banktransfer":
            case "debitcard":
            case "debit":
                method = PaymentMethod.Bank;
                return true;
            case "creditcard":
            case "credit":
            case "card":
            case "cc":
                method = PaymentMethod.CreditCard;
                return true;
            default:
                method = default;
                return false;
        }
    }
}
