using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The Categories section of the Settings page.
/// </summary>
public partial class CategorySettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ExcelService _excel;
    private readonly TimeProvider _clock;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCategoryCommand))]
    private string _newCategoryName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public CategorySettingsViewModel(SettingsService settings, IDialogService dialogs, ExcelService excel, TimeProvider clock)
    {
        _settings = settings;
        _dialogs = dialogs;
        _excel = excel;
        _clock = clock;

        foreach (var category in settings.Categories)
            Items.Add(new CategoryItemViewModel(this, category));

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    public ObservableCollection<CategoryItemViewModel> Items { get; } = new();

    public WorkbookRenameNotice RenameNotice { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool HasError => ErrorMessage is not null;

    partial void OnNewCategoryNameChanged(string value) => ErrorMessage = null;

    private bool CanAddCategory() => !string.IsNullOrWhiteSpace(NewCategoryName);

    [RelayCommand(CanExecute = nameof(CanAddCategory))]
    private void AddCategory()
    {
        var name = NewCategoryName;
        var error = _settings.ValidateCategoryName(name);
        if (error is null && SettingsSave.Try(() => _settings.AddCategory(name), out var category, out error))
        {
            Items.Add(new CategoryItemViewModel(this, category));
            NewCategoryName = string.Empty;
        }

        ErrorMessage = error;
    }

    internal void BeginRename(CategoryItemViewModel item)
    {
        foreach (var other in Items.Where(c => c != item && c.IsEditing))
            other.IsEditing = false;

        ErrorMessage = null;
        RenameNotice.ClearSummary();
        item.EditName = item.Name;
        item.IsEditing = true;
    }

    internal void CommitRename(CategoryItemViewModel item)
    {
        if (!item.IsEditing)
            return;

        var newName = item.EditName.Trim();
        if (newName == item.Name)
        {
            CancelRename(item);
            return;
        }

        var error = _settings.ValidateCategoryName(newName, item.Id);
        if (error is null)
            SettingsSave.Try(() => _settings.RenameCategory(item.Id, newName), out error);
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        var oldName = item.Name;
        item.Name = newName;
        item.IsEditing = false;

        var year = _clock.GetLocalNow().Year;
        RenameNotice.Run(year, oldName, () => _excel.RenameCategory(year, oldName, newName));
    }

    internal void CancelRename(CategoryItemViewModel item)
    {
        item.IsEditing = false;
        ErrorMessage = null;
    }

    internal void SetAppearance(CategoryItemViewModel item, string icon, string color)
    {
        if (!SettingsSave.Try(() => _settings.SetCategoryAppearance(item.Id, icon, color), out var error))
        {
            ErrorMessage = error;
            return;
        }

        item.Icon = icon;
        item.Color = color;
    }

    internal async Task RemoveAsync(CategoryItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            $"Remove {item.Name}?",
            $"{item.Name} will no longer be offered for new expenses. " +
            "Expenses already recorded under it keep the name and show with a plain icon.",
            "Remove");
        if (!confirmed)
            return;

        if (!SettingsSave.Try(() => _settings.RemoveCategory(item.Id), out var error))
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        Items.Remove(item);
    }
}
