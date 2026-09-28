using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The quick-add form, which doubles as the edit form, and deleting.
/// </summary>
public partial class ExpensesViewModel
{
    // Id of the member picked in the form, so the choice survives a rename in Settings
    // (by the time we hear about a rename, the old name is already gone).
    private Guid? _selectedMemberId;

    // Form choices from before an edit started, restored when it ends.
    private (DateTime? Date, CategoryOption? Category, string? Member, PaymentOption Payment)? _beforeEdit;

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

    public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();

    public ObservableCollection<string> MemberOptions { get; } = new();

    public IReadOnlyList<PaymentOption> PaymentOptions => PaymentOption.All;

    public bool HasMembers => MemberOptions.Count > 0;

    public bool IsEditing => EditingRow is not null;

    public string FormTitle => IsEditing ? "Edit expense" : "Add expense";

    public string SaveButtonText => IsEditing ? "Save changes" : "Add";

    /// <summary>
    /// Raised after a save so the view can put the cursor back in the amount box.
    /// </summary>
    public event EventHandler? EntryFocusRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBudgetWarning))]
    private string? _budgetWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecurringNotice))]
    private string? _recurringNotice;

    public bool HasBudgetWarning => BudgetWarning is not null;

    public bool HasRecurringNotice => RecurringNotice is not null;

    // Typing again clears the last "Added …" message and budget warning.
    partial void OnEntryAmountChanged(double value)
    {
        StatusMessage = null;
        if (!double.IsNaN(value))
            BudgetWarning = null;
    }

    partial void OnEntryNoteChanged(string value) => StatusMessage = null;

    partial void OnEntryMemberChanged(string? value) =>
        _selectedMemberId = _settings.FamilyMembers.FirstOrDefault(m => m.Name == value)?.Id;

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

            _store.Invalidate(YearMonth.Of(saved.Date));
            if (editing is not null)
            {
                _store.Invalidate(editing.Month);
                EndEdit();
            }

            await LoadAsync();

            var what = string.IsNullOrEmpty(saved.Category) ? Pkr.Format(saved.Amount) : $"{Pkr.Format(saved.Amount)} · {saved.Category}";
            var where = Rows.Any(r => r.Expense.Id == saved.Id) ? string.Empty
                : IsInRange(saved.Date) ? " (hidden by your search or filters)"
                : $" in {YearMonth.Of(saved.Date).DisplayName}";
            StatusMessage = editing is null ? $"Added {what}{where}." : $"Saved {what}{where}.";
            BudgetWarning = BudgetWarningFor(saved);
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

    /// <summary>
    /// A warning if the saved expense took one of its budgets near or over the limit.
    /// </summary>
    private string? BudgetWarningFor(Expense saved)
    {
        var month = YearMonth.Of(saved.Date);
        var hits = BudgetService.Calculate(_settings.ResolvedBudgets(), _store.LoadMonth(month).Expenses)
            .Where(s => s.Budget.Covers(saved) && s.Level != BudgetLevel.Fine)
            .ToList();
        if (hits.Count == 0)
            return null;

        return string.Join(" ", hits.Select(s => s.Level == BudgetLevel.Over
            ? $"{s.Budget.Label} is over budget for {month.DisplayName}: {Pkr.Format(s.Spent)} of {Pkr.Format(s.Budget.Amount)}."
            : $"{s.Budget.Label} has used {DashboardViewModel.Percent(s.Share)} of its {Pkr.Format(s.Budget.Amount)} budget."));
    }

    /// <summary>
    /// Shows which recurring expenses were just added automatically, and reloads.
    /// </summary>
    public void ShowRecurringAdded(RecurringRunResult result)
    {
        if (result.Added.Count == 0)
            return;

        RecurringNotice = "Added automatically: " + string.Join(", ", result.Added.Select(e =>
            $"{e.Note} {Pkr.Format(e.Amount)} ({e.Date.ToString("d MMM", CultureInfo.InvariantCulture)})")) + ".";
        if (_hasLoaded)
            _ = LoadAsync();
    }

    [RelayCommand]
    private void DismissRecurringNotice() => RecurringNotice = null;

    private bool IsInRange(DateOnly date) =>
        TryGetRange(out var from, out var to, out _) && date >= from && date <= to;

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
            Pkr.Format(expense.Amount), row.CategoryName,
            expense.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture), expense.Note,
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
            _store.Invalidate(row.Month);
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

    /// <summary>
    /// Rebuilds the form's category and member lists after a Settings change,
    /// keeping the current choices (following renames) where they still exist.
    /// </summary>
    private void RefreshEntryOptions()
    {
        var category = EntryCategory;
        var memberId = _selectedMemberId;
        var member = EntryMember;

        RebuildOptions();

        EntryCategory = CategoryOptions.FirstOrDefault(o => category?.Id is not null && o.Id == category.Id)
                        ?? CategoryOptions.FirstOrDefault(o => o.Name == category?.Name)
                        ?? CategoryOptions.FirstOrDefault();
        EntryMember = _settings.FamilyMembers.FirstOrDefault(m => m.Id == memberId)?.Name
                      ?? (member is not null && MemberOptions.Contains(member) ? member : MemberOptions.FirstOrDefault());
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
