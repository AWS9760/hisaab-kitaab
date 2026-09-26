using System.Globalization;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// A category as offered in the expense form, with its display look.
/// <see cref="Id"/> is null for names found in a workbook that aren't
/// (or are no longer) set up in Settings.
/// </summary>
public record CategoryOption(string Name, string Icon, string Color, Guid? Id = null)
{
    public static CategoryOption Unknown(string name) =>
        new(name, CategoryStyles.DefaultIcon, CategoryStyles.DefaultColor);

    public bool IsKnown => Id is not null;
}

public record PaymentOption(PaymentMethod Method, string Name, string Icon)
{
    public static readonly IReadOnlyList<PaymentOption> All = new PaymentOption[]
    {
        new(PaymentMethod.Cash, "Cash", "💵"),
        new(PaymentMethod.Bank, "Bank", "🏦"),
        new(PaymentMethod.CreditCard, "Credit Card", "💳"),
    };

    public static PaymentOption For(PaymentMethod method) => All.First(o => o.Method == method);
}

/// <summary>
/// One expense as shown in the list.
/// </summary>
public class ExpenseRowViewModel
{
    public ExpenseRowViewModel(Expense expense, CategoryOption category)
    {
        Expense = expense;
        Category = category;
        Payment = PaymentOption.For(expense.PaymentMethod);
    }

    public Expense Expense { get; }

    public CategoryOption Category { get; }

    public PaymentOption Payment { get; }

    public YearMonth Month => YearMonth.Of(Expense.Date);

    // Plain properties for DataGrid sorting.
    public DateOnly Date => Expense.Date;

    public decimal Amount => Expense.Amount;

    public string CategoryName => string.IsNullOrEmpty(Expense.Category) ? "—" : Expense.Category;

    public string Member => string.IsNullOrEmpty(Expense.FamilyMember) ? "—" : Expense.FamilyMember;

    public string Note => Expense.Note;

    public string PaymentName => Payment.Name;

    public string DateText => Expense.Date.ToString("ddd, d MMM", CultureInfo.InvariantCulture);

    public string AmountText => Pkr.Format(Expense.Amount);
}
