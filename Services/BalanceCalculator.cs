using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

public enum LedgerLineKind
{
    /// <summary>A withdrawal, deposit or income from the Bank &amp; Cash log.</summary>
    Entry,

    /// <summary>A Cash or Bank expense from the Expenses sheet.</summary>
    Expense,

    /// <summary>A credit card bill paid from the bank or from cash.</summary>
    CardRepayment,

    /// <summary>Zakat given from the bank or in cash (from the zakat workbook).</summary>
    Zakat,
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
    Expense? Expense = null,
    CardRepayment? Repayment = null,
    ZakatEntry? Zakat = null);

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
    IReadOnlyList<LedgerLine> Ledger,
    decimal CardRepaymentsBank = 0,
    decimal CardRepaymentsCash = 0,
    decimal ZakatBank = 0,
    decimal ZakatCash = 0)
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
/// touch neither (they're owed on the card until repaid). Card repayments and
/// zakat given come out of whichever they were paid from.
/// </summary>
public static class BalanceCalculator
{
    public static MonthBalances Calculate(
        YearMonth month,
        decimal openingBank, bool bankIsManual,
        decimal openingCash, bool cashIsManual,
        IReadOnlyList<BankCashEntry> entries,
        IReadOnlyList<Expense> expenses,
        IReadOnlyList<CardRepayment>? repayments = null,
        IReadOnlyList<ZakatEntry>? zakat = null)
    {
        repayments ??= Array.Empty<CardRepayment>();
        var zakatGiven = (zakat ?? Array.Empty<ZakatEntry>())
            .Where(z => z.Type == ZakatEntryType.Given && z.PaidFrom is not null && month.Contains(z.Date))
            .ToList();

        // Timeline: by date; within a day, logged entries (e.g. the morning's
        // withdrawal) come first, then card repayments, zakat and expenses, each in file order.
        var movements = entries
            .Select((e, i) => new Movement(e.Date, 0, i, e.Type.BankChange(e.Amount), e.Type.CashChange(e.Amount)) { Entry = e })
            .Concat(repayments
                .Select((r, i) => new Movement(r.Date, 1, i,
                    r.PaidFrom == Account.Bank ? -r.Amount : 0m,
                    r.PaidFrom == Account.Cash ? -r.Amount : 0m) { Repayment = r }))
            .Concat(zakatGiven
                .Select((z, i) => new Movement(z.Date, 2, i,
                    z.PaidFrom == Account.Bank ? -z.Amount : 0m,
                    z.PaidFrom == Account.Cash ? -z.Amount : 0m) { Zakat = z }))
            .Concat(expenses
                .Where(x => x.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Bank)
                .Select((x, i) => new Movement(x.Date, 3, i,
                    x.PaymentMethod == PaymentMethod.Bank ? -x.Amount : 0m,
                    x.PaymentMethod == PaymentMethod.Cash ? -x.Amount : 0m) { Expense = x }))
            .OrderBy(m => m.Date).ThenBy(m => m.Order).ThenBy(m => m.Index);

        var ledger = new List<LedgerLine>();
        var (bank, cash) = (openingBank, openingCash);
        foreach (var m in movements)
        {
            bank += m.Bank;
            cash += m.Cash;
            var kind = m.Entry is not null ? LedgerLineKind.Entry
                : m.Repayment is not null ? LedgerLineKind.CardRepayment
                : m.Zakat is not null ? LedgerLineKind.Zakat
                : LedgerLineKind.Expense;
            ledger.Add(new LedgerLine(m.Date, kind, m.Bank, m.Cash, bank, cash, m.Entry, m.Expense, m.Repayment, m.Zakat));
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
            ledger,
            CardRepaymentsBank: repayments.Where(r => r.PaidFrom == Account.Bank).Sum(r => r.Amount),
            CardRepaymentsCash: repayments.Where(r => r.PaidFrom == Account.Cash).Sum(r => r.Amount),
            ZakatBank: zakatGiven.Where(z => z.PaidFrom == Account.Bank).Sum(z => z.Amount),
            ZakatCash: zakatGiven.Where(z => z.PaidFrom == Account.Cash).Sum(z => z.Amount));
    }

    private sealed record Movement(DateOnly Date, int Order, int Index, decimal Bank, decimal Cash)
    {
        public BankCashEntry? Entry { get; init; }

        public Expense? Expense { get; init; }

        public CardRepayment? Repayment { get; init; }

        public ZakatEntry? Zakat { get; init; }
    }
}
