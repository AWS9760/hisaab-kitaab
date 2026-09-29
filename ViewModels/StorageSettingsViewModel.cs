using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The "Where your data is kept" section of Settings: the workbook folder and
/// the settings file's folder, each of which can be changed (moving what's
/// there) or put back to the default.
/// </summary>
public partial class StorageSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ExcelService _excel;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly Action? _dataFolderChanged;

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    [ObservableProperty]
    private bool _isDataFolderCustom;

    [ObservableProperty]
    private string _settingsFolder = string.Empty;

    [ObservableProperty]
    private bool _isSettingsFolderCustom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeDataFolderCommand), nameof(UseDefaultDataFolderCommand),
        nameof(ChangeSettingsFolderCommand), nameof(UseDefaultSettingsFolderCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    /// <param name="dataFolderChanged">Called after the workbook folder changes, e.g. to bring carried-forward figures up to date.</param>
    public StorageSettingsViewModel(SettingsService settings, ExcelService excel, IDialogService dialogs, ILauncherService launcher,
        Action? dataFolderChanged = null)
    {
        _settings = settings;
        _excel = excel;
        _dialogs = dialogs;
        _launcher = launcher;
        _dataFolderChanged = dataFolderChanged;
        Refresh();
    }

    public bool HasError => ErrorMessage is not null;

    public bool CanMoveSettings => _settings.CanMoveSettings;

    /// <summary>
    /// Raised after a folder changed, so other sections showing paths can refresh.
    /// </summary>
    public event EventHandler? FoldersChanged;

    public void Refresh()
    {
        DataFolder = _excel.DataFolder;
        IsDataFolderCustom = _settings.IsDataFolderCustom;
        SettingsFolder = Path.GetDirectoryName(_settings.FilePath)!;
        IsSettingsFolderCustom = _settings.IsSettingsFileMoved;
    }

    private bool CanChange() => !IsBusy;

    // ---- Workbooks ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChangeDataFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where to keep your workbooks");
        if (folder is not null)
            await MoveDataFolderAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task UseDefaultDataFolderAsync() => MoveDataFolderAsync(_settings.DefaultDataFolderPath);

    private async Task MoveDataFolderAsync(string folder)
    {
        ErrorMessage = null;
        StatusMessage = null;
        var oldFolder = _excel.DataFolder;
        if (SettingsService.PathsEqual(folder, oldFolder))
        {
            StatusMessage = "Your workbooks are already kept there.";
            return;
        }

        if (SettingsService.PathsEqual(folder, _settings.BackupFolder))
        {
            ErrorMessage = "That's the backup folder. Choose a different folder for your workbooks.";
            return;
        }

        var there = new ExcelService(folder);
        var thereCount = there.GetExistingMonths().Count + there.GetExistingZakatYears().Count;
        var hereCount = _excel.GetExistingMonths().Count + _excel.GetExistingZakatYears().Count;

        bool move;
        if (thereCount > 0)
        {
            var choice = await _dialogs.ChooseAsync("Use the workbooks in that folder?",
                $"That folder already has {Count(thereCount)} from Hisaab Kitaab, so the app will switch to those:\n{folder}" +
                (hereCount > 0 ? $"\n\nYour current {Count(hereCount)} stay where they are; nothing is moved or deleted:\n{oldFolder}" : string.Empty),
                "Use them", null);
            if (choice != DialogChoice.Primary)
                return;
            move = false;
        }
        else if (hereCount > 0)
        {
            var choice = await _dialogs.ChooseAsync("Move your workbooks?",
                $"Move your {Count(hereCount)}{(DefaultBackupsMove ? " and their backups" : string.Empty)} to the new folder?\n\n" +
                $"From: {oldFolder}\nTo: {folder}\n\n" +
                "Close them in Excel first. If any can't be moved, none are. Start empty leaves them where they are.",
                "Move them", "Start empty");
            if (choice == DialogChoice.Cancel)
                return;
            move = choice == DialogChoice.Primary;
        }
        else
        {
            move = false;
        }

        // The default backup folder lives inside the workbook folder, so it goes along.
        var backups = move && DefaultBackupsMove ? _settings.BackupFolder : null;
        var isDefault = SettingsService.PathsEqual(folder, _settings.DefaultDataFolderPath);

        IsBusy = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var moved = _excel.MoveDataFolder(folder, move, backups);
                try
                {
                    _settings.SetDataFolder(isDefault ? null : folder);
                }
                catch
                {
                    // Keep the files and the setting in step.
                    _excel.MoveDataFolder(oldFolder, move, backups is null ? null : Path.Combine(folder, Path.GetRelativePath(oldFolder, backups)));
                    throw;
                }

                return moved;
            });

            StatusMessage = move
                ? $"Moved {Count(result.Workbooks)} to {folder}." +
                  (result.Skipped.Count > 0 ? $" {result.Skipped.Count} backup file(s) couldn't be moved and are still in {oldFolder}." : string.Empty)
                : $"Workbooks are now kept in {folder}.";
            _dataFolderChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ErrorMessage = ex is ArgumentException arg && arg.ParamName is not null
                ? arg.Message.Replace($" (Parameter '{arg.ParamName}')", string.Empty)
                : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        Refresh();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool DefaultBackupsMove => string.IsNullOrWhiteSpace(_settings.Backups.Folder);

    private static string Count(int n) => n == 1 ? "1 workbook" : $"{n} workbooks";

    [RelayCommand]
    private void OpenDataFolder() => Open(DataFolder);

    // ---- Settings file --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChangeSettingsFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where to keep your settings");
        if (folder is not null)
            await MoveSettingsAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task UseDefaultSettingsFolderAsync() => MoveSettingsAsync(null);

    private async Task MoveSettingsAsync(string? folder)
    {
        ErrorMessage = null;
        StatusMessage = null;
        if (!CanMoveSettings)
        {
            ErrorMessage = "Settings are kept with the workbooks (HISAAB_KITAAB_HOME is set), so they can't be moved from here.";
            return;
        }

        var target = folder is null ? _settings.DefaultSettingsFilePath : Path.Combine(folder, "settings.json");
        if (SettingsService.PathsEqual(target, _settings.FilePath))
        {
            StatusMessage = "Your settings are already kept there.";
            return;
        }

        if (File.Exists(target) && await _dialogs.ChooseAsync("Replace the settings there?",
                $"{Path.GetDirectoryName(target)} already has a settings.json. Your current settings will replace it; " +
                "the one there is kept, renamed settings.replaced-(date).json.",
                "Replace", null) != DialogChoice.Primary)
            return;

        IsBusy = true;
        try
        {
            await Task.Run(() => _settings.MoveTo(folder));
            StatusMessage = $"Settings are now kept in {Path.GetDirectoryName(_settings.FilePath)}.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        Refresh();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenSettingsFolder() => Open(SettingsFolder);

    private void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _launcher.OpenFile(folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't open {folder}: {ex.Message}";
        }
    }
}
