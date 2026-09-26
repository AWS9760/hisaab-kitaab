using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One row in the Settings family member list, including its inline rename state.
/// </summary>
public partial class FamilyMemberItemViewModel : ViewModelBase
{
    private readonly SettingsViewModel _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _name;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = string.Empty;

    public FamilyMemberItemViewModel(SettingsViewModel owner, Guid id, string name)
    {
        _owner = owner;
        Id = id;
        _name = name;
    }

    public Guid Id { get; }

    /// <summary>
    /// First letter of the name, shown in the avatar circle.
    /// </summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : char.ToUpper(Name[0]).ToString();

    [RelayCommand]
    private void BeginEdit() => _owner.BeginRename(this);

    [RelayCommand]
    private void CommitEdit() => _owner.CommitRename(this);

    [RelayCommand]
    private void CancelEdit() => _owner.CancelRename(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);
}
