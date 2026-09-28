using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// How whole-family and member budgets look (categories use their own icon
/// and colour). Colourful emoji, since grey ones vanish on the tinted circle.
/// </summary>
public static class BudgetLook
{
    public const string EverythingIcon = "💰";
    public const string EverythingColor = "#0D9488";
    public const string MemberIcon = "🧑";
    public const string MemberColor = "#7C3AED";
}

/// <summary>
/// Something a budget can be set for: all spending, a category or a member.
/// </summary>
public record BudgetTargetOption(BudgetTarget Target, Guid Id, string Label, string Icon, string Color);

/// <summary>
/// One budget in the Settings list.
/// </summary>
public partial class BudgetItemViewModel : ViewModelBase
{
    private readonly BudgetSettingsViewModel _owner;

    [ObservableProperty]
    private double _amountInput;

    public BudgetItemViewModel(BudgetSettingsViewModel owner, Budget budget, BudgetTargetOption target)
    {
        _owner = owner;
        Budget = budget;
        Target = target;
        _amountInput = (double)budget.Amount;
    }

    public Budget Budget { get; }

    public BudgetTargetOption Target { get; }

    public string AmountText => $"{Pkr.Format(Budget.Amount)} a month";

    [RelayCommand]
    private void SaveAmount() => _owner.ChangeAmount(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}

/// <summary>
/// The Budgets section of Settings: a monthly limit for all spending, for a
/// category, or for a family member.
/// </summary>
public partial class BudgetSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;

    [ObservableProperty]
    private IReadOnlyList<BudgetTargetOption> _targetOptions = Array.Empty<BudgetTargetOption>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private BudgetTargetOption? _newTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private double _newAmount = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public BudgetSettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        Rebuild();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));

        _settings.BudgetsChanged += (_, _) => Rebuild();
        _settings.CategoriesChanged += (_, _) => Rebuild();
        _settings.FamilyMembersChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<BudgetItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool HasError => ErrorMessage is not null;

    partial void OnNewAmountChanged(double value) => ErrorMessage = null;

    private bool CanAdd() => NewTarget is not null && !double.IsNaN(NewAmount) && NewAmount > 0;

    /// <summary>
    /// Adds a budget, or changes the amount if that target already has one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var target = NewTarget!;
        var amount = Math.Round((decimal)Math.Min(NewAmount, 1e11), 2);
        if (SettingsSave.TryAny(() => _settings.SetBudget(target.Target, target.Id, amount), out var error))
        {
            NewAmount = double.NaN;
            NewTarget = null;
        }

        ErrorMessage = error;
    }

    internal void ChangeAmount(BudgetItemViewModel item)
    {
        if (double.IsNaN(item.AmountInput) || item.AmountInput <= 0)
        {
            ErrorMessage = "A budget must be more than zero.";
            return;
        }

        var amount = Math.Round((decimal)Math.Min(item.AmountInput, 1e11), 2);
        SettingsSave.TryAny(() => _settings.SetBudget(item.Target.Target, item.Target.Id, amount), out var error);
        ErrorMessage = error;
    }

    internal void Remove(BudgetItemViewModel item)
    {
        SettingsSave.TryAny(() => _settings.RemoveBudget(item.Budget.Id), out var error);
        ErrorMessage = error;
    }

    private void Rebuild()
    {
        var options = new List<BudgetTargetOption> { new(BudgetTarget.Everything, Guid.Empty, "All spending", BudgetLook.EverythingIcon, BudgetLook.EverythingColor) };
        options.AddRange(_settings.Categories.Select(c => new BudgetTargetOption(BudgetTarget.Category, c.Id, c.Name, c.Icon, c.Color)));
        options.AddRange(_settings.FamilyMembers.Select(m => new BudgetTargetOption(BudgetTarget.Member, m.Id, m.Name, BudgetLook.MemberIcon, BudgetLook.MemberColor)));
        TargetOptions = options;

        Items.Clear();
        foreach (var budget in _settings.Budgets)
        {
            var target = options.FirstOrDefault(o => o.Target == budget.Target && o.Id == budget.TargetId);
            if (target is not null)
                Items.Add(new BudgetItemViewModel(this, budget, target));
        }
    }
}
