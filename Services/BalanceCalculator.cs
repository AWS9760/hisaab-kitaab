using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

public enum LedgerLineKind
{
    /// <summary>A withdrawal, deposit or income from the Bank &amp; Cash log.</summary>
    Entry,

    /// <summary>A Cash or Bank expense from the Expenses sheet.</summary>
    Expense,
}

/// <summary>
/// One movement of money in the combined bank/cash timeline, with the
/// balances straight after it.
/// </summary>
public record LedgerLine(
    DateOnly Date,
    LedgerLineKind Kind,
    decimal BankChange,
    decimal CashChange,
    decimal BankBalance,
    decimal CashBalance,
    BankCashEntry? Entry = null,
    Expense? Expense = null);

/// <summary>
/// A month's bank balance and cash in hand, and how they got there.
/// Changes are signed: money in is positive, money out negative.
/// </summary>
public record MonthBalances(
    YearMonth Month,
    decimal OpeningBank,
    decimal OpeningCash,
    bool BankOpeningIsManual,
    bool CashOpeningIsManual,
    decimal BankIncome,
    decimal CashIncome,
    decimal Withdrawals,
    decimal Deposits,
    decimal BankExpenses,
    decimal CashExpenses,
    IReadOnlyList<LedgerLine> Ledger)
{
    public decimal ClosingBank => Ledger.Count == 0 ? OpeningBank : Ledger[^1].BankBalance;

    public decimal ClosingCash => Ledger.Count == 0 ? OpeningCash : Ledger[^1].CashBalance;

    /// <summary>
    /// First day cash in hand went below zero, which usually means a
    /// withdrawal wasn't logged.
    /// </summary>
    public DateOnly? FirstNegativeCash => Ledger.FirstOrDefault(l => l.CashBalance < 0)?.Date;

    public DateOnly? FirstNegativeBank => Ledger.FirstOrDefault(l => l.BankBalance < 0)?.Date;
}

/// <summary>
/// Pure arithmetic for bank balance and cash in hand. Cash expenses come out
/// of cash in hand and Bank expenses out of the bank; credit card expenses
/// touch neither (they're owed on the card until repaid).
/// </summary>
public static class BalanceCalculator
{
    public static MonthBalances Calculate(
        YearMonth month,
        decimal openingBank, bool bankIsManual,
        decimal openingCash, bool cashIsManual,
        IReadOnlyList<BankCashEntry> entries,
        IReadOnlyList<Expense> expenses)
    {
        // Timeline: by date; within a day, logged entries (e.g. the morning's
        // withdrawal) come before that day's expenses, each in file order.
        var movements = entries
            .Select((e, i) => (e.Date, Order: 0, Index: i,
                Bank: e.Type.BankChange(e.Amount), Cash: e.Type.CashChange(e.Amount),
                Entry: (BankCashEntry?)e, Expense: (Expense?)null))
            .Concat(expenses
                .Where(x => x.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Bank)
                .Select((x, i) => (x.Date, Order: 1, Index: i,
                    Bank: x.PaymentMethod == PaymentMethod.Bank ? -x.Amount : 0m,
                    Cash: x.PaymentMethod == PaymentMethod.Cash ? -x.Amount : 0m,
                    Entry: (BankCashEntry?)null, Expense: (Expense?)x)))
            .OrderBy(m => m.Date).ThenBy(m => m.Order).ThenBy(m => m.Index);

        var ledger = new List<LedgerLine>();
        var (bank, cash) = (openingBank, openingCash);
        foreach (var m in movements)
        {
            bank += m.Bank;
            cash += m.Cash;
            ledger.Add(new LedgerLine(m.Date, m.Entry is null ? LedgerLineKind.Expense : LedgerLineKind.Entry,
                m.Bank, m.Cash, bank, cash, m.Entry, m.Expense));
        }

        decimal Sum(BankCashEntryType type) => entries.Where(e => e.Type == type).Sum(e => e.Amount);

        return new MonthBalances(
            month, openingBank, openingCash, bankIsManual, cashIsManual,
            BankIncome: Sum(BankCashEntryType.BankIncome),
            CashIncome: Sum(BankCashEntryType.CashIncome),
            Withdrawals: Sum(BankCashEntryType.Withdrawal),
            Deposits: Sum(BankCashEntryType.Deposit),
            BankExpenses: expenses.Where(x => x.PaymentMethod == PaymentMethod.Bank).Sum(x => x.Amount),
            CashExpenses: expenses.Where(x => x.PaymentMethod == PaymentMethod.Cash).Sum(x => x.Amount),
            ledger);
    }
}
