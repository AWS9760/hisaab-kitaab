using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// One movement on the card: spending (owed goes up) or a repayment (owed goes down).
/// </summary>
public record CardLedgerLine(DateOnly Date, decimal Change, decimal Owed, Expense? Expense = null, CardRepayment? Repayment = null);

/// <summary>
/// A month on the card: what was owed at the start, spent, repaid, and owed at the end.
/// </summary>
public record CardMonth(
    YearMonth Month,
    decimal Opening,
    bool OpeningIsManual,
    decimal Spent,
    decimal Repaid,
    IReadOnlyList<CardLedgerLine> Ledger)
{
    public decimal Outstanding => Ledger.Count == 0 ? Opening : Ledger[^1].Owed;

    /// <summary>
    /// Outstanding as a share of <paramref name="limit"/> (0.25 = 25%), or null without a limit.
    /// </summary>
    public decimal? LimitUsed(decimal? limit) => limit is > 0 ? Outstanding / limit.Value : null;
}

public static class CardCalculator
{
    /// <summary>
    /// Credit card expenses add to what's owed; repayments take it off.
    /// Within a day, spending comes before repayments.
    /// </summary>
    public static CardMonth Calculate(YearMonth month, decimal opening, bool openingIsManual,
        IReadOnlyList<Expense> expenses, IReadOnlyList<CardRepayment> repayments)
    {
        var cardExpenses = expenses.Where(e => e.PaymentMethod == PaymentMethod.CreditCard).ToList();

        var movements = cardExpenses
            .Select((e, i) => (e.Date, Order: 0, Index: i, Change: e.Amount, Expense: (Expense?)e, Repayment: (CardRepayment?)null))
            .Concat(repayments.Select((r, i) => (r.Date, Order: 1, Index: i, Change: -r.Amount, Expense: (Expense?)null, Repayment: (CardRepayment?)r)))
            .OrderBy(m => m.Date).ThenBy(m => m.Order).ThenBy(m => m.Index);

        var ledger = new List<CardLedgerLine>();
        var owed = opening;
        foreach (var m in movements)
        {
            owed += m.Change;
            ledger.Add(new CardLedgerLine(m.Date, m.Change, owed, m.Expense, m.Repayment));
        }

        return new CardMonth(month, opening, openingIsManual,
            cardExpenses.Sum(e => e.Amount), repayments.Sum(r => r.Amount), ledger);
    }
}

/// <summary>
/// Works out what's owed on the card for any month, carrying each month's
/// outstanding forward as the next month's opening (unless typed in).
/// </summary>
public class CreditCardService
{
    private readonly WorkbookStore _store;

    public CreditCardService(WorkbookStore store)
    {
        _store = store;
    }

    public WorkbookStore Store => _store;

    public CardMonth GetMonth(YearMonth month)
    {
        var start = _store.Excel.GetExistingMonths().FirstOrDefault(m => m <= month);
        if (start == default || start > month)
            start = month;
        if (month.AddMonths(-BankCashService.MaxCarryMonths) > start)
            start = month.AddMonths(-BankCashService.MaxCarryMonths);

        decimal carried = 0;
        CardMonth? result = null;
        for (var m = start; m <= month; m = m.AddMonths(1))
        {
            var sheet = _store.LoadCard(m);
            result = CardCalculator.Calculate(m, sheet.ManualOpening ?? carried, sheet.ManualOpening is not null,
                _store.LoadMonth(m).Expenses, sheet.Repayments);
            carried = result.Outstanding;
        }

        return result!;
    }

    /// <summary>
    /// Keeps a workbook's carried-forward opening in step with last month's
    /// outstanding, for anyone opening it in Excel. Returns true if it wrote.
    /// </summary>
    public bool SyncCarriedOpening(CardMonth card)
    {
        if (card.OpeningIsManual || !_store.Excel.MonthFileExists(card.Month))
            return false;
        if (_store.LoadCard(card.Month).StoredOpening == card.Opening)
            return false;

        _store.Excel.SetCardOpening(card.Month, card.Opening, isManual: false);
        _store.Invalidate(card.Month);
        return true;
    }
}
