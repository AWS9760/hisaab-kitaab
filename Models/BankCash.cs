namespace HisaabKitaab.Models;

/// <summary>
/// Kinds of money movement logged on the Bank &amp; Cash sheet. Expenses are
/// not logged here; they're taken from the Expenses sheet automatically.
/// </summary>
public enum BankCashEntryType
{
    /// <summary>Cash taken out of the bank: bank down, cash up.</summary>
    Withdrawal,

    /// <summary>Cash paid into the bank: cash down, bank up.</summary>
    Deposit,

    /// <summary>Money received into the bank (salary, transfer in).</summary>
    BankIncome,

    /// <summary>Money received as cash.</summary>
    CashIncome,
}

public enum Account
{
    Bank,
    Cash,
}

public static class BankCashEntryTypes
{
    public static readonly IReadOnlyList<BankCashEntryType> All = new[]
    {
        BankCashEntryType.Withdrawal, BankCashEntryType.Deposit,
        BankCashEntryType.BankIncome, BankCashEntryType.CashIncome,
    };

    /// <summary>
    /// The text written in the Type column of the sheet.
    /// </summary>
    public static string ToDisplayName(this BankCashEntryType type) => type switch
    {
        BankCashEntryType.Withdrawal => "Withdrawal",
        BankCashEntryType.Deposit => "Deposit",
        BankCashEntryType.BankIncome => "Income to bank",
        BankCashEntryType.CashIncome => "Income in cash",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static string Explanation(this BankCashEntryType type) => type switch
    {
        BankCashEntryType.Withdrawal => "Bank → cash",
        BankCashEntryType.Deposit => "Cash → bank",
        BankCashEntryType.BankIncome => "Salary or money received into the bank",
        BankCashEntryType.CashIncome => "Money received as cash",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// How much this entry changes the bank balance (positive = more money).
    /// </summary>
    public static decimal BankChange(this BankCashEntryType type, decimal amount) => type switch
    {
        BankCashEntryType.Withdrawal => -amount,
        BankCashEntryType.Deposit => amount,
        BankCashEntryType.BankIncome => amount,
        _ => 0,
    };

    /// <summary>
    /// How much this entry changes cash in hand (positive = more money).
    /// </summary>
    public static decimal CashChange(this BankCashEntryType type, decimal amount) => type switch
    {
        BankCashEntryType.Withdrawal => amount,
        BankCashEntryType.Deposit => -amount,
        BankCashEntryType.CashIncome => amount,
        _ => 0,
    };

    /// <summary>
    /// Accepts the display names plus common spellings typed by hand
    /// ("ATM", "salary", "income bank", …). Case and spaces are ignored.
    /// </summary>
    public static bool TryParse(string? text, out BankCashEntryType type)
    {
        var key = new string((text ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();
        switch (key)
        {
            case "withdrawal" or "withdraw" or "atm" or "cashwithdrawal":
                type = BankCashEntryType.Withdrawal;
                return true;
            case "deposit" or "cashdeposit":
                type = BankCashEntryType.Deposit;
                return true;
            case "incometobank" or "bankincome" or "incomebank" or "salary":
                type = BankCashEntryType.BankIncome;
                return true;
            case "incomeincash" or "cashincome" or "incomecash":
                type = BankCashEntryType.CashIncome;
                return true;
            default:
                type = default;
                return false;
        }
    }
}

/// <summary>
/// One row of the Bank &amp; Cash transaction log.
/// </summary>
public record BankCashEntry
{
    public Guid Id { get; init; }

    public DateOnly Date { get; init; }

    public BankCashEntryType Type { get; init; }

    /// <summary>
    /// Always positive; <see cref="Type"/> decides which way it moves money.
    /// </summary>
    public decimal Amount { get; init; }

    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Everything read from one month's Bank &amp; Cash sheet.
/// </summary>
/// <param name="ManualOpeningBank">Opening bank balance the user typed in, or null to carry forward.</param>
/// <param name="ManualOpeningCash">Opening cash in hand the user typed in, or null to carry forward.</param>
public record BankCashSheetData(
    YearMonth Month,
    bool FileExists,
    decimal? ManualOpeningBank,
    decimal? ManualOpeningCash,
    IReadOnlyList<BankCashEntry> Entries,
    IReadOnlyList<SheetProblem> Problems,
    string? Error = null)
{
    /// <summary>
    /// The opening balances currently written in the sheet, whether typed or
    /// carried forward. Used to tell when a carried-forward value is out of date.
    /// </summary>
    public decimal? StoredOpeningBank { get; init; }

    public decimal? StoredOpeningCash { get; init; }

    public static BankCashSheetData Empty(YearMonth month, bool fileExists = false, string? error = null) =>
        new(month, fileExists, null, null, Array.Empty<BankCashEntry>(), Array.Empty<SheetProblem>(), error);
}
