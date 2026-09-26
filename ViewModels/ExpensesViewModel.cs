using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The Expenses page. This file covers loading and the period being viewed
/// (a month, or any date range). Filtering is in ExpensesViewModel.Filters.cs
/// and the add/edit form in ExpensesViewModel.Entry.cs.
/// </summary>
public partial class ExpensesViewModel : PageViewModelBase
{
    /// <summary>
    /// Longest date range that can be viewed at once.
    /// </summary>
    public const int MaxRangeMonths = 120;

    private readonly ExpenseStore _store;
    private readonly ExcelService _excel;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly TimeProvider _clock;

    // Increments on every load so a slow load for a period the user has
    // already moved away from doesn't overwrite the newer one.
    private int _loadVersion;
    private bool _hasLoaded;

    // Set while changing both ends of the range together, so that only one load runs.
    private bool _settingRange;

    // Every expense in the period, oldest first, before the filters.
    private IReadOnlyList<Expense> _loaded = Array.Empty<Expense>();

    /// <summary>
    /// The month the arrows move from and "Open in Excel" opens. See <see cref="UpdateAnchorMonth"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkbookName))]
    private YearMonth _month;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodTitle), nameof(IsCurrentMonth))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand))]
    private DateTime? _rangeFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodTitle), nameof(IsCurrentMonth))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand))]
    private DateTime? _rangeTo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkbookCommand))]
    private bool _fileExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private IReadOnlyList<string> _problemLines = Array.Empty<string>();

    [ObservableProperty]
    private string _problemSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>
    /// Why the chosen date range can't be shown (start after end, or too long).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRangeError))]
    private string? _rangeError;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEntryCommand), nameof(ReloadCommand), nameof(PreviousMonthCommand),
        nameof(NextMonthCommand), nameof(GoToThisMonthCommand), nameof(ShowLast30DaysCommand), nameof(ShowThisYearCommand))]
    private bool _isBusy;

    public ExpensesViewModel(ExpenseStore store, SettingsService settings, IDialogService dialogs,
        ILauncherService launcher, TimeProvider? clock = null)
    {
        _store = store;
        _excel = store.Excel;
        _settings = settings;
        _dialogs = dialogs;
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;

        var thisMonth = YearMonth.Of(Today);
        _month = thisMonth;
        _rangeFrom = thisMonth.FirstDay.ToDateTime(TimeOnly.MinValue);
        _rangeTo = thisMonth.LastDay.ToDateTime(TimeOnly.MinValue);
        _entryDate = Today.ToDateTime(TimeOnly.MinValue);

        RebuildOptions();
        EntryCategory = CategoryOptions.FirstOrDefault();
        EntryMember = MemberOptions.FirstOrDefault();
        PaymentChoices = PaymentOption.All
            .Select(o => new FilterChoiceViewModel(o.Method.ToString(), $"{o.Icon} {o.Name}", OnFilterChoiceChanged))
            .ToList();

        // Names or looks changed in Settings (and renames may have rewritten this year's files).
        _settings.FamilyMembersChanged += (_, _) => OnSettingsChanged();
        _settings.CategoriesChanged += (_, _) => OnSettingsChanged();
    }

    public override string Title => "Expenses";

    public override string Description => "Log, search and edit your spending.";

    /// <summary>
    /// "September 2026" for a whole month, otherwise the range, e.g. "1 Aug – 27 Sep 2026".
    /// </summary>
    public string PeriodTitle
    {
        get
        {
            if (RangeFrom is not { } fromDt || RangeTo is not { } toDt)
                return Month.DisplayName;

            var (from, to) = (DateOnly.FromDateTime(fromDt), DateOnly.FromDateTime(toDt));
            var month = YearMonth.Of(from);
            if (from == month.FirstDay && to == month.LastDay)
                return month.DisplayName;

            var inv = CultureInfo.InvariantCulture;
            if (from == to)
                return from.ToString("d MMM yyyy", inv);
            return from.Year == to.Year
                ? $"{from.ToString("d MMM", inv)} – {to.ToString("d MMM yyyy", inv)}"
                : $"{from.ToString("d MMM yyyy", inv)} – {to.ToString("d MMM yyyy", inv)}";
        }
    }

    public bool IsCurrentMonth
    {
        get
        {
            var thisMonth = YearMonth.Of(Today);
            return RangeFrom is { } from && RangeTo is { } to
                   && DateOnly.FromDateTime(from) == thisMonth.FirstDay
                   && DateOnly.FromDateTime(to) == thisMonth.LastDay;
        }
    }

    public string WorkbookName => Month.FileName;

    public bool HasProblems => ProblemLines.Count > 0;

    public bool HasError => ErrorMessage is not null;

    public bool HasRangeError => RangeError is not null;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo()
    {
        // Cheap on every visit: unchanged files come from memory, and files
        // edited in Excel meanwhile are picked up.
        _ = LoadAsync();
    }

    // Editing either date box reloads the period.
    partial void OnRangeFromChanged(DateTime? value)
    {
        UpdateAnchorMonth();
        if (!_settingRange)
            _ = LoadAsync();
    }

    partial void OnRangeToChanged(DateTime? value)
    {
        UpdateAnchorMonth();
        if (!_settingRange)
            _ = LoadAsync();
    }

    /// <summary>
    /// Picks the month the arrows move from and "Open in Excel" opens: the
    /// current month if the range includes today (e.g. "This year"),
    /// otherwise the range's last month.
    /// </summary>
    private void UpdateAnchorMonth()
    {
        if (RangeFrom is not { } fromDt || RangeTo is not { } toDt)
            return;

        var (from, to) = (DateOnly.FromDateTime(fromDt), DateOnly.FromDateTime(toDt));
        Month = from <= Today && Today <= to ? YearMonth.Of(Today) : YearMonth.Of(to);
    }

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;

        if (!TryGetRange(out var from, out var to, out var rangeError))
        {
            RangeError = rangeError;
            ShowLoaded(Array.Empty<Expense>(), Array.Empty<ExpenseSheetData>());
            return;
        }

        RangeError = null;
        var months = await Task.Run(() => _store.LoadMonths(YearMonth.Of(from), YearMonth.Of(to)));
        if (version != _loadVersion)
            return;

        var errors = months.Where(m => m.Error is not null).Select(m => m.Error!).ToList();
        if (errors.Count > 0)
            ErrorMessage = string.Join(" ", errors);

        var expenses = months.SelectMany(m => m.Expenses).Where(e => e.Date >= from && e.Date <= to).ToList();
        FileExists = months.FirstOrDefault(m => m.Month == Month)?.FileExists ?? _excel.MonthFileExists(Month);
        ShowLoaded(expenses, months);
        _hasLoaded = true;
    }

    private void ShowLoaded(IReadOnlyList<Expense> expenses, IReadOnlyList<ExpenseSheetData> months)
    {
        _loaded = expenses;

        var problems = months
            .SelectMany(m => m.Problems.Select(p => (m.Month, Problem: p)))
            .ToList();
        ProblemLines = problems.Select(x => $"{x.Month.FileName}, row {x.Problem.RowNumber}: {x.Problem.Message}").ToList();

        var files = problems.Select(x => x.Month).Distinct().ToList();
        var rows = problems.Count == 1 ? "1 row" : $"{problems.Count} rows";
        var where = files.Count == 1 ? files[0].FileName : $"{files.Count} workbooks";
        ProblemSummary = problems.Count == 1
            ? $"{rows} in {where} couldn't be read and isn't included below. Fix it in Excel, then reload."
            : $"{rows} in {where} couldn't be read and aren't included below. Fix them in Excel, then reload.";

        RebuildFilterChoices();
        ApplyFilters();
    }

    private bool TryGetRange(out DateOnly from, out DateOnly to, out string? error)
    {
        // An emptied date box falls back to the edges of the current month.
        from = RangeFrom is { } f ? DateOnly.FromDateTime(f) : Month.FirstDay;
        to = RangeTo is { } t ? DateOnly.FromDateTime(t) : YearMonth.Of(from).LastDay;
        error = null;

        if (from > to)
        {
            error = "The start date is after the end date.";
            return false;
        }

        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        if (months > MaxRangeMonths)
        {
            error = $"That range is too long: pick {MaxRangeMonths / 12} years or less.";
            return false;
        }

        return true;
    }

    private bool CanUseFile() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task ReloadAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;

        // Re-read from disk even if the files look unchanged.
        if (TryGetRange(out var from, out var to, out _))
        {
            for (var m = YearMonth.Of(from); m <= YearMonth.Of(to); m = m.AddMonths(1))
                _store.Invalidate(m);
        }

        return LoadAsync();
    }

    // ---- Period navigation --------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task PreviousMonthAsync() => ShowMonthAsync(Month.AddMonths(-1));

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task NextMonthAsync() => ShowMonthAsync(Month.AddMonths(1));

    private bool CanGoToThisMonth() => !IsBusy && !IsCurrentMonth;

    [RelayCommand(CanExecute = nameof(CanGoToThisMonth))]
    private Task GoToThisMonthAsync() => ShowMonthAsync(YearMonth.Of(Today));

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task ShowLast30DaysAsync() => ShowRangeAsync(Today.AddDays(-29), Today);

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task ShowThisYearAsync() => ShowRangeAsync(new DateOnly(Today.Year, 1, 1), new DateOnly(Today.Year, 12, 31));

    private Task ShowMonthAsync(YearMonth month)
    {
        // New entries default to today in the current month, otherwise to the 1st of the month.
        EntryDate = (month.Contains(Today) ? Today : month.FirstDay).ToDateTime(TimeOnly.MinValue);
        return ShowRangeAsync(month.FirstDay, month.LastDay);
    }

    private Task ShowRangeAsync(DateOnly from, DateOnly to)
    {
        if (IsEditing)
            CancelEdit();

        ErrorMessage = null;
        StatusMessage = null;

        _settingRange = true;
        try
        {
            RangeFrom = from.ToDateTime(TimeOnly.MinValue);
            RangeTo = to.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _settingRange = false;
        }

        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(FileExists))]
    private void OpenWorkbook()
    {
        try
        {
            _launcher.OpenFile(_excel.GetMonthFilePath(Month));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't open {WorkbookName}: {ex.Message}";
        }
    }

    private void OnSettingsChanged()
    {
        RefreshEntryOptions();
        if (_hasLoaded)
            _ = LoadAsync();
    }
}
