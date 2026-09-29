using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One backup copy in the restore list.
/// </summary>
public partial class BackupVersionRow : ViewModelBase
{
    private readonly BackupSettingsViewModel _owner;

    public BackupVersionRow(BackupSettingsViewModel owner, BackupInfo info, bool isNewest)
    {
        _owner = owner;
        Info = info;
        IsNewest = isNewest;
    }

    public BackupInfo Info { get; }

    public bool IsNewest { get; }

    public string TakenText => Info.Taken.ToString("ddd, d MMM yyyy · HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => Info.Size < 1024 ? $"{Info.Size} bytes" : $"{Math.Ceiling(Info.Size / 1024.0):N0} KB";

    [RelayCommand]
    private Task RestoreAsync() => _owner.RestoreAsync(this);
}

/// <summary>
/// The Backups section of Settings: on/off, where backups go, and restoring
/// a workbook from an earlier copy.
/// </summary>
public partial class BackupSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly BackupService _backups;
    private readonly ExcelService _excel;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private bool _loading;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _folderText = string.Empty;

    [ObservableProperty]
    private bool _isCustomFolder;

    [ObservableProperty]
    private string _lastBackupText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupProblem))]
    private string? _backupProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    private IReadOnlyList<BackedUpWorkbook> _files = Array.Empty<BackedUpWorkbook>();

    [ObservableProperty]
    private BackedUpWorkbook? _selectedFile;

    [ObservableProperty]
    private IReadOnlyList<BackupVersionRow> _versions = Array.Empty<BackupVersionRow>();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    public BackupSettingsViewModel(SettingsService settings, BackupService backups, ExcelService excel,
        IDialogService dialogs, ILauncherService launcher)
    {
        _settings = settings;
        _backups = backups;
        _excel = excel;
        _dialogs = dialogs;
        _launcher = launcher;
        Refresh();
    }

    public bool HasError => ErrorMessage is not null;

    public bool HasBackupProblem => BackupProblem is not null;

    public bool HasFiles => Files.Count > 0;

    public string RetentionText =>
        $"Every time a workbook or your settings are saved, a copy goes to the backup folder. " +
        $"The newest {BackupService.KeepRecent} copies of each file are kept, plus the last copy of each day for {BackupService.KeepDays} days.";

    /// <summary>
    /// Re-reads the settings and the backup folder, e.g. each time Settings is shown.
    /// </summary>
    public void Refresh()
    {
        _loading = true;
        IsEnabled = _settings.Backups.Enabled;
        _loading = false;

        FolderText = _settings.BackupFolder;
        IsCustomFolder = !string.IsNullOrWhiteSpace(_settings.Backups.Folder);
        BackupProblem = _backups.LastError;
        LastBackupText = _backups.LastBackup is { } last
            ? $"Last backup: {BackupService.OriginalName(last.Path)} at {last.Taken.ToString("HH:mm, d MMM", CultureInfo.InvariantCulture)}."
            : string.Empty;

        var selected = SelectedFile;
        Files = _backups.BackedUpWorkbooks();
        SelectedFile = selected is not null && Files.Contains(selected) ? selected : null;
        ShowVersions();
    }

    partial void OnSelectedFileChanged(BackedUpWorkbook? value)
    {
        StatusMessage = null;
        ErrorMessage = null;
        ShowVersions();
    }

    private void ShowVersions()
    {
        var list = SelectedFile is { } file ? _backups.BackupsOf(file) : Array.Empty<BackupInfo>();
        Versions = list.Select((b, i) => new BackupVersionRow(this, b, i == 0)).ToList();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
            Save(value, _settings.Backups.Folder, value ? "Backups are on." : "Backups are off. Existing copies are kept.");
    }

    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where to keep backups");
        if (folder is null)
            return;

        if (Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(_excel.DataFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Choose a folder other than the one your workbooks are in (a folder inside it is fine).";
            return;
        }

        Save(_settings.Backups.Enabled, folder, $"New backups will go to {folder}. Earlier ones stay where they were.");
    }

    [RelayCommand]
    private void UseDefaultFolder() =>
        Save(_settings.Backups.Enabled, null, "New backups will go to the default folder.");

    private void Save(bool enabled, string? folder, string message)
    {
        var ok = SettingsSave.TryAny(() => _settings.UpdateBackups(enabled, folder), out var error);
        Refresh();
        StatusMessage = ok ? message : null;
        ErrorMessage = ok ? null : error;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderText);
            _launcher.OpenFile(FolderText);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't open {FolderText}: {ex.Message}";
        }
    }

    internal async Task RestoreAsync(BackupVersionRow row)
    {
        if (SelectedFile is not { } file || IsBusy)
            return;

        var when = row.Info.Taken.ToString("d MMM yyyy 'at' HH:mm:ss", CultureInfo.InvariantCulture);
        if (!await _dialogs.ConfirmAsync($"Restore {file.FileName}?",
                $"This replaces {file.FileName} with the copy from {when}.\n\n" +
                "The current file is backed up first, so you can undo this by restoring that copy. Close the file in Excel before restoring.",
                "Restore"))
            return;

        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                _backups.BackUpWorkbookNow(Path.Combine(_excel.GetYearFolder(file.Year), file.FileName));
                _excel.RestoreWorkbook(file.Year, file.FileName, row.Info.Path);
            });
            StatusMessage = $"Restored {file.FileName} from the copy of {when}.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        var status = StatusMessage;
        Refresh();
        StatusMessage = status;
    }
}
