using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

public partial class SettingsViewModel : PageViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ExcelService _excel;
    private readonly TimeProvider _clock;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private string _newMemberName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public SettingsViewModel(SettingsService settings, IDialogService dialogs, ExcelService excel, TimeProvider? clock = null,
        ReminderService? reminders = null, BackupService? backups = null, ILauncherService? launcher = null,
        Action? dataFolderChanged = null)
    {
        _settings = settings;
        _dialogs = dialogs;
        _excel = excel;
        _clock = clock ?? TimeProvider.System;

        foreach (var member in settings.FamilyMembers)
            Members.Add(new FamilyMemberItemViewModel(this, member.Id, member.Name));

        Members.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMembers));

        Categories = new CategorySettingsViewModel(settings, dialogs, excel, _clock);
        Budgets = new BudgetSettingsViewModel(settings);
        Recurring = new RecurringSettingsViewModel(settings, dialogs, _clock, reminders is null ? null : reminders.CheckAsync);
        Notifications = new NotificationSettingsViewModel(settings, reminders);
        launcher ??= new LauncherService();
        Backups = new BackupSettingsViewModel(settings, backups ?? new BackupService(settings.BackupOptions), excel, dialogs, launcher);
        Storage = new StorageSettingsViewModel(settings, excel, dialogs, launcher, dataFolderChanged);
        Storage.FoldersChanged += (_, _) => Backups.Refresh();
    }

    public override string Title => "Settings";

    public override string Description => "Family members, categories, budgets, recurring expenses, notifications, backups and where your data is kept.";

    public BudgetSettingsViewModel Budgets { get; }

    public RecurringSettingsViewModel Recurring { get; }

    public NotificationSettingsViewModel Notifications { get; }

    public BackupSettingsViewModel Backups { get; }

    public StorageSettingsViewModel Storage { get; }

    public override void OnNavigatedTo()
    {
        Backups.Refresh();
        Storage.Refresh();
    }

    public ObservableCollection<FamilyMemberItemViewModel> Members { get; } = new();

    public CategorySettingsViewModel Categories { get; }

    /// <summary>
    /// Outcome of carrying a member rename into this year's workbooks.
    /// </summary>
    public WorkbookRenameNotice RenameNotice { get; } = new();

    public bool HasMembers => Members.Count > 0;

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Shown when the settings file on disk was unreadable at startup.
    /// </summary>
    public string? LoadWarning => _settings.LoadWarning;

    public bool HasLoadWarning => LoadWarning is not null;

    // Stale errors disappear as soon as the user starts correcting them.
    partial void OnNewMemberNameChanged(string value) => ErrorMessage = null;

    private bool CanAddMember() => !string.IsNullOrWhiteSpace(NewMemberName);

    [RelayCommand(CanExecute = nameof(CanAddMember))]
    private void AddMember()
    {
        var error = _settings.ValidateMemberName(NewMemberName);
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        var name = NewMemberName;
        if (!SettingsSave.Try(() => _settings.AddFamilyMember(name), out var member, out error))
        {
            ErrorMessage = error;
            return;
        }

        Members.Add(new FamilyMemberItemViewModel(this, member.Id, member.Name));
        NewMemberName = string.Empty;
    }

    internal void BeginRename(FamilyMemberItemViewModel item)
    {
        // Only one row is editable at a time.
        foreach (var other in Members.Where(m => m != item && m.IsEditing))
            other.IsEditing = false;

        ErrorMessage = null;
        RenameNotice.ClearSummary();
        item.EditName = item.Name;
        item.IsEditing = true;
    }

    internal void CommitRename(FamilyMemberItemViewModel item)
    {
        if (!item.IsEditing)
            return;

        var newName = item.EditName.Trim();
        if (newName == item.Name)
        {
            CancelRename(item);
            return;
        }

        var error = _settings.ValidateMemberName(newName, item.Id);
        if (error is null)
            SettingsSave.Try(() => _settings.RenameFamilyMember(item.Id, newName), out error);
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        var oldName = item.Name;
        item.Name = newName;
        item.IsEditing = false;

        // Past expenses in this year's workbooks follow the new name; earlier years keep the old one.
        var year = _clock.GetLocalNow().Year;
        RenameNotice.Run(year, oldName, () => _excel.RenameFamilyMember(year, oldName, newName));
    }

    internal void CancelRename(FamilyMemberItemViewModel item)
    {
        item.IsEditing = false;
        ErrorMessage = null;
    }

    internal async Task RemoveAsync(FamilyMemberItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            $"Remove {item.Name}?",
            $"{item.Name} will no longer appear in the family member list for new expenses. " +
            "Expenses already recorded for them stay in your monthly files.",
            "Remove");
        if (!confirmed)
            return;

        if (!SettingsSave.Try(() => _settings.RemoveFamilyMember(item.Id), out var error))
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        Members.Remove(item);
    }
}

/// <summary>
/// Runs a settings change and turns file errors into a message for the UI.
/// </summary>
internal static class SettingsSave
{
    public static bool Try<T>(Func<T> action, out T result, out string? error)
    {
        try
        {
            result = action();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = default!;
            error = $"Couldn't save settings: {ex.Message}";
            return false;
        }
    }

    public static bool Try(Action action, out string? error) =>
        Try(() => { action(); return true; }, out _, out error);

    /// <summary>
    /// Like <see cref="Try(Action, out string?)"/>, but also turns validation
    /// errors (ArgumentException) and missing items into messages.
    /// </summary>
    public static bool TryAny(Action action, out string? error)
    {
        try
        {
            if (Try(action, out error))
                return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);
        }
        catch (KeyNotFoundException)
        {
            error = "That item no longer exists; it may have been removed.";
        }

        return false;
    }
}
