using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// An amount spent under one name (a category or a family member).
/// </summary>
public record SpendingShare(string Name, decimal Amount, decimal ShareOfTotal);

/// <summary>
/// Income and spending for one month, for the trend chart.
/// </summary>
public record MonthTotals(YearMonth Month, decimal Income, decimal Spent)
{
    public decimal Saved => Income - Spent;
}

/// <summary>
/// Everything the dashboard shows for one month.
/// </summary>
public record DashboardData(
    YearMonth Month,
    decimal Income,
    IReadOnlyList<Expense> Expenses,
    decimal BankBalance,
    decimal CashInHand,
    decimal CardOwed,
    IReadOnlyList<MonthTotals> Trend)
{
    public decimal Spent => Expenses.Sum(e => e.Amount);

    public decimal Saved => Income - Spent;

    /// <summary>
    /// Saved as a share of income, or null when there was no income.
    /// </summary>
    public decimal? SavingsRate => Income > 0 ? Saved / Income : null;

    public IReadOnlyList<SpendingShare> ByMember() => Group(Expenses, e => e.FamilyMember);

    /// <summary>
    /// Spending per category, optionally for one family member only
    /// (an empty string selects expenses with no member).
    /// </summary>
    public IReadOnlyList<SpendingShare> ByCategory(string? member = null) =>
        Group(ForMember(member), e => e.Category);

    public IEnumerable<Expense> ForMember(string? member) => member is null
        ? Expenses
        : Expenses.Where(e => string.Equals(e.FamilyMember.Trim(), member, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// Biggest first. Names are trimmed and compared ignoring case; the
    /// spelling of the first one seen is kept.
    /// </summary>
    public static IReadOnlyList<SpendingShare> Group(IEnumerable<Expense> expenses, Func<Expense, string> key)
    {
        var list = expenses.ToList();
        var total = list.Sum(e => e.Amount);
        return list
            .GroupBy(e => key(e).Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new SpendingShare(g.Key, g.Sum(e => e.Amount), total == 0 ? 0 : g.Sum(e => e.Amount) / total))
            .OrderByDescending(s => s.Amount)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Puts together the dashboard from the other screens' data. Income is the
/// "Income to bank" and "Income in cash" entries on Bank &amp; Cash.
/// </summary>
public class DashboardService
{
    public const int TrendMonths = 6;

    private readonly WorkbookStore _store;
    private readonly BankCashService _bankCash;
    private readonly CreditCardService _card;

    public DashboardService(WorkbookStore store)
    {
        _store = store;
        _bankCash = new BankCashService(store);
        _card = new CreditCardService(store);
    }

    public WorkbookStore Store => _store;

    public DashboardData GetMonth(YearMonth month)
    {
        var balances = _bankCash.GetBalances(month);
        var card = _card.GetMonth(month);

        var trend = Enumerable.Range(0, TrendMonths)
            .Select(i => month.AddMonths(i - TrendMonths + 1))
            .Select(m => new MonthTotals(m, IncomeOf(m), _store.LoadMonth(m).Expenses.Sum(e => e.Amount)))
            .ToList();

        return new DashboardData(month, IncomeOf(month), _store.LoadMonth(month).Expenses,
            balances.ClosingBank, balances.ClosingCash, card.Outstanding, trend);
    }

    private decimal IncomeOf(YearMonth month) => _store.LoadBankCash(month).Entries
        .Where(e => e.Type is BankCashEntryType.BankIncome or BankCashEntryType.CashIncome)
        .Sum(e => e.Amount);
}
