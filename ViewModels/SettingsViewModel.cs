using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

public partial class SettingsViewModel : PageViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private string _newMemberName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public SettingsViewModel(SettingsService settings, IDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;

        foreach (var member in settings.FamilyMembers)
            Members.Add(new FamilyMemberItemViewModel(this, member.Id, member.Name));

        Members.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMembers));
    }

    public override string Title => "Settings";

    public override string Description => "Family members and app preferences.";

    public ObservableCollection<FamilyMemberItemViewModel> Members { get; } = new();

    public bool HasMembers => Members.Count > 0;

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Shown when the settings file on disk was unreadable at startup.
    /// </summary>
    public string? LoadWarning => _settings.LoadWarning;

    public bool HasLoadWarning => LoadWarning is not null;

    public string SettingsFilePath => _settings.FilePath;

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

        item.Name = newName;
        item.IsEditing = false;
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
