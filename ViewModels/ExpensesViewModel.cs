using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The Expenses page: one month's expenses, a quick-add form that doubles as
/// the edit form, and month navigation.
/// </summary>
public partial class ExpensesViewModel : PageViewModelBase
{
    private readonly ExcelService _excel;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly TimeProvider _clock;

    // Increments on every load so a slow load for a month the user has
    // already navigated away from doesn't overwrite the newer one.
    private int _loadVersion;
    private bool _hasLoaded;

    // Id of the member picked in the form, so the choice survives a rename in Settings
    // (by the time we hear about a rename, the old name is already gone).
    private Guid? _selectedMemberId;

    // Form choices from before an edit started, restored when it ends.
    private (DateTime? Date, CategoryOption? Category, string? Member, PaymentOption Payment)? _beforeEdit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(IsCurrentMonth), nameof(WorkbookName), nameof(EmptyText))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand))]
    private YearMonth _month;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkbookCommand))]
    private bool _fileExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(ProblemSummary))]
    private IReadOnlyList<SheetProblem> _problems = Array.Empty<SheetProblem>();

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

    // ---- Entry form ----

    /// <summary>
    /// NaN while the box is empty (that's how NumberBox represents "no value").
    /// </summary>
    [ObservableProperty]
    private double _entryAmount = double.NaN;

    [ObservableProperty]
    private DateTime? _entryDate;

    [ObservableProperty]
    private CategoryOption? _entryCategory;

    [ObservableProperty]
    private string? _entryMember;

    [ObservableProperty]
    private PaymentOption _entryPayment = PaymentOption.For(PaymentMethod.Cash);

    [ObservableProperty]
    private string _entryNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveButtonText))]
    private ExpenseRowViewModel? _editingRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEntryCommand), nameof(ReloadCommand), nameof(PreviousMonthCommand),
        nameof(NextMonthCommand), nameof(GoToThisMonthCommand))]
    private bool _isBusy;

    public ExpensesViewModel(ExcelService excel, SettingsService settings, IDialogService dialogs,
        ILauncherService launcher, TimeProvider? clock = null)
    {
        _excel = excel;
        _settings = settings;
        _dialogs = dialogs;
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;

        _month = YearMonth.Of(Today);
        _entryDate = Today.ToDateTime(TimeOnly.MinValue);

        RebuildOptions();
        EntryCategory = CategoryOptions.FirstOrDefault();
        EntryMember = MemberOptions.FirstOrDefault();

        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));

        // Names or looks changed in Settings (and renames may have rewritten this year's files).
        _settings.FamilyMembersChanged += (_, _) => OnSettingsChanged();
        _settings.CategoriesChanged += (_, _) => OnSettingsChanged();
    }

    public override string Title => "Expenses";

    public override string Description => "Log and edit this month's spending.";

    public ObservableCollection<ExpenseRowViewModel> Rows { get; } = new();

    public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();

    public ObservableCollection<string> MemberOptions { get; } = new();

    public IReadOnlyList<PaymentOption> PaymentOptions => PaymentOption.All;

    public string MonthTitle => Month.DisplayName;

    public bool IsCurrentMonth => Month == YearMonth.Of(Today);

    public string WorkbookName => Month.FileName;

    public string EmptyText => $"No expenses in {Month.DisplayName} yet.";

    public bool HasRows => Rows.Count > 0;

    public bool HasProblems => Problems.Count > 0;

    public string ProblemSummary => Problems.Count == 1
        ? $"1 row in {WorkbookName} couldn't be read and isn't included below. Fix it in Excel, then reload."
        : $"{Problems.Count} rows in {WorkbookName} couldn't be read and aren't included below. Fix them in Excel, then reload.";

    public bool HasError => ErrorMessage is not null;

    public bool HasMembers => MemberOptions.Count > 0;

    public bool IsEditing => EditingRow is not null;

    public string FormTitle => IsEditing ? "Edit expense" : "Add expense";

    public string SaveButtonText => IsEditing ? "Save changes" : "Add";

    /// <summary>
    /// Raised after a save so the view can put the cursor back in the amount box.
    /// </summary>
    public event EventHandler? EntryFocusRequested;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo()
    {
        // Reload every visit: cheap, and picks up edits made in Excel meanwhile.
        _ = LoadAsync();
    }

    // Typing again clears the last "Added …" message.
    partial void OnEntryAmountChanged(double value) => StatusMessage = null;

    partial void OnEntryNoteChanged(string value) => StatusMessage = null;

    partial void OnEntryMemberChanged(string? value) =>
        _selectedMemberId = _settings.FamilyMembers.FirstOrDefault(m => m.Name == value)?.Id;

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var month = Month;

        try
        {
            var data = await Task.Run(() => _excel.LoadExpenses(month));
            if (version != _loadVersion)
                return;

            ApplyData(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            if (version != _loadVersion)
                return;

            ApplyData(new ExpenseSheetData(month, File.Exists(_excel.GetMonthFilePath(month)),
                Array.Empty<Expense>(), Array.Empty<SheetProblem>()));
            ErrorMessage = ex.Message;
        }

        _hasLoaded = true;
    }

    private void ApplyData(ExpenseSheetData data)
    {
        FileExists = data.FileExists;
        Problems = data.Problems;

        // Newest first. The file is oldest-first with same-day entries in the
        // order they were added, so reversing puts the latest entry on top.
        Rows.Clear();
        foreach (var expense in data.Expenses.Reverse())
            Rows.Add(new ExpenseRowViewModel(expense, LookUpCategory(expense.Category)));

        var byMethod = data.Expenses.GroupBy(e => e.PaymentMethod).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        TotalText = Pkr.Format(data.Expenses.Sum(e => e.Amount));
        CashText = Pkr.Format(byMethod.GetValueOrDefault(PaymentMethod.Cash));
        BankText = Pkr.Format(byMethod.GetValueOrDefault(PaymentMethod.Bank));
        CardText = Pkr.Format(byMethod.GetValueOrDefault(PaymentMethod.CreditCard));
        CountText = data.Expenses.Count switch
        {
            0 => "No expenses",
            1 => "1 expense",
            var n => $"{n} expenses",
        };
    }

    private bool CanUseFile() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task ReloadAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;
        return LoadAsync();
    }

    // ---- Month navigation -------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task PreviousMonthAsync() => ChangeMonthAsync(Month.AddMonths(-1));

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task NextMonthAsync() => ChangeMonthAsync(Month.AddMonths(1));

    private bool CanGoToThisMonth() => !IsBusy && !IsCurrentMonth;

    [RelayCommand(CanExecute = nameof(CanGoToThisMonth))]
    private Task GoToThisMonthAsync() => ChangeMonthAsync(YearMonth.Of(Today));

    private Task ChangeMonthAsync(YearMonth month)
    {
        if (IsEditing)
            CancelEdit();

        Month = month;
        ErrorMessage = null;
        StatusMessage = null;

        // New entries default to today in the current month, otherwise to the
        // 1st of the month being viewed.
        EntryDate = (month.Contains(Today) ? Today : month.FirstDay).ToDateTime(TimeOnly.MinValue);
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

    // ---- Add / edit / delete ----------------------------------------------

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

        var editing = EditingRow;
        var expense = new Expense
        {
            Id = editing?.Expense.Id ?? Guid.Empty,
            Date = DateOnly.FromDateTime(date),
            Amount = Math.Round((decimal)EntryAmount, 2, MidpointRounding.AwayFromZero),
            Category = EntryCategory?.Name ?? string.Empty,
            FamilyMember = EntryMember ?? string.Empty,
            PaymentMethod = EntryPayment.Method,
            Note = EntryNote,
        };

        // Clear the quick-add fields straight away, so someone who types the
        // next amount while this one is still saving doesn't have it wiped
        // when the save finishes. They're put back if the save fails.
        var (typedAmount, typedNote) = (EntryAmount, EntryNote);
        if (editing is null)
            ClearAmountAndNote();

        IsBusy = true;
        try
        {
            var saved = editing is null
                ? await Task.Run(() => _excel.AddExpense(expense))
                : await Task.Run(() => _excel.UpdateExpense(editing.Month, expense));

            var savedMonth = YearMonth.Of(saved.Date);
            var what = string.IsNullOrEmpty(saved.Category) ? Pkr.Format(saved.Amount) : $"{Pkr.Format(saved.Amount)} · {saved.Category}";
            var where = savedMonth == Month ? string.Empty : $" in {savedMonth.DisplayName}";

            if (editing is not null)
                EndEdit();

            await LoadAsync();
            StatusMessage = editing is null ? $"Added {what}{where}." : $"Saved {what}{where}.";
            EntryFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException ex)
        {
            // Drop the " (Parameter 'x')" suffix; the message is for people.
            ErrorMessage = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);
            RestoreTyped();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            RestoreTyped();
        }
        catch (KeyNotFoundException ex)
        {
            // The row vanished from the file (edited in Excel). Show the file as it is now.
            ErrorMessage = ex.Message;
            EndEdit();
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }

        // Put back what was typed so the user can retry, unless they've already started on the next one.
        void RestoreTyped()
        {
            if (editing is null && double.IsNaN(EntryAmount) && EntryNote.Length == 0)
                (EntryAmount, EntryNote) = (typedAmount, typedNote);
        }
    }

    [RelayCommand]
    private void BeginEdit(ExpenseRowViewModel row)
    {
        if (!IsEditing)
            _beforeEdit = (EntryDate, EntryCategory, EntryMember, EntryPayment);

        ErrorMessage = null;
        StatusMessage = null;
        EditingRow = row;

        var expense = row.Expense;
        EntryAmount = (double)expense.Amount;
        EntryDate = expense.Date.ToDateTime(TimeOnly.MinValue);
        EntryCategory = EnsureCategoryOption(expense.Category);
        EntryMember = EnsureMemberOption(expense.FamilyMember);
        EntryPayment = PaymentOption.For(expense.PaymentMethod);
        EntryNote = expense.Note;

        EntryFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ErrorMessage = null;
        EndEdit();
    }

    [RelayCommand]
    private async Task DeleteAsync(ExpenseRowViewModel row)
    {
        var expense = row.Expense;
        var details = string.Join(" · ", new[]
        {
            Pkr.Format(expense.Amount), row.CategoryName, expense.Date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), expense.Note,
        }.Where(s => !string.IsNullOrWhiteSpace(s) && s != "—"));

        var confirmed = await _dialogs.ConfirmAsync(
            "Delete this expense?",
            $"{details}\n\nThis removes the row from {row.Month.FileName}.",
            "Delete");
        if (!confirmed)
            return;

        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        try
        {
            await Task.Run(() => _excel.DeleteExpense(row.Month, expense.Id));
            if (EditingRow?.Expense.Id == expense.Id)
                EndEdit();
            StatusMessage = $"Deleted {Pkr.Format(expense.Amount)}.";
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
    }

    private void EndEdit()
    {
        if (EditingRow is null)
            return;

        EditingRow = null;
        ClearAmountAndNote();
        if (_beforeEdit is { } before)
        {
            EntryDate = before.Date;
            EntryCategory = before.Category is null ? null : EnsureCategoryOption(before.Category.Name);
            EntryMember = before.Member;
            EntryPayment = before.Payment;
            _beforeEdit = null;
        }
    }

    private void ClearAmountAndNote()
    {
        EntryAmount = double.NaN;
        EntryNote = string.Empty;
    }

    // ---- Dropdown options ---------------------------------------------------

    private void OnSettingsChanged()
    {
        var category = EntryCategory;
        var memberId = _selectedMemberId;
        var member = EntryMember;

        RebuildOptions();

        // Keep the current choice (following a rename) if it still exists; otherwise fall back to the first.
        EntryCategory = CategoryOptions.FirstOrDefault(o => category?.Id is not null && o.Id == category.Id)
                        ?? CategoryOptions.FirstOrDefault(o => o.Name == category?.Name)
                        ?? CategoryOptions.FirstOrDefault();
        EntryMember = _settings.FamilyMembers.FirstOrDefault(m => m.Id == memberId)?.Name
                      ?? (member is not null && MemberOptions.Contains(member) ? member : MemberOptions.FirstOrDefault());

        if (_hasLoaded)
            _ = LoadAsync();
    }

    private void RebuildOptions()
    {
        CategoryOptions.Clear();
        foreach (var c in _settings.Categories)
            CategoryOptions.Add(new CategoryOption(c.Name, c.Icon, c.Color, c.Id));

        MemberOptions.Clear();
        foreach (var m in _settings.FamilyMembers)
            MemberOptions.Add(m.Name);

        OnPropertyChanged(nameof(HasMembers));
    }

    private CategoryOption LookUpCategory(string name) =>
        _settings.FindCategory(name) is { } c
            ? new CategoryOption(c.Name, c.Icon, c.Color, c.Id)
            : CategoryOption.Unknown(name);

    /// <summary>
    /// Returns the dropdown option for a category name, adding one if the name
    /// came from a workbook but isn't in Settings, so editing that expense doesn't
    /// silently change its category.
    /// </summary>
    private CategoryOption? EnsureCategoryOption(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var existing = CategoryOptions.FirstOrDefault(o =>
            string.Equals(o.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
            return existing;

        var option = CategoryOption.Unknown(name);
        CategoryOptions.Add(option);
        return option;
    }

    private string? EnsureMemberOption(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var existing = MemberOptions.FirstOrDefault(m => string.Equals(m, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
            return existing;

        MemberOptions.Add(name);
        OnPropertyChanged(nameof(HasMembers));
        return name;
    }
}
