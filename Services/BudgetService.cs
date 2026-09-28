using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

public enum BudgetLevel
{
    Fine,

    /// <summary>At or above <see cref="BudgetService.NearShare"/> of the budget.</summary>
    Near,

    /// <summary>More than the budget.</summary>
    Over,
}

/// <summary>
/// How a budget stands for one month.
/// </summary>
public record BudgetStatus(ResolvedBudget Budget, decimal Spent)
{
    public decimal Share => Budget.Amount == 0 ? 0 : Spent / Budget.Amount;

    public decimal Remaining => Budget.Amount - Spent;

    public BudgetLevel Level =>
        Spent > Budget.Amount ? BudgetLevel.Over
        : Share >= BudgetService.NearShare ? BudgetLevel.Near
        : BudgetLevel.Fine;
}

public class BudgetService
{
    /// <summary>
    /// Share of a budget at which it counts as nearly used up.
    /// </summary>
    public const decimal NearShare = 0.8m;

    private readonly SettingsService _settings;
    private readonly WorkbookStore _store;

    public BudgetService(SettingsService settings, WorkbookStore store)
    {
        _settings = settings;
        _store = store;
    }

    public IReadOnlyList<BudgetStatus> GetMonth(YearMonth month) => Calculate(_settings.ResolvedBudgets(), _store.LoadMonth(month).Expenses);

    /// <summary>
    /// Each budget against the expenses, most used first. Whole-family budgets come first.
    /// </summary>
    public static IReadOnlyList<BudgetStatus> Calculate(IEnumerable<ResolvedBudget> budgets, IReadOnlyList<Expense> expenses) =>
        budgets
            .Select(b => new BudgetStatus(b, expenses.Where(b.Covers).Sum(e => e.Amount)))
            .OrderBy(s => s.Budget.Target != BudgetTarget.Everything)
            .ThenByDescending(s => s.Share)
            .ThenBy(s => s.Budget.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
