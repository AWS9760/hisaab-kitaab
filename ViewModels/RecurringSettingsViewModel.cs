using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// A family member as offered in a dropdown; Id null means nobody in particular.
/// </summary>
public record MemberOption(Guid? Id, string Name)
{
    public static readonly MemberOption Nobody = new(null, "(no member)");
}

/// <summary>
/// One recurring expense in the Settings list.
/// </summary>
public partial class RecurringItemViewModel : ViewModelBase
{
    private readonly RecurringSettingsViewModel _owner;
    private bool _loading;

    /// <summary>
    /// On = added every month; off = paused.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    private bool _isActive;

    public RecurringItemViewModel(RecurringSettingsViewModel owner, RecurringExpense item, CategoryOption? category,
        (string Category, string Member) names)
    {
        _owner = owner;
        Item = item;
        Icon = category?.Icon ?? CategoryStyles.DefaultIcon;
        Color = category?.Color ?? CategoryStyles.DefaultColor;
        var who = string.IsNullOrEmpty(names.Member) ? string.Empty : $" · {names.Member}";
        var what = string.IsNullOrEmpty(names.Category) ? string.Empty : $"{names.Category}{who} · ";
        Details = $"{what}{PaymentOption.For(item.PaymentMethod).Name} · {Ordinal(item.DayOfMonth)} of each month";
        NextText = $"Next: {item.NextDate.ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture)}";

        _loading = true;
        IsActive = !item.IsPaused;
        _loading = false;
    }

    public RecurringExpense Item { get; }

    public string Name => Item.Name;

    public string AmountText => Pkr.Format(Item.Amount);

    public string Icon { get; }

    public string Color { get; }

    public string Details { get; }

    public string NextText { get; }

    public bool IsPaused => !IsActive;

    partial void OnIsActiveChanged(bool value)
    {
        if (!_loading)
            _owner.SetPaused(this, !value);
    }

    [RelayCommand]
    private void Edit() => _owner.BeginEdit(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    public static string Ordinal(int day) => day switch
    {
        1 or 21 or 31 => $"{day}st",
        2 or 22 => $"{day}nd",
        3 or 23 => $"{day}rd",
        _ => $"{day}th",
    };
}

/// <summary>
/// The Recurring expenses section of Settings: rent, bills and subscriptions
/// that are added as expenses automatically each month.
/// </summary>
public partial class RecurringSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _clock;
    private readonly Func<Task>? _addDueNow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(SaveButtonText), nameof(CanIncludeThisMonth))]
    private RecurringExpense? _editing;

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private double _formAmount = double.NaN;

    [ObservableProperty]
    private CategoryOption? _formCategory;

    [ObservableProperty]
    private MemberOption _formMember = MemberOption.Nobody;

    [ObservableProperty]
    private PaymentOption _formPayment = PaymentOption.For(PaymentMethod.Bank);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludeThisMonth), nameof(IncludeThisMonthText))]
    private double _formDay = 1;

    [ObservableProperty]
    private bool _formIncludeThisMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private IReadOnlyList<CategoryOption> _categoryOptions = Array.Empty<CategoryOption>();

    [ObservableProperty]
    private IReadOnlyList<MemberOption> _memberOptions = new[] { MemberOption.Nobody };

    /// <param name="addDueNow">Adds anything that's already due, e.g. this month's rent just set up.</param>
    public RecurringSettingsViewModel(SettingsService settings, IDialogService dialogs, TimeProvider clock, Func<Task>? addDueNow = null)
    {
        _settings = settings;
        _dialogs = dialogs;
        _clock = clock;
        _addDueNow = addDueNow;
        Rebuild();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));

        _settings.RecurringChanged += (_, _) => Rebuild();
        _settings.CategoriesChanged += (_, _) => Rebuild();
        _settings.FamilyMembersChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<RecurringItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public static int MaxNameLength => SettingsService.MaxRecurringNameLength;

    public bool HasError => ErrorMessage is not null;

    public IReadOnlyList<PaymentOption> PaymentOptions => PaymentOption.All;

    public string FormTitle => Editing is null ? "New recurring expense" : "Edit recurring expense";

    public string SaveButtonText => Editing is null ? "Add" : "Save changes";

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private int Day => double.IsNaN(FormDay) ? 1 : (int)Math.Clamp(Math.Round(FormDay), 1, 31);

    /// <summary>
    /// A new item whose day has already passed this month may also be added for this month.
    /// </summary>
    public bool CanIncludeThisMonth => Editing is null && RecurringService.FirstDate(Day, Today, false) > Today;

    public string IncludeThisMonthText =>
        $"Also add it for this month ({new RecurringExpense { DayOfMonth = Day }.DateIn(YearMonth.Of(Today)).ToString("d MMM", CultureInfo.InvariantCulture)})";

    [RelayCommand]
    private void OpenNewForm()
    {
        Editing = null;
        FormName = string.Empty;
        FormAmount = double.NaN;
        FormCategory = CategoryOptions.FirstOrDefault();
        FormMember = MemberOption.Nobody;
        FormPayment = PaymentOption.For(PaymentMethod.Bank);
        FormDay = 1;
        FormIncludeThisMonth = false;
        ErrorMessage = null;
        IsFormOpen = true;
    }

    internal void BeginEdit(RecurringItemViewModel row)
    {
        var item = row.Item;
        Editing = item;
        FormName = item.Name;
        FormAmount = (double)item.Amount;
        FormCategory = CategoryOptions.FirstOrDefault(c => c.Id == item.CategoryId)
                       ?? CategoryOptions.FirstOrDefault(c => c.Name == item.CategoryName);
        FormMember = MemberOptions.FirstOrDefault(m => m.Id is not null && m.Id == item.MemberId) ?? MemberOption.Nobody;
        FormPayment = PaymentOption.For(item.PaymentMethod);
        FormDay = item.DayOfMonth;
        FormIncludeThisMonth = false;
        ErrorMessage = null;
        IsFormOpen = true;
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        Editing = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveFormAsync()
    {
        var amount = double.IsNaN(FormAmount) ? 0 : Math.Round((decimal)Math.Min(FormAmount, 1e11), 2);
        var item = new RecurringExpense
        {
            Id = Editing?.Id ?? Guid.NewGuid(),
            Name = FormName,
            Amount = amount,
            CategoryId = FormCategory?.Id,
            CategoryName = FormCategory?.Name ?? string.Empty,
            MemberId = FormMember.Id,
            MemberName = FormMember.Id is null ? string.Empty : FormMember.Name,
            PaymentMethod = FormPayment.Method,
            DayOfMonth = Day,
            IsPaused = Editing?.IsPaused ?? false,
            LastAddedFor = Editing?.LastAddedFor,
            StartsOn = Editing?.StartsOn ?? RecurringService.FirstDate(Day, Today, FormIncludeThisMonth && CanIncludeThisMonth),
        };

        if (_settings.ValidateRecurring(item) is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        if (!SettingsSave.TryAny(() => _settings.SaveRecurring(item), out var error))
        {
            ErrorMessage = error;
            return;
        }

        IsFormOpen = false;
        Editing = null;
        ErrorMessage = null;

        if (item.NextDate <= Today && _addDueNow is not null)
            await _addDueNow();
    }

    internal void SetPaused(RecurringItemViewModel row, bool paused)
    {
        SettingsSave.TryAny(() => _settings.SetRecurringPaused(row.Item.Id, paused, Today), out var error);
        ErrorMessage = error;
    }

    internal async Task RemoveAsync(RecurringItemViewModel row)
    {
        if (!await _dialogs.ConfirmAsync($"Stop \"{row.Name}\"?",
                "It won't be added again. Expenses it has already added stay where they are.", "Remove"))
            return;

        SettingsSave.TryAny(() => _settings.RemoveRecurring(row.Item.Id), out var error);
        ErrorMessage = error;
    }

    private void Rebuild()
    {
        CategoryOptions = _settings.Categories.Select(c => new CategoryOption(c.Name, c.Icon, c.Color, c.Id)).ToList();
        MemberOptions = new[] { MemberOption.Nobody }
            .Concat(_settings.FamilyMembers.Select(m => new MemberOption(m.Id, m.Name)))
            .ToList();
        RefreshItems();
    }

    /// <summary>
    /// Rebuilds the list, e.g. so "Next" dates move on after expenses were added.
    /// </summary>
    public void RefreshItems()
    {
        Items.Clear();
        foreach (var item in _settings.Recurring.OrderBy(r => r.DayOfMonth).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var names = _settings.NamesFor(item);
            var category = _settings.FindCategory(names.Category) is { } c ? new CategoryOption(c.Name, c.Icon, c.Color, c.Id) : null;
            Items.Add(new RecurringItemViewModel(this, item, category, names));
        }
    }
}
