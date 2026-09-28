using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// What a run of recurring expenses did.
/// </summary>
public record RecurringRunResult(IReadOnlyList<Expense> Added, IReadOnlyList<string> Errors)
{
    public static readonly RecurringRunResult Nothing = new(Array.Empty<Expense>(), Array.Empty<string>());
}

/// <summary>
/// Adds recurring expenses (rent, bills, subscriptions) as ordinary expenses
/// once their day has come, catching up any months missed while the app
/// wasn't running. Each item remembers the last date it was added for, so
/// nothing is ever added twice and a deleted one isn't brought back.
/// </summary>
public class RecurringService
{
    /// <summary>
    /// Most months one item catches up in a single run (e.g. after a long break).
    /// </summary>
    public const int MaxCatchUp = 24;

    private readonly SettingsService _settings;
    private readonly ExcelService _excel;
    private readonly SemaphoreSlim _running = new(1, 1);

    public RecurringService(SettingsService settings, ExcelService excel)
    {
        _settings = settings;
        _excel = excel;
    }

    /// <summary>
    /// Raised (on the thread that ran) after a run added expenses.
    /// </summary>
    public event EventHandler<RecurringRunResult>? ExpensesAdded;

    /// <summary>
    /// Adds every recurring expense due on or before <paramref name="today"/>.
    /// Only one run happens at a time; a run requested meanwhile does nothing.
    /// </summary>
    public RecurringRunResult RunDue(DateOnly today)
    {
        if (!_running.Wait(0))
            return RecurringRunResult.Nothing;

        try
        {
            var added = new List<Expense>();
            var errors = new List<string>();

            foreach (var id in _settings.RecurringSnapshot().Select(r => r.Id))
            {
                for (var n = 0; n < MaxCatchUp; n++)
                {
                    // Read afresh each time: it may have been edited, paused or removed
                    // in Settings while this runs.
                    if (_settings.GetRecurring(id) is not { IsPaused: false } item || item.NextDate > today)
                        break;

                    var date = item.NextDate;
                    var (category, member) = _settings.NamesFor(item);
                    try
                    {
                        added.Add(_excel.AddExpense(new Expense
                        {
                            Date = date,
                            Amount = item.Amount,
                            Category = category,
                            FamilyMember = member,
                            PaymentMethod = item.PaymentMethod,
                            Note = item.Name,
                        }));
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                    {
                        // Usually the month's workbook is open in Excel. Try again next run.
                        errors.Add($"Couldn't add \"{item.Name}\" for {date:d MMM}: {ex.Message}");
                        break;
                    }

                    try
                    {
                        _settings.MarkRecurringAdded(item.Id, date);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Without the mark it would be added again next run, so stop here.
                        errors.Add($"Added \"{item.Name}\" but couldn't save settings: {ex.Message}");
                        break;
                    }
                    catch (KeyNotFoundException)
                    {
                        // Removed in Settings just now; the expense already added stays.
                        break;
                    }
                }
            }

            var result = new RecurringRunResult(added, errors);
            if (added.Count > 0)
                ExpensesAdded?.Invoke(this, result);
            return result;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// The first date for a new recurring expense on <paramref name="day"/>:
    /// this month's if it hasn't passed (or if <paramref name="includeThisMonth"/>),
    /// otherwise next month's.
    /// </summary>
    public static DateOnly FirstDate(int day, DateOnly today, bool includeThisMonth)
    {
        var probe = new RecurringExpense { DayOfMonth = day };
        var thisMonth = probe.DateIn(YearMonth.Of(today));
        return thisMonth >= today || includeThisMonth ? thisMonth : probe.DateIn(YearMonth.Of(today).AddMonths(1));
    }
}
