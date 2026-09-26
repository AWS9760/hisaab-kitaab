using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// Runs a rename across a year's workbooks and reports the outcome as a
/// success summary and/or a warning with a Retry for files that were busy.
/// </summary>
public partial class WorkbookRenameNotice : ViewModelBase
{
    private Func<RenameResult>? _retry;
    private string _oldName = string.Empty;
    private int _year;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    public bool HasSummary => Summary is not null;

    public bool HasWarning => Warning is not null;

    public void Run(int year, string oldName, Func<RenameResult> rename)
    {
        _year = year;
        _oldName = oldName;
        _retry = null;
        Summary = null;
        Warning = null;

        var result = rename();

        if (result.FilesFailed.Count > 0)
        {
            _retry = rename;
            Warning = $"Couldn't update {string.Join(", ", result.FilesFailed)}, so expenses there still say " +
                      $"\"{oldName}\". The file may be open in Excel: close it and choose Retry.";
        }

        if (result.RowsUpdated > 0)
        {
            Summary = $"Renamed {Plural(result.RowsUpdated, "expense")} in {year}'s " +
                      $"{Plural(result.FilesUpdated.Count, "workbook")}.";
        }

        RetryCommand.NotifyCanExecuteChanged();
    }

    public void ClearSummary() => Summary = null;

    private bool CanRetry() => _retry is not null;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        if (_retry is { } retry)
            Run(_year, _oldName, retry);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
