using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// Search and filters. Everything here works on the expenses already in
/// memory, so changing a filter never touches the Excel files.
/// </summary>
public partial class ExpensesViewModel
{
    private const string NoneLabelMember = "(no member)";
    private const string NoneLabelCategory = "(no category)";

    // Set while choice lists are rebuilt, so ticking restored selections doesn't re-filter each time.
    private bool _rebuildingChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private IReadOnlyList<ExpenseRowViewModel> _rows = Array.Empty<ExpenseRowViewModel>();

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// NaN when empty.
    /// </summary>
    [ObservableProperty]
    private double _minAmount = double.NaN;

    [ObservableProperty]
    private double _maxAmount = double.NaN;

    [ObservableProperty]
    private bool _isFilterPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(FilterButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ClearFiltersCommand))]
    private int _activeFilterCount;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    [ObservableProperty]
    private string _totalText = Pkr.Format(0);

    [ObservableProperty]
    private string _countText = "No expenses";

    [ObservableProperty]
    private string _cashText = Pkr.Format(0);

    [ObservableProperty]
    private string _bankText = Pkr.Format(0);

    [ObservableProperty]
    private string _cardText = Pkr.Format(0);

    [ObservableProperty]
    private string _memberFilterText = "All members";

    [ObservableProperty]
    private string _categoryFilterText = "All categories";

    [ObservableProperty]
    private string _paymentFilterText = "Any payment method";

    public ObservableCollection<FilterChoiceViewModel> MemberChoices { get; } = new();

    public ObservableCollection<FilterChoiceViewModel> CategoryChoices { get; } = new();

    public IReadOnlyList<FilterChoiceViewModel> PaymentChoices { get; }

    public bool HasRows => Rows.Count > 0;

    public bool HasActiveFilters => ActiveFilterCount > 0;

    public string FilterButtonText => ActiveFilterCount == 0 ? "Filters" : $"Filters · {ActiveFilterCount}";

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnMinAmountChanged(double value) => ApplyFilters();

    partial void OnMaxAmountChanged(double value) => ApplyFilters();

    private void OnFilterChoiceChanged()
    {
        if (!_rebuildingChoices)
            ApplyFilters();
    }

    [RelayCommand(CanExecute = nameof(HasActiveFilters))]
    private void ClearFilters()
    {
        _rebuildingChoices = true;
        try
        {
            foreach (var choice in MemberChoices.Concat(CategoryChoices).Concat(PaymentChoices))
                choice.IsSelected = false;
            SearchText = string.Empty;
            MinAmount = double.NaN;
            MaxAmount = double.NaN;
        }
        finally
        {
            _rebuildingChoices = false;
        }

        ApplyFilters();
    }

    /// <summary>
    /// The current search and filter settings (the date range is applied when loading).
    /// </summary>
    public ExpenseFilter BuildFilter() => new()
    {
        Text = SearchText,
        Members = Selected(MemberChoices),
        Categories = Selected(CategoryChoices),
        PaymentMethods = PaymentChoices.Where(c => c.IsSelected).Select(c => Enum.Parse<PaymentMethod>(c.Value)).ToList(),
        MinAmount = ToAmount(MinAmount),
        MaxAmount = ToAmount(MaxAmount),
    };

    private void ApplyFilters()
    {
        var filter = BuildFilter();

        // Newest first. Sorted explicitly rather than trusting the file order,
        // since rows typed into Excel by hand may be out of order. Same-day
        // entries keep their file order reversed, so the latest added is on top.
        var matched = _loaded
            .Select((expense, index) => (expense, index))
            .Where(x => filter.Matches(x.expense))
            .OrderByDescending(x => x.expense.Date)
            .ThenByDescending(x => x.index)
            .Select(x => x.expense)
            .ToList();
        Rows = matched.Select(e => new ExpenseRowViewModel(e, LookUpCategory(e.Category))).ToList();

        var byMethod = matched.GroupBy(e => e.PaymentMethod).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        TotalText = Pkr.Format(matched.Sum(e => e.Amount));
        CashText = Pkr.Format(byMethod.GetValueOrDefault(PaymentMethod.Cash));
        BankText = Pkr.Format(byMethod.GetValueOrDefault(PaymentMethod.Bank));
        CardText = Pkr.Format(byMethod.GetValueOrDefault(PaymentMethod.CreditCard));

        ActiveFilterCount = filter.ActiveFilterCount;
        CountText = HasActiveFilters
            ? $"{matched.Count} of {Plural(_loaded.Count, "expense")}"
            : _loaded.Count == 0 ? "No expenses" : Plural(_loaded.Count, "expense");
        EmptyText = _loaded.Count == 0
            ? $"No expenses in {PeriodTitle} yet."
            : "No expenses match your search and filters.";

        MemberFilterText = Summarise(MemberChoices, "All members", "members");
        CategoryFilterText = Summarise(CategoryChoices, "All categories", "categories");
        PaymentFilterText = Summarise(PaymentChoices, "Any payment method", "payment methods");
    }

    /// <summary>
    /// Offers every member and category from Settings, plus any other names
    /// found in the loaded expenses (typed in Excel, or since removed), keeping
    /// whatever was ticked before.
    /// </summary>
    private void RebuildFilterChoices()
    {
        _rebuildingChoices = true;
        try
        {
            Rebuild(MemberChoices, _settings.FamilyMembers.Select(m => m.Name), _loaded.Select(e => e.FamilyMember), NoneLabelMember);
            Rebuild(CategoryChoices, _settings.Categories.Select(c => c.Name), _loaded.Select(e => e.Category), NoneLabelCategory);
        }
        finally
        {
            _rebuildingChoices = false;
        }
    }

    private void Rebuild(ObservableCollection<FilterChoiceViewModel> choices, IEnumerable<string> configured,
        IEnumerable<string> found, string noneLabel)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var wasSelected = new HashSet<string>(choices.Where(c => c.IsSelected).Select(c => c.Value), comparer);
        var foundList = found.Select(n => n.Trim()).ToList();

        var names = configured.ToList();
        names.AddRange(foundList.Where(n => n.Length > 0 && !names.Contains(n, comparer))
            .Distinct(comparer)
            .OrderBy(n => n, comparer));

        choices.Clear();
        foreach (var name in names)
            choices.Add(new FilterChoiceViewModel(name, name, OnFilterChoiceChanged, wasSelected.Contains(name)));

        // Only offer "(none)" when some expense actually has it blank.
        if (foundList.Any(n => n.Length == 0) || wasSelected.Contains(string.Empty))
            choices.Add(new FilterChoiceViewModel(string.Empty, noneLabel, OnFilterChoiceChanged, wasSelected.Contains(string.Empty)));
    }

    private static List<string> Selected(IEnumerable<FilterChoiceViewModel> choices) =>
        choices.Where(c => c.IsSelected).Select(c => c.Value).ToList();

    private static string Summarise(IEnumerable<FilterChoiceViewModel> choices, string all, string plural)
    {
        var selected = choices.Where(c => c.IsSelected).ToList();
        return selected.Count switch
        {
            0 => all,
            1 => selected[0].Label,
            2 => $"{selected[0].Label}, {selected[1].Label}",
            var n => $"{n} {plural}",
        };
    }

    private static decimal? ToAmount(double value) =>
        double.IsNaN(value) || value < 0 ? null : Math.Round((decimal)Math.Min(value, (double)ExcelService.MaxAmount), 2);

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {noun}s";
}
