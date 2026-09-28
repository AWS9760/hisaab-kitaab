using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One line of the card timeline: a card expense or a repayment.
/// </summary>
public class CardRowViewModel
{
    public CardRowViewModel(CardLedgerLine line, CategoryOption? category)
    {
        Line = line;
        if (line.Repayment is { } r)
        {
            Icon = "💳";
            Color = "#2563EB";
            Title = "Repayment";
            Detail = string.IsNullOrWhiteSpace(r.Note) ? $"from {r.PaidFrom.ToDisplayName().ToLowerInvariant()}" : $"{r.Note} · from {r.PaidFrom.ToDisplayName().ToLowerInvariant()}";
        }
        else
        {
            var e = line.Expense!;
            Icon = category?.Icon ?? CategoryStyles.DefaultIcon;
            Color = category?.Color ?? CategoryStyles.DefaultColor;
            Title = string.IsNullOrWhiteSpace(e.Category) ? "Card expense" : e.Category;
            Detail = string.Join(" · ", new[] { e.FamilyMember, e.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public CardLedgerLine Line { get; }

    public CardRepayment? Repayment => Line.Repayment;

    /// <summary>
    /// Repayments are edited here; card expenses on the Expenses page.
    /// </summary>
    public bool IsRepayment => Line.Repayment is not null;

    public string Icon { get; }

    public string Color { get; }

    public string Title { get; }

    public string Detail { get; }

    public string DateText => Line.Date.ToString("ddd, d MMM", CultureInfo.InvariantCulture);

    public string ChangeText => Pkr.FormatChange(Line.Change);

    public string OwedText => Pkr.Format(Line.Owed);
}

public record AccountOption(Account Account, string Name, string Icon)
{
    public static readonly IReadOnlyList<AccountOption> All = new AccountOption[]
    {
        new(Account.Bank, "From bank", "🏦"),
        new(Account.Cash, "In cash", "💵"),
    };

    public static AccountOption For(Account account) => All.First(o => o.Account == account);
}

/// <summary>
/// The Credit Card page: what's owed, how much of the limit it uses, when
/// it's due, repayments, and a timeline of card spending and repayments.
/// </summary>
public partial class CreditCardViewModel : PageViewModelBase
{
    /// <summary>
    /// Share of the limit at which the page starts warning.
    /// </summary>
    public const decimal HighUsage = 0.8m;

    /// <summary>
    /// Days before the due date at which the page starts warning.
    /// </summary>
    public const int DueSoonDays = 3;

    private readonly CreditCardService _service;
    private readonly WorkbookStore _store;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly TimeProvider _clock;
    private int _loadVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(IsCurrentMonth), nameof(WorkbookName))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand))]
    private YearMonth _month;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkbookCommand))]
    private bool _fileExists;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRepaymentCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    // ---- Summary ----

    [ObservableProperty]
    private string _cardName = "Credit card";

    [ObservableProperty]
    private string _outstandingText = Pkr.Format(0);

    [ObservableProperty]
    private string _openingText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _breakdown = Array.Empty<string>();

    [ObservableProperty]
    private bool _hasLimit;

    [ObservableProperty]
    private double _limitUsedPercent;

    [ObservableProperty]
    private string _limitText = string.Empty;

    [ObservableProperty]
    private bool _isHighUsage;

    [ObservableProperty]
    private bool _isOverLimit;

    [ObservableProperty]
    private string _dueText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private IReadOnlyList<string> _problemLines = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private IReadOnlyList<CardRowViewModel> _rows = Array.Empty<CardRowViewModel>();

    // ---- Editors ----

    [ObservableProperty]
    private double _openingInput;

    [ObservableProperty]
    private bool _openingIsManual;

    [ObservableProperty]
    private string _cardNameInput = string.Empty;

    [ObservableProperty]
    private double _limitInput = double.NaN;

    [ObservableProperty]
    private double _dueDayInput = double.NaN;

    [ObservableProperty]
    private double _entryAmount = double.NaN;

    [ObservableProperty]
    private DateTime? _entryDate;

    [ObservableProperty]
    private AccountOption _entryFrom = AccountOption.For(Account.Bank);

    [ObservableProperty]
    private string _entryNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveButtonText))]
    private CardRowViewModel? _editingRow;

    public CreditCardViewModel(CreditCardService service, SettingsService settings, IDialogService dialogs,
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
        FillCardEditor();

        _settings.CardSettingsChanged += (_, _) =>
        {
            FillCardEditor();
            if (Card is not null)
                ShowSummary(Card);
        };
    }

    public override string Title => "Credit Card";

    public override string Description => "What you owe on the card. Card expenses are added automatically; log repayments here.";

    public IReadOnlyList<AccountOption> FromOptions => AccountOption.All;

    public string MonthTitle => Month.DisplayName;

    public bool IsCurrentMonth => Month == YearMonth.Of(Today);

    public string WorkbookName => Month.FileName;

    public bool HasError => ErrorMessage is not null;

    public bool HasWarning => Warning is not null;

    public bool HasProblems => ProblemLines.Count > 0;

    public bool HasRows => Rows.Count > 0;

    public bool IsEditing => EditingRow is not null;

    public string FormTitle => IsEditing ? "Edit repayment" : "Log a repayment";

    public string SaveButtonText => IsEditing ? "Save changes" : "Add";

    /// <summary>
    /// The month most recently shown.
    /// </summary>
    public CardMonth? Card { get; private set; }

    public event EventHandler? EntryFocusRequested;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo() => _ = LoadAsync();

    partial void OnEntryAmountChanged(double value) => StatusMessage = null;

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var month = Month;

        var (card, sheet) = await Task.Run(() =>
        {
            var c = _service.GetMonth(month);
            try
            {
                _service.SyncCarriedOpening(c);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // e.g. open in Excel; the app's figures are still right.
            }

            return (c, _store.LoadCard(month));
        });

        if (version != _loadVersion)
            return;

        Card = card;
        FileExists = sheet.FileExists;
        if (sheet.Error is not null)
            ErrorMessage = sheet.Error;
        ProblemLines = sheet.Problems.Select(p => $"{WorkbookName}, {p.SheetName} row {p.RowNumber}: {p.Message}").ToList();

        ShowSummary(card);
        Rows = card.Ledger
            .Select(l => new CardRowViewModel(l, l.Expense is { } e ? LookUpCategory(e.Category) : null))
            .Reverse()
            .ToList();
    }

    private void ShowSummary(CardMonth card)
    {
        var settings = _settings.CreditCard;
        CardName = settings.Name;
        OutstandingText = Pkr.Format(card.Outstanding);
        OpeningText = $"Owed at the start {Pkr.Format(card.Opening)} · " +
                      (card.OpeningIsManual ? "set by you" : $"carried from {Month.AddMonths(-1).DisplayName}");
        (OpeningInput, OpeningIsManual) = ((double)card.Opening, card.OpeningIsManual);
        Breakdown = new[] { ("Card spending", card.Spent), ("Repayments", -card.Repaid) }
            .Where(p => p.Item2 != 0)
            .Select(p => $"{p.Item1} {Pkr.FormatChange(p.Item2)}")
            .ToList();

        var used = card.LimitUsed(settings.Limit);
        HasLimit = used is not null;
        LimitUsedPercent = used is { } u ? (double)Math.Clamp(u * 100, 0, 100) : 0;
        IsOverLimit = used > 1;
        IsHighUsage = used >= HighUsage && !IsOverLimit;
        LimitText = settings.Limit is { } limit
            ? $"{used:P0} of {Pkr.Format(limit)} used · {Pkr.Format(Math.Max(limit - card.Outstanding, 0))} available"
            : "Set your credit limit to see how much of it you've used.";

        var due = settings.NextDueDate(Today);
        var days = due is { } d ? d.DayNumber - Today.DayNumber : (int?)null;
        DueText = due is null
            ? "Set the day your bill is due to see when to pay."
            : $"Next payment due {due.Value.ToString("ddd, d MMM", CultureInfo.InvariantCulture)} " + days switch
            {
                0 => "(today)",
                1 => "(tomorrow)",
                var n => $"(in {n} days)",
            };

        // Warnings only make sense for what's owed now, i.e. this month.
        Warning = !IsCurrentMonth || card.Outstanding <= 0 ? null
            : IsOverLimit ? $"You're over your credit limit by {Pkr.Format(card.Outstanding - settings.Limit!.Value)}."
            : days is { } left && left <= DueSoonDays ? $"{Pkr.Format(card.Outstanding)} is due {(left == 0 ? "today" : left == 1 ? "tomorrow" : $"in {left} days")}."
            : IsHighUsage ? $"You've used {used:P0} of your credit limit."
            : null;
    }

    private void FillCardEditor()
    {
        var card = _settings.CreditCard;
        CardNameInput = card.Name;
        LimitInput = card.Limit is { } l ? (double)l : double.NaN;
        DueDayInput = card.DueDay ?? double.NaN;
    }

    private CategoryOption? LookUpCategory(string name) =>
        _settings.FindCategory(name) is { } c ? new CategoryOption(c.Name, c.Icon, c.Color, c.Id) : null;

    // ---- Card details ---------------------------------------------------------

    [RelayCommand]
    private async Task SaveCardDetailsAsync()
    {
        decimal? limit = double.IsNaN(LimitInput) ? null : Math.Round((decimal)Math.Clamp(LimitInput, 0, 1e11), 2);
        int? dueDay = double.IsNaN(DueDayInput) ? null : (int)Math.Round(DueDayInput);
        if (dueDay is < 1 or > 31)
        {
            ErrorMessage = "The due day must be between 1 and 31.";
            return;
        }

        if (!SettingsSave.Try(() => _settings.UpdateCreditCard(CardNameInput, limit, dueDay), out var error))
        {
            ErrorMessage = error;
            return;
        }

        // Show the new limit and due date in this month's sheet straight away.
        var month = Month;
        await RunFileOperationAsync(() => _store.Excel.RefreshMonthWorkbook(month), "Card details saved.");
    }

    // ---- Opening outstanding --------------------------------------------------

    [RelayCommand]
    private Task SetOpeningAsync()
    {
        if (double.IsNaN(OpeningInput) || OpeningInput < 0)
        {
            ErrorMessage = "Enter what was owed at the start of the month (zero or more).";
            return Task.CompletedTask;
        }

        var amount = Math.Round((decimal)Math.Min(OpeningInput, 1e11), 2);
        return RunFileOperationAsync(() => _store.Excel.SetCardOpening(Month, amount, isManual: true),
            $"Opening amount owed set to {Pkr.Format(amount)}.");
    }

    [RelayCommand]
    private Task CarryOpeningAsync()
    {
        var month = Month;
        return RunFileOperationAsync(() =>
        {
            var previous = _service.GetMonth(month.AddMonths(-1));
            _store.Excel.SetCardOpening(month, Math.Max(previous.Outstanding, 0), isManual: false);
        }, $"Opening amount owed now follows {month.AddMonths(-1).DisplayName}.");
    }

    // ---- Repayments -----------------------------------------------------------

    [RelayCommand]
    private void PayInFull()
    {
        if (Card is { Outstanding: > 0 } card)
            EntryAmount = (double)card.Outstanding;
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveRepaymentAsync()
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

        var editing = EditingRow?.Repayment;
        var repayment = new CardRepayment
        {
            Id = editing?.Id ?? Guid.Empty,
            Date = DateOnly.FromDateTime(date),
            Amount = Math.Round((decimal)EntryAmount, 2, MidpointRounding.AwayFromZero),
            PaidFrom = EntryFrom.Account,
            Note = EntryNote,
        };
        var savedMonth = YearMonth.Of(repayment.Date);
        var where = savedMonth == Month ? string.Empty : $" in {savedMonth.DisplayName}";

        var (typedAmount, typedNote) = (EntryAmount, EntryNote);
        if (editing is null)
            (EntryAmount, EntryNote) = (double.NaN, string.Empty);

        var ok = await RunFileOperationAsync(() =>
        {
            if (editing is null)
                _store.Excel.AddCardRepayment(repayment);
            else
                _store.Excel.UpdateCardRepayment(YearMonth.Of(editing.Date), repayment);
        }, $"{(editing is null ? "Added" : "Saved")} repayment of {Pkr.Format(repayment.Amount)} {EntryFrom.Name.ToLowerInvariant()}{where}.");

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
    private void BeginEdit(CardRowViewModel row)
    {
        if (row.Repayment is not { } r)
            return;

        ErrorMessage = null;
        StatusMessage = null;
        EditingRow = row;
        EntryAmount = (double)r.Amount;
        EntryDate = r.Date.ToDateTime(TimeOnly.MinValue);
        EntryFrom = AccountOption.For(r.PaidFrom);
        EntryNote = r.Note;
        EntryFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ErrorMessage = null;
        EndEdit();
    }

    [RelayCommand]
    private async Task DeleteAsync(CardRowViewModel row)
    {
        if (row.Repayment is not { } r)
            return;

        var what = $"{Pkr.Format(r.Amount)} {AccountOption.For(r.PaidFrom).Name.ToLowerInvariant()} · " +
                   r.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (!await _dialogs.ConfirmAsync("Delete this repayment?",
                $"{what}\n\nThis removes the row from {YearMonth.Of(r.Date).FileName}.", "Delete"))
            return;

        var ok = await RunFileOperationAsync(() => _store.Excel.DeleteCardRepayment(YearMonth.Of(r.Date), r.Id),
            $"Deleted repayment of {Pkr.Format(r.Amount)}.");
        if (ok && EditingRow?.Repayment?.Id == r.Id)
            EndEdit();
    }

    private void EndEdit()
    {
        EditingRow = null;
        EntryAmount = double.NaN;
        EntryNote = string.Empty;
    }

    // ---- Navigation -----------------------------------------------------------

    [RelayCommand]
    private Task PreviousMonthAsync() => ShowMonthAsync(Month.AddMonths(-1));

    [RelayCommand]
    private Task NextMonthAsync() => ShowMonthAsync(Month.AddMonths(1));

    private bool CanGoToThisMonth() => !IsCurrentMonth;

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

    [RelayCommand]
    private Task ReloadAsync()
    {
        ErrorMessage = null;
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
