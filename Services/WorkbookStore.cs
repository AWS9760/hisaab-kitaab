using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Keeps parsed monthly data in memory so searching, filtering and balance
/// calculations never re-read Excel. Each cached month remembers the file's
/// last-write time and size; a month is only parsed again when its file has
/// changed (saved by the app, or edited in Excel). Safe to call from
/// background threads.
/// </summary>
public sealed class WorkbookStore
{
    private readonly ExcelService _excel;
    private readonly object _lock = new();
    private readonly Dictionary<YearMonth, Entry<ExpenseSheetData>> _expenses = new();
    private readonly Dictionary<YearMonth, Entry<BankCashSheetData>> _bankCash = new();

    private sealed record Entry<T>(T Data, FileStamp Stamp);

    private readonly record struct FileStamp(DateTime WriteTimeUtc, long Length);

    public WorkbookStore(ExcelService excel)
    {
        _excel = excel;
    }

    public ExcelService Excel => _excel;

    /// <summary>
    /// Number of times a sheet was actually parsed. Lets tests check that the cache is used.
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

    /// <summary>
    /// The month's expenses.
    /// </summary>
    public ExpenseSheetData LoadMonth(YearMonth month) => Load(
        _expenses, month,
        () => _excel.LoadExpenses(month),
        () => new ExpenseSheetData(month, false, Array.Empty<Expense>(), Array.Empty<SheetProblem>()),
        error => new ExpenseSheetData(month, true, Array.Empty<Expense>(), Array.Empty<SheetProblem>(), error));

    /// <summary>
    /// The month's Bank &amp; Cash sheet.
    /// </summary>
    public BankCashSheetData LoadBankCash(YearMonth month) => Load(
        _bankCash, month,
        () => _excel.LoadBankCash(month),
        () => BankCashSheetData.Empty(month),
        error => BankCashSheetData.Empty(month, fileExists: true, error));

    /// <summary>
    /// Forget a month so the next load re-reads it, even if the file's timestamp looks unchanged.
    /// </summary>
    public void Invalidate(YearMonth month)
    {
        lock (_lock)
        {
            _expenses.Remove(month);
            _bankCash.Remove(month);
        }
    }

    private T Load<T>(Dictionary<YearMonth, Entry<T>> cache, YearMonth month, Func<T> read, Func<T> missing, Func<string, T> failed)
    {
        var path = _excel.GetMonthFilePath(month);
        var stamp = Stamp(path);
        if (stamp is null)
            return missing();

        lock (_lock)
        {
            if (cache.TryGetValue(month, out var cached) && cached.Stamp == stamp)
                return cached.Data;
        }

        T data;
        try
        {
            data = read();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            return failed(ex.Message);
        }

        lock (_lock)
        {
            ParseCount++;

            // Stamp after reading: loading can itself save the file (to store new row IDs).
            if (Stamp(path) is { } after)
                cache[month] = new Entry<T>(data, after);
        }

        return data;
    }

    private static FileStamp? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
    }
}
