using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One row in the Settings category list: name, icon, colour and inline rename state.
/// </summary>
public partial class CategoryItemViewModel : ViewModelBase
{
    private readonly CategorySettingsViewModel _owner;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _icon;

    [ObservableProperty]
    private string _color;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = string.Empty;

    public CategoryItemViewModel(CategorySettingsViewModel owner, Category category)
    {
        _owner = owner;
        Id = category.Id;
        _name = category.Name;
        _icon = category.Icon;
        _color = category.Color;
    }

    public Guid Id { get; }

    public static IReadOnlyList<string> IconChoices => CategoryStyles.Icons;

    public static IReadOnlyList<string> ColorChoices => CategoryStyles.Colors;

    [RelayCommand]
    private void BeginEdit() => _owner.BeginRename(this);

    [RelayCommand]
    private void CommitEdit() => _owner.CommitRename(this);

    [RelayCommand]
    private void CancelEdit() => _owner.CancelRename(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    [RelayCommand]
    private void SetIcon(string icon) => _owner.SetAppearance(this, icon, Color);

    [RelayCommand]
    private void SetColor(string color) => _owner.SetAppearance(this, Icon, color);
}
