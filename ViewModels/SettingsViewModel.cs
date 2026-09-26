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

    // A rename whose workbook update partly failed, kept so the user can retry.
    private (int Year, string OldName, string NewName)? _pendingRename;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private string _newMemberName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    // Result of pushing a rename into the Excel files.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameSummary))]
    private string? _renameSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameWarning))]
    private string? _renameWarning;

    public SettingsViewModel(SettingsService settings, IDialogService dialogs, ExcelService excel, TimeProvider? clock = null)
    {
        _settings = settings;
        _dialogs = dialogs;
        _excel = excel;
        _clock = clock ?? TimeProvider.System;

        foreach (var member in settings.FamilyMembers)
            Members.Add(new FamilyMemberItemViewModel(this, member.Id, member.Name));

        Members.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMembers));
    }

    public override string Title => "Settings";

    public override string Description => "Family members and app preferences.";

    public ObservableCollection<FamilyMemberItemViewModel> Members { get; } = new();

    public bool HasMembers => Members.Count > 0;

    public bool HasError => ErrorMessage is not null;

    public bool HasRenameSummary => RenameSummary is not null;

    public bool HasRenameWarning => RenameWarning is not null;

    /// <summary>
    /// Shown when the settings file on disk was unreadable at startup.
    /// </summary>
    public string? LoadWarning => _settings.LoadWarning;

    public bool HasLoadWarning => LoadWarning is not null;

    public string SettingsFilePath => _settings.FilePath;

    public string DataFolder => _excel.DataFolder;

    public int MaxNameLength => SettingsService.MaxMemberNameLength;

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

        if (!TrySave(() => _settings.AddFamilyMember(NewMemberName), out var member))
            return;

        Members.Add(new FamilyMemberItemViewModel(this, member.Id, member.Name));
        NewMemberName = string.Empty;
    }

    internal void BeginRename(FamilyMemberItemViewModel item)
    {
        // Only one row is editable at a time.
        foreach (var other in Members.Where(m => m != item && m.IsEditing))
            other.IsEditing = false;

        ErrorMessage = null;
        RenameSummary = null;
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
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        if (!TrySave(() => { _settings.RenameFamilyMember(item.Id, newName); return true; }, out _))
            return;

        var oldName = item.Name;
        item.Name = newName;
        item.IsEditing = false;

        RenameInWorkbooks(_clock.GetLocalNow().Year, oldName, newName);
    }

    /// <summary>
    /// Updates the member's name on past expenses in this year's workbooks.
    /// Earlier years keep the name they were recorded with.
    /// </summary>
    private void RenameInWorkbooks(int year, string oldName, string newName)
    {
        RenameSummary = null;
        RenameWarning = null;
        _pendingRename = null;

        var result = _excel.RenameFamilyMember(year, oldName, newName);

        if (result.FilesFailed.Count > 0)
        {
            _pendingRename = (year, oldName, newName);
            RenameWarning = $"Couldn't update {string.Join(", ", result.FilesFailed)}, so expenses there still say " +
                            $"\"{oldName}\". The file may be open in Excel: close it and choose Retry.";
        }

        if (result.RowsUpdated > 0)
        {
            RenameSummary = $"Renamed {Plural(result.RowsUpdated, "expense")} in {year}'s " +
                            $"{Plural(result.FilesUpdated.Count, "workbook")}.";
        }

        RetryRenameCommand.NotifyCanExecuteChanged();
    }

    private bool CanRetryRename() => _pendingRename is not null;

    [RelayCommand(CanExecute = nameof(CanRetryRename))]
    private void RetryRename()
    {
        if (_pendingRename is { } pending)
            RenameInWorkbooks(pending.Year, pending.OldName, pending.NewName);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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

        if (!TrySave(() => { _settings.RemoveFamilyMember(item.Id); return true; }, out _))
            return;

        Members.Remove(item);
    }

    private bool TrySave<T>(Func<T> action, out T result)
    {
        try
        {
            result = action();
            ErrorMessage = null;
            return true;
        }
        catch (IOException ex)
        {
            result = default!;
            ErrorMessage = $"Couldn't save settings: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            result = default!;
            ErrorMessage = $"Couldn't save settings: {ex.Message}";
            return false;
        }
    }
}
