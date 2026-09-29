using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// A kind of zakat entry as offered in the form.
/// </summary>
public record ZakatTypeOption(ZakatEntryType Type, string Name, string Icon)
{
    public static readonly IReadOnlyList<ZakatTypeOption> All = new ZakatTypeOption[]
    {
        new(ZakatEntryType.SetAside, "Set aside", "📥"),
        new(ZakatEntryType.Given, "Given", "🤲"),
    };

    public static ZakatTypeOption For(ZakatEntryType type) => All.First(o => o.Type == type);
}

/// <summary>
/// One row of the zakat log.
/// </summary>
public class ZakatRowViewModel
{
    public ZakatRowViewModel(ZakatLine line)
    {
        Line = line;
        var e = line.Entry;
        if (e.Type == ZakatEntryType.SetAside)
        {
            Icon = "📥";
            Color = "#0D9488";
            Title = "Set aside";
            Detail = e.Note;
        }
        else
        {
            Icon = "🤲";
            Color = "#7C3AED";
            Title = string.IsNullOrWhiteSpace(e.Recipient) ? "Given" : $"Given to {e.Recipient}";
            var from = e.PaidFrom == Account.Bank ? "from bank" : "in cash";
            Detail = string.IsNullOrWhiteSpace(e.Note) ? from : $"{e.Note} · {from}";
        }
    }

    public ZakatLine Line { get; }

    public ZakatEntry Entry => Line.Entry;

    public string Icon { get; }

    public string Color { get; }

    public string Title { get; }

    public string Detail { get; }

    public string DateText => Entry.Date.ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture);

    public string ChangeText => Pkr.FormatChange(Entry.Type == ZakatEntryType.SetAside ? Entry.Amount : -Entry.Amount);

    public string RemainingText => Pkr.Format(Line.Remaining);

    public bool IsRemainingNegative => Line.Remaining < 0;
}

/// <summary>
/// The Zakat page: one zakat year at a time (running over dates the user
/// picks), what's been set aside and given, and what's left. Zakat given
/// from the bank or in cash comes off those balances on Bank &amp; Cash.
/// </summary>
public partial class ZakatViewModel : PageViewModelBase
{
    /// <summary>
    /// Days in an Islamic (lunar) year, for the "Islamic year" shortcut.
    /// </summary>
    public const int IslamicYearDays = 354;

    private readonly ZakatService _service;
    private readonly WorkbookStore _store;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly TimeProvider _clock;
    private int _loadVersion;
    private bool _yearChosen;

    // The zakat year running today, worked out in the background on each load.
    private int _currentYear;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YearTitle), nameof(WorkbookName), nameof(IsCurrentYear))]
    [NotifyCanExecuteChangedFor(nameof(GoToCurrentYearCommand))]
    private int _year;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkbookCommand))]
    private bool _fileExists;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEntryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    // ---- Summary ----

    [ObservableProperty]
    private string _periodText = string.Empty;

    [ObservableProperty]
    private bool _periodIsSaved;

    [ObservableProperty]
    private string _remainingText = Pkr.Format(0);

    [ObservableProperty]
    private bool _isOverGiven;

    [ObservableProperty]
    private string _remainingDetail = string.Empty;

    [ObservableProperty]
    private string _takenOutText = Pkr.Format(0);

    [ObservableProperty]
    private string _givenText = Pkr.Format(0);

    [ObservableProperty]
    private string _givenDetail = string.Empty;

    [ObservableProperty]
    private string _carriedText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _breakdown = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private IReadOnlyList<string> _problemLines = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private IReadOnlyList<ZakatRowViewModel> _rows = Array.Empty<ZakatRowViewModel>();

    // ---- Editors ----

    [ObservableProperty]
    private DateTime? _periodStartInput;

    [ObservableProperty]
    private DateTime? _periodEndInput;

    [ObservableProperty]
    private double _carriedInput;

    [ObservableProperty]
    private bool _carriedIsManual;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGiven))]
    private ZakatTypeOption _entryType = ZakatTypeOption.For(ZakatEntryType.Given);

    [ObservableProperty]
    private double _entryAmount = double.NaN;

    [ObservableProperty]
    private DateTime? _entryDate;

    [ObservableProperty]
    private AccountOption _entryFrom = AccountOption.For(Account.Cash);

    [ObservableProperty]
    private string _entryRecipient = string.Empty;

    [ObservableProperty]
    private string _entryNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveButtonText))]
    private ZakatRowViewModel? _editingRow;

    public ZakatViewModel(ZakatService service, IDialogService dialogs, ILauncherService launcher, TimeProvider? clock = null)
    {
        _service = service;
        _store = service.Store;
        _dialogs = dialogs;
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;
        _year = Today.Year;
        _currentYear = Today.Year;
        _entryDate = Today.ToDateTime(TimeOnly.MinValue);
    }

    public override string Title => "Zakat";

    public override string Description => "Zakat set aside and given, one zakat year at a time. Each year has its own workbook.";

    public IReadOnlyList<ZakatTypeOption> TypeOptions => ZakatTypeOption.All;

    public IReadOnlyList<AccountOption> FromOptions => AccountOption.All;

    public string YearTitle => $"Zakat {Year}";

    public string WorkbookName => $"Zakat_{Year}.xlsx";

    public bool IsCurrentYear => Year == _currentYear;

    public bool IsGiven => EntryType.Type == ZakatEntryType.Given;

    public bool HasError => ErrorMessage is not null;

    public bool HasWarning => Warning is not null;

    public bool HasProblems => ProblemLines.Count > 0;

    public bool HasRows => Rows.Count > 0;

    public bool IsEditing => EditingRow is not null;

    public string FormTitle => IsEditing ? "Edit entry" : "Log zakat";

    public string SaveButtonText => IsEditing ? "Save changes" : "Add";

    /// <summary>
    /// The year most recently shown.
    /// </summary>
    public ZakatYear? Shown { get; private set; }

    public event EventHandler? EntryFocusRequested;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo() => _ = LoadAsync();

    partial void OnEntryAmountChanged(double value) => StatusMessage = null;

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;

        // The first time, show the zakat year running today (it may have started last year).
        if (!_yearChosen)
        {
            _yearChosen = true;
            Year = await Task.Run(() => _service.YearFor(Today));
            if (version != _loadVersion)
                return;
        }

        var year = Year;
        var (shown, data) = await Task.Run(() =>
        {
            try
            {
                _service.SyncCarriedAfter(year - 1);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // e.g. open in Excel; the app's figures are still right.
            }

            _currentYear = _service.YearFor(Today);
            return (_service.GetYear(year), _store.LoadZakat(year));
        });

        if (version != _loadVersion)
            return;

        Shown = shown;
        FileExists = data.FileExists;
        if (data.Error is not null)
            ErrorMessage = data.Error;
        ProblemLines = data.Problems.Select(p => $"{WorkbookName}, row {p.RowNumber}: {p.Message}").ToList();
        OnPropertyChanged(nameof(IsCurrentYear));
        GoToCurrentYearCommand.NotifyCanExecuteChanged();

        ShowSummary(shown);
        Rows = shown.Ledger.Select(l => new ZakatRowViewModel(l)).Reverse().ToList();

        if (!IsEditing && (EntryDate is not { } date || !shown.Period.Contains(DateOnly.FromDateTime(date))))
            EntryDate = DefaultEntryDate(shown.Period).ToDateTime(TimeOnly.MinValue);
    }

    private void ShowSummary(ZakatYear y)
    {
        PeriodIsSaved = y.PeriodIsSaved;
        PeriodText = y.Period.ToString();
        (PeriodStartInput, PeriodEndInput) = (y.Period.Start.ToDateTime(TimeOnly.MinValue), y.Period.End.ToDateTime(TimeOnly.MinValue));

        RemainingText = Pkr.Format(Math.Abs(y.Remaining));
        IsOverGiven = y.Remaining < 0;
        RemainingDetail = y.Remaining < 0 ? "given more than was taken out"
            : y.TakenOut == 0 ? "nothing set aside yet"
            : y.Remaining == 0 ? "all of it has been given"
            : "still to give";
        TakenOutText = Pkr.Format(y.TakenOut);
        GivenText = Pkr.Format(y.Given);
        GivenDetail = y.Given == 0 ? "nothing given yet"
            : string.Join(" · ", new[] { ("from bank", y.GivenFromBank), ("in cash", y.GivenFromCash) }
                .Where(p => p.Item2 != 0).Select(p => $"{Pkr.Format(p.Item2)} {p.Item1}"));

        CarriedText = $"Carried from {Year - 1}: {Pkr.Format(y.Carried)} · " + (y.CarriedIsManual ? "set by you" : "what was left");
        (CarriedInput, CarriedIsManual) = ((double)y.Carried, y.CarriedIsManual);
        Breakdown = new[] { $"Set aside this year {Pkr.Format(y.SetAside)}" };

        Warning = y.PeriodIsSaved ? null
            : $"The {Year} zakat year's dates haven't been set. It's shown as {y.Period}; change the dates above if yours are different.";
    }

    private DateOnly DefaultEntryDate(ZakatPeriod period) =>
        period.Contains(Today) ? Today : Today < period.Start ? period.Start : period.End;

    // ---- Zakat year dates -------------------------------------------------------

    [RelayCommand]
    private void UseFullYear()
    {
        if (PeriodStartInput is { } start)
            PeriodEndInput = ZakatPeriod.YearFrom(DateOnly.FromDateTime(start)).End.ToDateTime(TimeOnly.MinValue);
    }

    [RelayCommand]
    private void UseIslamicYear()
    {
        if (PeriodStartInput is { } start)
            PeriodEndInput = start.AddDays(IslamicYearDays - 1);
    }

    [RelayCommand]
    private Task SavePeriodAsync()
    {
        if (PeriodStartInput is not { } start || PeriodEndInput is not { } end)
        {
            ErrorMessage = "Pick the day the zakat year starts and the day it ends.";
            return Task.CompletedTask;
        }

        var period = new ZakatPeriod(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
        if (_service.ValidatePeriod(Year, period) is { } problem)
        {
            ErrorMessage = problem;
            return Task.CompletedTask;
        }

        var year = Year;
        return RunFileOperationAsync(() => { _service.SetPeriod(year, period); return ZakatChangeResult.Done; },
            $"The {year} zakat year now runs {period}.");
    }

    // ---- Carried from last year ---------------------------------------------------

    [RelayCommand]
    private Task SetCarriedAsync()
    {
        if (double.IsNaN(CarriedInput) || Math.Abs(CarriedInput) >= (double)ExcelService.MaxAmount)
        {
            ErrorMessage = "Enter the amount carried from last year.";
            return Task.CompletedTask;
        }

        var amount = Math.Round((decimal)CarriedInput, 2, MidpointRounding.AwayFromZero);
        var year = Year;
        return RunFileOperationAsync(() => { _service.SetCarried(year, amount); return ZakatChangeResult.Done; },
            $"Carried from {year - 1} set to {Pkr.Format(amount)}.");
    }

    [RelayCommand]
    private Task CarryForwardAsync()
    {
        var year = Year;
        return RunFileOperationAsync(() => { _service.CarryForward(year); return ZakatChangeResult.Done; },
            $"Now carrying in what was left from {year - 1}.");
    }

    // ---- Entries --------------------------------------------------------------

    [RelayCommand]
    private void GiveRemaining()
    {
        if (Shown is { Remaining: > 0 } shown)
        {
            EntryType = ZakatTypeOption.For(ZakatEntryType.Given);
            EntryAmount = (double)shown.Remaining;
        }
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
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
        var entry = new ZakatEntry
        {
            Id = editing?.Id ?? Guid.Empty,
            Date = DateOnly.FromDateTime(date),
            Type = EntryType.Type,
            Amount = Math.Round((decimal)EntryAmount, 2, MidpointRounding.AwayFromZero),
            PaidFrom = IsGiven ? EntryFrom.Account : null,
            Recipient = IsGiven ? EntryRecipient : string.Empty,
            Note = EntryNote,
        };

        var what = entry.Type == ZakatEntryType.SetAside
            ? $"{Pkr.Format(entry.Amount)} set aside"
            : $"{Pkr.Format(entry.Amount)} given{(string.IsNullOrWhiteSpace(entry.Recipient) ? "" : $" to {entry.Recipient.Trim()}")} {EntryFrom.Name.ToLowerInvariant()}";

        var typed = (EntryAmount, EntryRecipient, EntryNote);
        if (editing is null)
            (EntryAmount, EntryRecipient, EntryNote) = (double.NaN, string.Empty, string.Empty);

        var year = Year;
        var ok = await RunFileOperationAsync(
            () => editing is null ? _service.Add(year, entry) : _service.Update(year, editing, entry),
            $"{(editing is null ? "Added" : "Saved")}: {what}.");

        if (ok)
        {
            if (editing is not null)
                EndEdit();
            EntryFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (editing is null && double.IsNaN(EntryAmount) && EntryRecipient.Length == 0 && EntryNote.Length == 0)
        {
            (EntryAmount, EntryRecipient, EntryNote) = typed;
        }
    }

    [RelayCommand]
    private void BeginEdit(ZakatRowViewModel row)
    {
        var e = row.Entry;
        ErrorMessage = null;
        StatusMessage = null;
        EditingRow = row;
        EntryType = ZakatTypeOption.For(e.Type);
        EntryAmount = (double)e.Amount;
        EntryDate = e.Date.ToDateTime(TimeOnly.MinValue);
        EntryFrom = AccountOption.For(e.PaidFrom ?? Account.Cash);
        EntryRecipient = e.Recipient;
        EntryNote = e.Note;
        EntryFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ErrorMessage = null;
        EndEdit();
    }

    [RelayCommand]
    private async Task DeleteAsync(ZakatRowViewModel row)
    {
        var e = row.Entry;
        var what = $"{row.Title}, {Pkr.Format(e.Amount)} · {e.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        if (!await _dialogs.ConfirmAsync("Delete this entry?", $"{what}\n\nThis removes the row from {WorkbookName}.", "Delete"))
            return;

        var year = Year;
        var ok = await RunFileOperationAsync(() => _service.Delete(year, e), $"Deleted: {row.Title.ToLowerInvariant()}, {Pkr.Format(e.Amount)}.");
        if (ok && EditingRow?.Entry.Id == e.Id)
            EndEdit();
    }

    private void EndEdit()
    {
        EditingRow = null;
        EntryAmount = double.NaN;
        EntryRecipient = string.Empty;
        EntryNote = string.Empty;
    }

    // ---- Navigation -----------------------------------------------------------

    [RelayCommand]
    private Task PreviousYearAsync() => ShowYearAsync(Year - 1);

    [RelayCommand]
    private Task NextYearAsync() => ShowYearAsync(Year + 1);

    private bool CanGoToCurrentYear() => !IsCurrentYear;

    [RelayCommand(CanExecute = nameof(CanGoToCurrentYear))]
    private Task GoToCurrentYearAsync() => ShowYearAsync(_currentYear);

    private Task ShowYearAsync(int year)
    {
        if (IsEditing)
            EndEdit();
        _yearChosen = true;
        Year = year;
        ErrorMessage = null;
        StatusMessage = null;
        EntryDate = null;
        return LoadAsync();
    }

    [RelayCommand]
    private Task ReloadAsync()
    {
        ErrorMessage = null;
        _store.InvalidateZakat(Year);
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(FileExists))]
    private void OpenWorkbook()
    {
        try
        {
            _launcher.OpenFile(_store.Excel.GetZakatFilePath(Year));
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
    private async Task<bool> RunFileOperationAsync(Func<ZakatChangeResult> operation, string successMessage)
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        ZakatChangeResult? result = null;
        try
        {
            result = await Task.Run(operation);
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
        if (result is null)
            return false;

        StatusMessage = successMessage;
        if (result.MonthsNotUpdated.Count > 0)
            Warning = $"{string.Join(", ", result.MonthsNotUpdated)} couldn't be updated (open in Excel?). Balances in the app are right; " +
                      "its Bank & Cash sheet catches up the next time the app saves it or starts.";
        return true;
    }
}
