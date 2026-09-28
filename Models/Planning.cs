using System.Text.Json.Serialization;

namespace HisaabKitaab.Models;

public enum BudgetTarget
{
    /// <summary>All spending, whoever and whatever it was.</summary>
    Everything,
    Category,
    Member,
}

/// <summary>
/// A monthly spending limit. Applies to every month until changed.
/// Points at its category or member by id, so renames carry it along.
/// </summary>
public class Budget
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public BudgetTarget Target { get; set; }

    /// <summary>
    /// The category's or member's id; empty for <see cref="BudgetTarget.Everything"/>.
    /// </summary>
    public Guid TargetId { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>
/// A budget with its target's current name, for places that match on names
/// (the Expenses sheet stores names, not ids).
/// </summary>
public record ResolvedBudget(Guid Id, BudgetTarget Target, string Name, decimal Amount)
{
    public string Label => Target == BudgetTarget.Everything ? "All spending" : Name;

    public bool Covers(Expense e) => Target switch
    {
        BudgetTarget.Everything => true,
        BudgetTarget.Category => string.Equals(e.Category.Trim(), Name, StringComparison.CurrentCultureIgnoreCase),
        BudgetTarget.Member => string.Equals(e.FamilyMember.Trim(), Name, StringComparison.CurrentCultureIgnoreCase),
        _ => false,
    };
}

/// <summary>
/// An expense that repeats every month (rent, subscriptions, bills) and is
/// added automatically on its day.
/// </summary>
public class RecurringExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Written as the expense's note, e.g. "House rent".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>
    /// The category by id (follows renames), with its name at the time as a
    /// fallback if the category is later removed.
    /// </summary>
    public Guid? CategoryId { get; set; }

    public string CategoryName { get; set; } = string.Empty;

    public Guid? MemberId { get; set; }

    public string MemberName { get; set; } = string.Empty;

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Bank;

    /// <summary>
    /// 1-31. Shorter months use their last day.
    /// </summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>
    /// The first date it's added on.
    /// </summary>
    public DateOnly StartsOn { get; set; }

    /// <summary>
    /// The last date it was added for, or null if never. Next time it's added
    /// for the following month, so deleting an added expense doesn't bring it back.
    /// </summary>
    public DateOnly? LastAddedFor { get; set; }

    public bool IsPaused { get; set; }

    /// <summary>
    /// When it's due in a given month.
    /// </summary>
    public DateOnly DateIn(YearMonth month) =>
        new(month.Year, month.Month, Math.Min(DayOfMonth, month.LastDay.Day));

    /// <summary>
    /// The next date it will be added for.
    /// </summary>
    [JsonIgnore]
    public DateOnly NextDate => LastAddedFor is { } last ? DateIn(YearMonth.Of(last).AddMonths(1)) : StartsOn;

    public RecurringExpense Copy() => (RecurringExpense)MemberwiseClone();
}

/// <summary>
/// Which desktop notifications to show, and which have already been shown
/// (so none repeats, even after a restart).
/// </summary>
public class NotificationSettings
{
    public bool DailyReminder { get; set; } = true;

    public TimeOnly DailyReminderTime { get; set; } = new(21, 0);

    public bool CardDueReminder { get; set; } = true;

    public int CardDueDaysBefore { get; set; } = 3;

    public bool BudgetAlerts { get; set; } = true;

    // ---- What's been shown ----

    public DateOnly? LastDailyReminder { get; set; }

    /// <summary>
    /// The due date most recently reminded about.
    /// </summary>
    public DateOnly? LastCardDueReminder { get; set; }

    /// <summary>
    /// "budgetId:2026-09" for each budget already alerted as over in that month.
    /// </summary>
    public List<string> BudgetAlertsSent { get; set; } = new();
}
