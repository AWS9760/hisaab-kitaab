using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One note in the count: "₨ 1,000 × 12 = ₨ 12,000".
/// </summary>
public partial class DenominationRowViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    /// <summary>
    /// Double for NumberBox; NaN (an emptied box) counts as zero.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double _count;

    public DenominationRowViewModel(int denomination, Action onChanged)
    {
        Denomination = denomination;
        _onChanged = onChanged;
    }

    public int Denomination { get; }

    public string Label => Pkr.Format(Denomination);

    public string Color => PkrNotes.ColorOf(Denomination);

    public int Notes => double.IsNaN(Count) ? 0 : (int)Math.Clamp(Math.Round(Count), 0, PkrNotes.MaxCount);

    public decimal Value => (decimal)Denomination * Notes;

    public string ValueText => Pkr.Format(Value);

    partial void OnCountChanged(double value) => _onChanged();
}

/// <summary>
/// The Currency page: count the notes you have and compare the total with
/// cash in hand from Bank &amp; Cash. Saves automatically shortly after each change.
/// </summary>
public partial class CurrencyViewModel : PageViewModelBase
{
    private readonly BankCashService _service;
    private readonly WorkbookStore _store;
    private readonly ILauncherService _launcher;
    private readonly TimeProvider _clock;

    private int _loadVersion;
    private bool _loading;
    private bool _dirty;
    private CancellationTokenSource? _saveDelay;
    private MonthBalances? _balances;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(IsCurrentMonth), nameof(WorkbookName))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand), nameof(CountedTodayCommand))]
    private YearMonth _month;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkbookCommand))]
    private bool _fileExists;

    [ObservableProperty]
    private double _coins;

    [ObservableProperty]
    private DateTime? _countedOn;

    [ObservableProperty]
    private string _totalText = Pkr.Format(0);

    [ObservableProperty]
    private string _expectedText = Pkr.Format(0);

    [ObservableProperty]
    private string _expectedLabel = "Cash in hand in your records";

    [ObservableProperty]
    private string _differenceText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMatch), nameof(IsMismatch), nameof(IsNotCounted))]
    private CashCheckStatus _status = CashCheckStatus.NotCounted;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _saveState = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private IReadOnlyList<string> _problemLines = Array.Empty<string>();

    public CurrencyViewModel(BankCashService service, ILauncherService launcher, TimeProvider? clock = null)
    {
        _service = service;
        _store = service.Store;
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;
        _month = YearMonth.Of(Today);

        Rows = PkrNotes.Denominations.Select(d => new DenominationRowViewModel(d, OnCountChanged)).ToList();
    }

    public override string Title => "Currency";

    public override string Description => "Count your notes and check them against cash in hand.";

    public IReadOnlyList<DenominationRowViewModel> Rows { get; }

    /// <summary>
    /// How long after the last change the count is saved. Tests set it to zero.
    /// </summary>
    public TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    public string MonthTitle => Month.DisplayName;

    public bool IsCurrentMonth => Month == YearMonth.Of(Today);

    public string WorkbookName => Month.FileName;

    public bool IsMatch => Status == CashCheckStatus.Matches;

    public bool IsMismatch => Status is CashCheckStatus.MoreThanRecorded or CashCheckStatus.LessThanRecorded;

    public bool IsNotCounted => Status == CashCheckStatus.NotCounted;

    public bool HasError => ErrorMessage is not null;

    public bool HasProblems => ProblemLines.Count > 0;

    /// <summary>
    /// The count as currently entered.
    /// </summary>
    public CurrencyCount CurrentCount => new()
    {
        CountedOn = CountedOn is { } d ? DateOnly.FromDateTime(d) : null,
        Notes = Rows.Where(r => r.Notes > 0).ToDictionary(r => r.Denomination, r => r.Notes),
        Coins = double.IsNaN(Coins) || Coins < 0 ? 0 : Math.Round((decimal)Math.Min(Coins, 1e11), 2),
    };

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo() => _ = LoadAsync();

    public override Task OnNavigatedFromAsync() => FlushAsync();

    partial void OnCoinsChanged(double value) => OnCountChanged();

    partial void OnCountedOnChanged(DateTime? value)
    {
        if (_loading)
            return;
        UpdateCheck();
        ScheduleSave();
    }

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        await FlushAsync();

        var version = ++_loadVersion;
        var month = Month;
        var (sheet, balances) = await Task.Run(() => (_store.LoadCurrency(month), _service.GetBalances(month)));
        if (version != _loadVersion)
            return;

        _balances = balances;
        FileExists = sheet.FileExists;
        ErrorMessage = sheet.Error;
        ProblemLines = sheet.Problems.Select(p => $"{WorkbookName}, {p.SheetName} row {p.RowNumber}: {p.Message}").ToList();

        _loading = true;
        try
        {
            foreach (var row in Rows)
                row.Count = sheet.Count.CountOf(row.Denomination);
            Coins = (double)sheet.Count.Coins;
            CountedOn = sheet.Count.CountedOn?.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _loading = false;
        }

        _dirty = false;
        SaveState = sheet.Count.IsEmpty ? string.Empty : "Saved";
        UpdateCheck();
    }

    // ---- The check ------------------------------------------------------------

    private void OnCountChanged()
    {
        if (_loading)
            return;

        // The first number entered dates the count, if it isn't dated yet.
        if (CountedOn is null)
        {
            _loading = true;
            CountedOn = DefaultCountDate().ToDateTime(TimeOnly.MinValue);
            _loading = false;
        }

        UpdateCheck();
        ScheduleSave();
    }

    private DateOnly DefaultCountDate() => Month.Contains(Today) ? Today : Month.LastDay;

    private void UpdateCheck()
    {
        var count = CurrentCount;
        TotalText = Pkr.Format(count.Total);

        if (_balances is null)
            return;

        var date = count.CountedOn ?? DefaultCountDate();
        var check = CashCheck.For(count, date, _balances);
        var day = date.ToString("d MMM", CultureInfo.InvariantCulture);

        ExpectedLabel = $"Cash in hand on {day}, from Bank & Cash";
        ExpectedText = Pkr.Format(check.Expected);
        DifferenceText = check.Status == CashCheckStatus.NotCounted ? string.Empty
            : check.Difference == 0 ? Pkr.Format(0)
            : Pkr.FormatChange(check.Difference);
        Status = check.Status;
        var gap = Pkr.Format(Math.Abs(check.Difference));
        (StatusTitle, StatusMessage) = check.Status switch
        {
            CashCheckStatus.Matches => ("Your cash matches your records", "Nothing to fix."),
            CashCheckStatus.MoreThanRecorded => ($"{gap} more than your records",
                "Maybe a withdrawal or some cash income wasn't logged on Bank & Cash, or a cash expense was entered twice."),
            CashCheckStatus.LessThanRecorded => ($"{gap} less than your records",
                "Maybe some cash spending wasn't added on the Expenses page, or a withdrawal was logged for the wrong amount."),
            _ => ("Not counted yet", "Enter how many of each note you have, and any coins."),
        };
    }

    // ---- Saving ---------------------------------------------------------------

    private void ScheduleSave()
    {
        _dirty = true;
        SaveState = "Saving…";
        _saveDelay?.Cancel();
        var delay = _saveDelay = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(delay.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(SaveDelay, cancel);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await SaveNowAsync();
    }

    /// <summary>
    /// Saves straight away if there are unsaved changes (e.g. before leaving the page).
    /// </summary>
    public async Task FlushAsync()
    {
        _saveDelay?.Cancel();
        if (_dirty)
            await SaveNowAsync();
    }

    [RelayCommand]
    private async Task SaveNowAsync()
    {
        var month = Month;
        var count = CurrentCount;
        _dirty = false;

        // Don't create a workbook just to store an empty count.
        if (count.IsEmpty && !_store.Excel.MonthFileExists(month))
        {
            SaveState = string.Empty;
            return;
        }

        try
        {
            await Task.Run(() => _store.Excel.SaveCurrencyCount(month, count));
            _store.Invalidate(month);
            if (month == Month)
            {
                FileExists = true;
                ErrorMessage = null;
                SaveState = "Saved";
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            _dirty = true;
            SaveState = "Not saved";
            ErrorMessage = ex is ArgumentException a && a.ParamName is not null
                ? a.Message.Replace($" (Parameter '{a.ParamName}')", string.Empty)
                : $"{ex.Message} Your count is kept here; choose Save again once that's sorted.";
        }
    }

    [RelayCommand]
    private void ClearCount()
    {
        foreach (var row in Rows)
            row.Count = 0;
        Coins = 0;
    }

    private bool CanSayCountedToday() => IsCurrentMonth;

    [RelayCommand(CanExecute = nameof(CanSayCountedToday))]
    private void CountedToday() => CountedOn = Today.ToDateTime(TimeOnly.MinValue);

    // ---- Navigation -----------------------------------------------------------

    [RelayCommand]
    private Task PreviousMonthAsync() => ShowMonthAsync(Month.AddMonths(-1));

    [RelayCommand]
    private Task NextMonthAsync() => ShowMonthAsync(Month.AddMonths(1));

    private bool CanGoToThisMonth() => !IsCurrentMonth;

    [RelayCommand(CanExecute = nameof(CanGoToThisMonth))]
    private Task GoToThisMonthAsync() => ShowMonthAsync(YearMonth.Of(Today));

    private async Task ShowMonthAsync(YearMonth month)
    {
        await FlushAsync();
        Month = month;
        await LoadAsync();
    }

    [RelayCommand]
    private Task ReloadAsync()
    {
        _store.Invalidate(Month);
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(FileExists))]
    private void OpenWorkbook()
    {
        try
        {
            _launcher.OpenFile(_store.Excel.GetMonthFilePath(Month));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't open {WorkbookName}: {ex.Message}";
        }
    }
}
