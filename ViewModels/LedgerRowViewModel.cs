using System.Globalization;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// A type of Bank &amp; Cash entry as offered in the form.
/// </summary>
public record BankCashTypeOption(BankCashEntryType Type, string Name, string Icon, string Explanation)
{
    public static readonly IReadOnlyList<BankCashTypeOption> All = new BankCashTypeOption[]
    {
        new(BankCashEntryType.Withdrawal, "Withdrawal", "🏧", BankCashEntryType.Withdrawal.Explanation()),
        new(BankCashEntryType.Deposit, "Deposit", "🏦", BankCashEntryType.Deposit.Explanation()),
        new(BankCashEntryType.BankIncome, "Income to bank", "💼", BankCashEntryType.BankIncome.Explanation()),
        new(BankCashEntryType.CashIncome, "Income in cash", "💵", BankCashEntryType.CashIncome.Explanation()),
    };

    public static BankCashTypeOption For(BankCashEntryType type) => All.First(o => o.Type == type);
}

/// <summary>
/// One line of the Bank &amp; Cash timeline: a logged entry or a cash/bank expense.
/// </summary>
public class LedgerRowViewModel
{
    public LedgerRowViewModel(LedgerLine line, CategoryOption? category)
    {
        Line = line;

        if (line.Entry is { } entry)
        {
            var option = BankCashTypeOption.For(entry.Type);
            Icon = option.Icon;
            Color = "#0D9488";
            Title = option.Name;
            Detail = string.IsNullOrWhiteSpace(entry.Note) ? option.Explanation : entry.Note;
        }
        else if (line.Repayment is { } repayment)
        {
            Icon = "💳";
            Color = "#2563EB";
            Title = "Card repayment";
            Detail = string.IsNullOrWhiteSpace(repayment.Note) ? $"Paid from {repayment.PaidFrom.ToDisplayName().ToLowerInvariant()}" : repayment.Note;
        }
        else if (line.Zakat is { } zakat)
        {
            Icon = "🤲";
            Color = "#7C3AED";
            Title = "Zakat given";
            Detail = string.Join(" · ", new[] { zakat.Recipient, zakat.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        else
        {
            var expense = line.Expense!;
            Icon = category?.Icon ?? CategoryStyles.DefaultIcon;
            Color = category?.Color ?? CategoryStyles.DefaultColor;
            Title = string.IsNullOrWhiteSpace(expense.Category) ? "Expense" : expense.Category;
            Detail = string.Join(" · ", new[] { expense.FamilyMember, expense.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public LedgerLine Line { get; }

    public BankCashEntry? Entry => Line.Entry;

    /// <summary>
    /// Only logged entries can be edited here; expenses, card repayments and zakat have their own pages.
    /// </summary>
    public bool IsEntry => Line.Entry is not null;

    public string Icon { get; }

    public string Color { get; }

    public string Title { get; }

    public string Detail { get; }

    public DateOnly Date => Line.Date;

    public string DateText => Line.Date.ToString("ddd, d MMM", CultureInfo.InvariantCulture);

    public string BankChangeText => Pkr.FormatChange(Line.BankChange);

    public string CashChangeText => Pkr.FormatChange(Line.CashChange);

    public string BankBalanceText => Pkr.Format(Line.BankBalance);

    public string CashBalanceText => Pkr.Format(Line.CashBalance);

    public bool IsBankNegative => Line.BankBalance < 0;

    public bool IsCashNegative => Line.CashBalance < 0;
}
