using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The Bank &amp; Cash page: a month's bank balance and cash in hand, their
/// opening balances, a log of withdrawals/deposits/income, and a timeline
/// that includes the month's cash and bank expenses.
/// </summary>
public partial class BankCashViewModel : PageViewModelBase
{
    private readonly BankCashService _service;
    private readonly WorkbookStore _store;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly TimeProvider _clock;
    private int _loadVersion;
    private IReadOnlyList<LedgerRowViewModel> _allRows = Array.Empty<LedgerRowViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(IsCurrentMonth), nameof(WorkbookName))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand))]
    private YearMonth _month;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkbookCommand))]
    private bool _fileExists;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEntryCommand), nameof(PreviousMonthCommand), nameof(NextMonthCommand),
        nameof(GoToThisMonthCommand), nameof(ReloadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    // ---- Balances ----

    [ObservableProperty]
    private string _bankBalanceText = Pkr.Format(0);

    [ObservableProperty]
    private string _cashBalanceText = Pkr.Format(0);

    [ObservableProperty]
    private bool _isBankNegative;

    [ObservableProperty]
    private bool _isCashNegative;

    [ObservableProperty]
    private string _bankOpeningText = string.Empty;

    [ObservableProperty]
    private string _cashOpeningText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _bankBreakdown = Array.Empty<string>();

    [ObservableProperty]
    private IReadOnlyList<string> _cashBreakdown = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBalanceWarning))]
    private string? _balanceWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private IReadOnlyList<string> _problemLines = Array.Empty<string>();

    // ---- Opening balance editors ----

    [ObservableProperty]
    private double _openingBankInput;

    [ObservableProperty]
    private double _openingCashInput;

    [ObservableProperty]
    private bool _bankOpeningIsManual;

    [ObservableProperty]
    private bool _cashOpeningIsManual;

    // ---- Timeline ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private IReadOnlyList<LedgerRowViewModel> _rows = Array.Empty<LedgerRowViewModel>();

    [ObservableProperty]
    private bool _showExpenses = true;

    // ---- Entry form ----

    [ObservableProperty]
    private BankCashTypeOption _entryType = BankCashTypeOption.For(BankCashEntryType.Withdrawal);

    /// <summary>
    /// NaN while empty.
    /// </summary>
    [ObservableProperty]
    private double _entryAmount = double.NaN;

    [ObservableProperty]
    private DateTime? _entryDate;

    [ObservableProperty]
    private string _entryNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveButtonText))]
    private LedgerRowViewModel? _editingRow;

    public BankCashViewModel(BankCashService service, SettingsService settings, IDialogService dialogs,
        ILauncherService launcher, TimeProvider? clock = null)
    {
        _service = service;
        _store = service.Store;
        _settings = settings;
        _dialogs = dialogs;
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;

        _month = YearMonth.Of(Today);
        _entryDate = Today.ToDateTime(TimeOnly.MinValue);
    }

    public override string Title => "Bank & Cash";

    public override string Description => "Bank balance and cash in hand. Cash and bank expenses are taken off automatically.";

    public IReadOnlyList<BankCashTypeOption> TypeOptions => BankCashTypeOption.All;

    public string MonthTitle => Month.DisplayName;

    public bool IsCurrentMonth => Month == YearMonth.Of(Today);

    public string WorkbookName => Month.FileName;

    public bool HasError => ErrorMessage is not null;

    public bool HasBalanceWarning => BalanceWarning is not null;

    public bool HasProblems => ProblemLines.Count > 0;

    public bool HasRows => Rows.Count > 0;

    public bool IsEditing => EditingRow is not null;

    public string FormTitle => IsEditing ? "Edit transaction" : "Log a withdrawal, deposit or income";

    public string SaveButtonText => IsEditing ? "Save changes" : "Add";

    /// <summary>
    /// Raised after a save so the view can put the cursor back in the amount box.
    /// </summary>
    public event EventHandler? EntryFocusRequested;

    /// <summary>
    /// The balances most recently shown, for tests and other pages.
    /// </summary>
    public MonthBalances? Balances { get; private set; }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo() => _ = LoadAsync();

    partial void OnShowExpensesChanged(bool value) => ApplyRowFilter();

    partial void OnEntryAmountChanged(double value) => StatusMessage = null;

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var month = Month;

        var (balances, sheet, hasEarlier) = await Task.Run(() =>
        {
            var b = _service.GetBalances(month);
            try
            {
                // Keep the workbook's carried-forward opening figures current for anyone opening it in Excel.
                _service.SyncCarriedOpenings(b);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // e.g. open in Excel right now. The figures in the app are still right;
                // the workbook catches up next time.
            }

            var hasEarlierMonths = _store.Excel.GetExistingMonths().Any(m => m < month);
            return (b, _store.LoadBankCash(month), hasEarlierMonths);
        });

        if (version != _loadVersion)
            return;

        Show(balances, sheet, hasEarlier);
    }

    private void Show(MonthBalances b, BankCashSheetData sheet, bool hasEarlierMonths)
    {
        Balances = b;
        FileExists = sheet.FileExists;
        if (sheet.Error is not null)
            ErrorMessage = sheet.Error;

        BankBalanceText = Pkr.Format(b.ClosingBank);
        CashBalanceText = Pkr.Format(b.ClosingCash);
        IsBankNegative = b.ClosingBank < 0;
        IsCashNegative = b.ClosingCash < 0;

        // With no earlier workbook there's nothing to carry forward, so say so rather than "carried from July".
        var carried = hasEarlierMonths ? $"carried from {Month.AddMonths(-1).DisplayName}" : "not set yet";
        BankOpeningText = $"Opening {Pkr.Format(b.OpeningBank)} · " + (b.BankOpeningIsManual ? "set by you" : carried);
        CashOpeningText = $"Opening {Pkr.Format(b.OpeningCash)} · " + (b.CashOpeningIsManual ? "set by you" : carried);
        (OpeningBankInput, OpeningCashInput) = ((double)b.OpeningBank, (double)b.OpeningCash);
        (BankOpeningIsManual, CashOpeningIsManual) = (b.BankOpeningIsManual, b.CashOpeningIsManual);

        BankBreakdown = Lines(("Income", b.BankIncome), ("Deposits", b.Deposits), ("Withdrawals", -b.Withdrawals), ("Bank expenses", -b.BankExpenses), ("Card repayments", -b.CardRepaymentsBank), ("Zakat given", -b.ZakatBank));
        CashBreakdown = Lines(("Income", b.CashIncome), ("Withdrawals", b.Withdrawals), ("Deposits", -b.Deposits), ("Cash expenses", -b.CashExpenses), ("Card repayments", -b.CardRepaymentsCash), ("Zakat given", -b.ZakatCash));

        BalanceWarning = (b.FirstNegativeCash, b.FirstNegativeBank) switch
        {
            ({ } cash, _) => $"Cash in hand went below zero on {Day(cash)}. Did you forget to log a withdrawal or cash income?",
            (_, { } bank) => $"The bank balance went below zero on {Day(bank)}. Check the opening balance, or log the income that came in.",
            _ => null,
        };

        ProblemLines = sheet.Problems.Select(p => $"{WorkbookName}, {p.SheetName} row {p.RowNumber}: {p.Message}").ToList();

        _allRows = b.Ledger
            .Select(line => new LedgerRowViewModel(line, line.Expense is { } e ? LookUpCategory(e.Category) : null))
            .Reverse()
            .ToList();
        ApplyRowFilter();
    }

    private static IReadOnlyList<string> Lines(params (string Label, decimal Amount)[] parts) =>
        parts.Where(p => p.Amount != 0).Select(p => $"{p.Label} {Pkr.FormatChange(p.Amount)}").ToList();

    private static string Day(DateOnly date) => date.ToString("d MMM", CultureInfo.InvariantCulture);

    private void ApplyRowFilter() =>
        Rows = ShowExpenses ? _allRows : _allRows.Where(r => r.IsEntry).ToList();

    private CategoryOption? LookUpCategory(string name) =>
        _settings.FindCategory(name) is { } c ? new CategoryOption(c.Name, c.Icon, c.Color, c.Id) : null;

    // ---- Navigation -----------------------------------------------------------

    private bool CanUseFile() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task PreviousMonthAsync() => ShowMonthAsync(Month.AddMonths(-1));

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task NextMonthAsync() => ShowMonthAsync(Month.AddMonths(1));

    private bool CanGoToThisMonth() => !IsBusy && !IsCurrentMonth;

    [RelayCommand(CanExecute = nameof(CanGoToThisMonth))]
    private Task GoToThisMonthAsync() => ShowMonthAsync(YearMonth.Of(Today));

    private Task ShowMonthAsync(YearMonth month)
    {
        if (IsEditing)
            EndEdit();

        Month = month;
        ErrorMessage = null;
        StatusMessage = null;
        EntryDate = (month.Contains(Today) ? Today : month.FirstDay).ToDateTime(TimeOnly.MinValue);
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task ReloadAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;
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

    // ---- Opening balances ---------------------------------------------------

    [RelayCommand]
    private Task SetBankOpeningAsync() => SetOpeningAsync(Account.Bank, OpeningBankInput);

    [RelayCommand]
    private Task SetCashOpeningAsync() => SetOpeningAsync(Account.Cash, OpeningCashInput);

    [RelayCommand]
    private Task CarryBankOpeningAsync() => CarryForwardAsync(Account.Bank);

    [RelayCommand]
    private Task CarryCashOpeningAsync() => CarryForwardAsync(Account.Cash);

    private Task SetOpeningAsync(Account account, double value)
    {
        if (double.IsNaN(value))
        {
            ErrorMessage = "Enter the opening balance.";
            return Task.CompletedTask;
        }

        var amount = Math.Round((decimal)Math.Clamp(value, -(double)ExcelService.MaxAmount, (double)ExcelService.MaxAmount), 2);
        return RunFileOperationAsync(
            () => _store.Excel.SetOpeningBalance(Month, account, amount, isManual: true),
            $"Opening {(account == Account.Bank ? "bank balance" : "cash in hand")} set to {Pkr.Format(amount)}.");
    }

    /// <summary>
    /// Goes back to using last month's closing balance.
    /// </summary>
    private Task CarryForwardAsync(Account account)
    {
        var month = Month;
        return RunFileOperationAsync(() =>
        {
            var previous = _service.GetBalances(month.AddMonths(-1));
            var carried = account == Account.Bank ? previous.ClosingBank : previous.ClosingCash;
            _store.Excel.SetOpeningBalance(month, account, carried, isManual: false);
        }, $"Opening {(account == Account.Bank ? "bank balance" : "cash in hand")} now follows {month.AddMonths(-1).DisplayName}'s closing balance.");
    }

    // ---- Log entries --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private async Task SaveEntryAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;

        if (double.IsNaN(EntryAmount) || EntryAmount <= 0)
        {
            ErrorMessage = "Enter an amount more than zero.";
            return;
        }

        if (EntryAmount >= (double)ExcelService.MaxAmount)
        {
            ErrorMessage = "That amount is too large.";
            return;
        }

        if (EntryDate is not { } date)
        {
            ErrorMessage = "Pick a date.";
            return;
        }

        var editing = EditingRow?.Entry;
        var entry = new BankCashEntry
        {
            Id = editing?.Id ?? Guid.Empty,
            Date = DateOnly.FromDateTime(date),
            Type = EntryType.Type,
            Amount = Math.Round((decimal)EntryAmount, 2, MidpointRounding.AwayFromZero),
            Note = EntryNote,
        };
        var savedMonth = YearMonth.Of(entry.Date);
        var where = savedMonth == Month ? string.Empty : $" in {savedMonth.DisplayName}";

        // Cleared up front so a fast typist's next amount isn't wiped when the save finishes.
        var (typedAmount, typedNote) = (EntryAmount, EntryNote);
        if (editing is null)
            (EntryAmount, EntryNote) = (double.NaN, string.Empty);

        var ok = await RunFileOperationAsync(() =>
        {
            if (editing is null)
                _store.Excel.AddBankCashEntry(entry);
            else
                _store.Excel.UpdateBankCashEntry(YearMonth.Of(editing.Date), entry);
        }, $"{(editing is null ? "Added" : "Saved")} {EntryType.Name.ToLowerInvariant()} of {Pkr.Format(entry.Amount)}{where}.");

        if (ok)
        {
            if (editing is not null)
                EndEdit();
            EntryFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (editing is null && double.IsNaN(EntryAmount) && EntryNote.Length == 0)
        {
            (EntryAmount, EntryNote) = (typedAmount, typedNote);
        }
    }

    [RelayCommand]
    private void BeginEdit(LedgerRowViewModel row)
    {
        if (row.Entry is not { } entry)
            return;

        ErrorMessage = null;
        StatusMessage = null;
        EditingRow = row;
        EntryType = BankCashTypeOption.For(entry.Type);
        EntryAmount = (double)entry.Amount;
        EntryDate = entry.Date.ToDateTime(TimeOnly.MinValue);
        EntryNote = entry.Note;
        EntryFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ErrorMessage = null;
        EndEdit();
    }

    [RelayCommand]
    private async Task DeleteAsync(LedgerRowViewModel row)
    {
        if (row.Entry is not { } entry)
            return;

        var what = $"{BankCashTypeOption.For(entry.Type).Name} · {Pkr.Format(entry.Amount)} · " +
                   entry.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (!await _dialogs.ConfirmAsync("Delete this transaction?",
                $"{what}\n\nThis removes the row from {YearMonth.Of(entry.Date).FileName}.", "Delete"))
            return;

        var ok = await RunFileOperationAsync(
            () => _store.Excel.DeleteBankCashEntry(YearMonth.Of(entry.Date), entry.Id),
            $"Deleted {BankCashTypeOption.For(entry.Type).Name.ToLowerInvariant()} of {Pkr.Format(entry.Amount)}.");
        if (ok && EditingRow?.Entry?.Id == entry.Id)
            EndEdit();
    }

    private void EndEdit()
    {
        EditingRow = null;
        EntryAmount = double.NaN;
        EntryNote = string.Empty;
    }

    /// <summary>
    /// Runs a workbook change in the background, then reloads. Returns false
    /// (with <see cref="ErrorMessage"/> set) if it failed.
    /// </summary>
    private async Task<bool> RunFileOperationAsync(Action operation, string successMessage)
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        var ok = false;
        try
        {
            await Task.Run(operation);
            ok = true;
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);
        }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
        if (ok)
            StatusMessage = successMessage;
        return ok;
    }
}
