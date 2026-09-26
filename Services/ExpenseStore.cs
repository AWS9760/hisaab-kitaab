using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Keeps parsed monthly expenses in memory so searching and filtering never
/// re-read Excel. Each cached month remembers the file's last-write time and
/// size; a month is only parsed again when its file has changed (saved by the
/// app, or edited in Excel). Safe to call from background threads.
/// </summary>
public sealed class ExpenseStore
{
    private readonly ExcelService _excel;
    private readonly object _lock = new();
    private readonly Dictionary<YearMonth, Entry> _cache = new();

    private sealed record Entry(ExpenseSheetData Data, FileStamp Stamp);

    private readonly record struct FileStamp(DateTime WriteTimeUtc, long Length);

    public ExpenseStore(ExcelService excel)
    {
        _excel = excel;
    }

    public ExcelService Excel => _excel;

    /// <summary>
    /// Number of times a workbook was actually parsed. Lets tests check that the cache is used.
    /// </summary>
    public int ParseCount { get; private set; }

    /// <summary>
    /// Every month from <paramref name="from"/> to <paramref name="to"/> inclusive, oldest first.
    /// A month whose file can't be read comes back empty with <see cref="ExpenseSheetData.Error"/> set.
    /// </summary>
    public IReadOnlyList<ExpenseSheetData> LoadMonths(YearMonth from, YearMonth to)
    {
        var months = new List<ExpenseSheetData>();
        for (var month = from; month <= to; month = month.AddMonths(1))
            months.Add(LoadMonth(month));
        return months;
    }

    public ExpenseSheetData LoadMonth(YearMonth month)
    {
        var path = _excel.GetMonthFilePath(month);
        var stamp = Stamp(path);
        if (stamp is null)
            return new ExpenseSheetData(month, false, Array.Empty<Expense>(), Array.Empty<SheetProblem>());

        lock (_lock)
        {
            if (_cache.TryGetValue(month, out var cached) && cached.Stamp == stamp)
                return cached.Data;
        }

        ExpenseSheetData data;
        try
        {
            data = _excel.LoadExpenses(month);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            return new ExpenseSheetData(month, true, Array.Empty<Expense>(), Array.Empty<SheetProblem>(), ex.Message);
        }

        lock (_lock)
        {
            ParseCount++;

            // Stamp after reading: loading can itself save the file (to store new row IDs).
            if (Stamp(path) is { } after)
                _cache[month] = new Entry(data, after);
        }

        return data;
    }

    /// <summary>
    /// Forget a month so the next load re-reads it, even if the file's timestamp looks unchanged.
    /// </summary>
    public void Invalidate(YearMonth month)
    {
        lock (_lock)
            _cache.Remove(month);
    }

    private static FileStamp? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
    }
}
