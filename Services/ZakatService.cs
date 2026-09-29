using System.Globalization;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// One row of a zakat year's log, with what's left to give straight after it.
/// </summary>
public record ZakatLine(ZakatEntry Entry, decimal Remaining);

/// <summary>
/// A zakat year: what was carried in, set aside, given, and what's left.
/// </summary>
/// <param name="PeriodIsSaved">False when the year has no workbook yet and <paramref name="Period"/> is a suggestion.</param>
public record ZakatYear(
    int Year,
    ZakatPeriod Period,
    bool PeriodIsSaved,
    decimal Carried,
    bool CarriedIsManual,
    decimal SetAside,
    decimal Given,
    decimal GivenFromBank,
    decimal GivenFromCash,
    IReadOnlyList<ZakatLine> Ledger)
{
    /// <summary>
    /// Carried in plus set aside this year.
    /// </summary>
    public decimal TakenOut => Carried + SetAside;

    /// <summary>
    /// Still to give (negative if more was given than taken out).
    /// </summary>
    public decimal Remaining => TakenOut - Given;
}

public static class ZakatCalculator
{
    /// <summary>
    /// Set aside adds to what's left to give; given takes it off.
    /// Within a day, setting aside comes first.
    /// </summary>
    public static ZakatYear Calculate(int year, ZakatPeriod period, bool periodIsSaved, decimal carried, bool carriedIsManual,
        IReadOnlyList<ZakatEntry> entries)
    {
        var ledger = new List<ZakatLine>();
        var remaining = carried;
        foreach (var e in entries.Select((e, i) => (e, i)).OrderBy(x => x.e.Date).ThenBy(x => x.e.Type).ThenBy(x => x.i).Select(x => x.e))
        {
            remaining += e.Type == ZakatEntryType.SetAside ? e.Amount : -e.Amount;
            ledger.Add(new ZakatLine(e, remaining));
        }

        decimal Given(Account? from) => entries
            .Where(e => e.Type == ZakatEntryType.Given && (from is null || e.PaidFrom == from))
            .Sum(e => e.Amount);

        return new ZakatYear(year, period, periodIsSaved, carried, carriedIsManual,
            entries.Where(e => e.Type == ZakatEntryType.SetAside).Sum(e => e.Amount),
            Given(null), Given(Account.Bank), Given(Account.Cash), ledger);
    }
}

/// <summary>
/// What a zakat change did besides saving the zakat workbook.
/// </summary>
/// <param name="MonthsNotUpdated">
/// Month workbooks whose Bank &amp; Cash copy of zakat given couldn't be
/// updated (usually open in Excel). The app's own balances are right anyway,
/// and the copy is brought up to date the next time that month is saved or the app starts.
/// </param>
public record ZakatChangeResult(IReadOnlyList<string> MonthsNotUpdated)
{
    public static readonly ZakatChangeResult Done = new(Array.Empty<string>());
}

/// <summary>
/// Works out each zakat year (carrying what's left into the next year unless
/// the user typed their own figure), suggests and checks the dates a year
/// runs over, and keeps the other workbooks in step after a change: the
/// copy of zakat given on each month's Bank &amp; Cash sheet, and the
/// amount carried into later years' zakat workbooks.
/// </summary>
public class ZakatService
{
    /// <summary>
    /// How far back the carry-forward chain looks.
    /// </summary>
    public const int MaxCarryYears = 50;

    private readonly WorkbookStore _store;

    public ZakatService(WorkbookStore store)
    {
        _store = store;
    }

    public WorkbookStore Store => _store;

    private ExcelService Excel => _store.Excel;

    // ---- Years and their dates ------------------------------------------------

    /// <summary>
    /// The zakat year to show on <paramref name="today"/>: last year's if its
    /// dates are still running, otherwise this year's.
    /// </summary>
    public int YearFor(DateOnly today)
    {
        var previous = today.Year - 1;
        return Excel.ZakatFileExists(previous) && PeriodFor(previous).Contains(today) ? previous : today.Year;
    }

    /// <summary>
    /// The year's saved dates, or a suggestion if it has none yet.
    /// </summary>
    public ZakatPeriod PeriodFor(int year) => _store.LoadZakat(year).Period ?? SuggestedPeriod(year);

    /// <summary>
    /// For a year with no dates yet: the day after last year's zakat year
    /// ended, for a year; or the calendar year if there's nothing to follow.
    /// </summary>
    public ZakatPeriod SuggestedPeriod(int year)
    {
        if (_store.LoadZakat(year - 1).Period is { } previous && previous.End.AddDays(1).Year == year)
            return ZakatPeriod.YearFrom(previous.End.AddDays(1));
        return ZakatPeriod.CalendarYear(year);
    }

    /// <summary>
    /// Why <paramref name="period"/> can't be used for <paramref name="year"/>, or null if it can.
    /// Years mustn't overlap their neighbours.
    /// </summary>
    public string? ValidatePeriod(int year, ZakatPeriod period)
    {
        if (period.Start.Year != year)
            return $"The {year} zakat year must start in {year}.";
        if (period.End < period.Start)
            return "The zakat year must end on or after the day it starts.";
        if (period.End > ZakatPeriod.LatestEnd(period.Start))
            return $"A zakat year can't be longer than a year (from {Day(period.Start)} it can end on {Day(ZakatPeriod.LatestEnd(period.Start))} at the latest).";

        if (_store.LoadZakat(year - 1).Period is { } previous && period.Start <= previous.End)
            return $"It must start after the {year - 1} zakat year ends ({Day(previous.End)}).";
        if (_store.LoadZakat(year + 1).Period is { } next && period.End >= next.Start)
            return $"It must end before the {year + 1} zakat year starts ({Day(next.Start)}).";

        return null;
    }

    /// <exception cref="ArgumentException">The dates can't be used (see <see cref="ValidatePeriod"/>).</exception>
    public void SetPeriod(int year, ZakatPeriod period)
    {
        if (ValidatePeriod(year, period) is { } problem)
            throw new ArgumentException(problem, nameof(period));

        Excel.SetZakatPeriod(year, period);
        _store.InvalidateZakat(year);
        SyncCarriedAfter(year - 1);
    }

    // ---- Figures --------------------------------------------------------------

    /// <summary>
    /// The zakat year's figures, walking forward from the earliest zakat
    /// workbook so each year starts with what the one before had left.
    /// </summary>
    public ZakatYear GetYear(int year)
    {
        var start = Excel.GetExistingZakatYears().FirstOrDefault(y => y <= year);
        if (start == 0 || start > year)
            start = year;
        start = Math.Max(start, year - MaxCarryYears);

        decimal carried = 0;
        ZakatYear? result = null;
        for (var y = start; y <= year; y++)
        {
            var data = _store.LoadZakat(y);
            result = ZakatCalculator.Calculate(y, data.Period ?? SuggestedPeriod(y), data.Period is not null,
                data.ManualCarried ?? carried, data.ManualCarried is not null, data.Entries);
            carried = result.Remaining;
        }

        return result!;
    }

    // ---- Changes --------------------------------------------------------------

    public ZakatChangeResult Add(int year, ZakatEntry entry)
    {
        var period = PeriodFor(year);
        CheckDate(year, period, entry.Date);

        var saved = Excel.AddZakatEntry(year, entry, period);
        return AfterChange(year, saved);
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public ZakatChangeResult Update(int year, ZakatEntry original, ZakatEntry updated)
    {
        CheckDate(year, PeriodFor(year), updated.Date);

        var saved = Excel.UpdateZakatEntry(year, updated with { Id = original.Id });
        return AfterChange(year, original, saved);
    }

    /// <exception cref="KeyNotFoundException">The entry is no longer in the file.</exception>
    public ZakatChangeResult Delete(int year, ZakatEntry entry)
    {
        Excel.DeleteZakatEntry(year, entry.Id);
        return AfterChange(year, entry);
    }

    /// <summary>
    /// Sets the amount carried in from last year to the user's own figure.
    /// </summary>
    public void SetCarried(int year, decimal amount)
    {
        Excel.SetZakatCarried(year, amount, isManual: true, PeriodFor(year));
        _store.InvalidateZakat(year);
        SyncCarriedAfter(year);
    }

    /// <summary>
    /// Goes back to carrying in what last year had left.
    /// </summary>
    public void CarryForward(int year)
    {
        var carried = Excel.GetExistingZakatYears().Any(y => y < year) ? GetYear(year - 1).Remaining : 0;
        Excel.SetZakatCarried(year, carried, isManual: false, PeriodFor(year));
        _store.InvalidateZakat(year);
        SyncCarriedAfter(year);
    }

    private static void CheckDate(int year, ZakatPeriod period, DateOnly date)
    {
        if (!period.Contains(date))
            throw new ArgumentException($"{Day(date)} is outside the {year} zakat year ({period}).", nameof(date));
    }

    private ZakatChangeResult AfterChange(int year, params ZakatEntry[] touched)
    {
        _store.InvalidateZakat(year);

        // Zakat given changes those months' bank and cash; refresh their copies.
        var notUpdated = new List<string>();
        foreach (var month in touched.Where(e => e.Type == ZakatEntryType.Given).Select(e => YearMonth.Of(e.Date)).Distinct())
        {
            try
            {
                Excel.RefreshMonthWorkbook(month, createIfMissing: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                notUpdated.Add(month.FileName);
            }

            _store.Invalidate(month);
        }

        // Includes this year: its workbook may have just been created with nothing carried in.
        SyncCarriedAfter(year - 1);
        return notUpdated.Count == 0 ? ZakatChangeResult.Done : new ZakatChangeResult(notUpdated);
    }

    // ---- Keeping the workbooks in step ------------------------------------------

    /// <summary>
    /// Rewrites the carried-forward figure in every zakat workbook after
    /// <paramref name="year"/> that no longer matches (for anyone opening them
    /// in Excel; the app always works it out). Workbooks that can't be written
    /// are skipped. Returns how many were rewritten.
    /// </summary>
    public int SyncCarriedAfter(int year)
    {
        var written = 0;
        foreach (var later in Excel.GetExistingZakatYears().Where(y => y > year))
        {
            try
            {
                var data = _store.LoadZakat(later);
                if (data.Error is not null || data.ManualCarried is not null)
                    continue;

                var carried = GetYear(later).Carried;
                if (data.StoredCarried == carried)
                    continue;

                Excel.SetZakatCarried(later, carried, isManual: false, PeriodFor(later));
                _store.InvalidateZakat(later);
                written++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // Open in Excel or unreadable: the next change or startup tries again.
            }
        }

        return written;
    }

    /// <summary>
    /// <see cref="SyncCarriedAfter"/> for every zakat workbook (at startup).
    /// </summary>
    public int SyncAllCarried() => SyncCarriedAfter(int.MinValue);

    /// <summary>
    /// Zakat given during <paramref name="month"/>, from the zakat years that can cover it.
    /// </summary>
    public static IReadOnlyList<ZakatEntry> GivenIn(WorkbookStore store, YearMonth month) =>
        new[] { month.Year - 1, month.Year }
            .SelectMany(y => store.LoadZakat(y).Entries)
            .Where(e => e.Type == ZakatEntryType.Given && month.Contains(e.Date))
            .ToList();

    /// <summary>
    /// Whether the month's Bank &amp; Cash sheet shows different zakat payments
    /// from the zakat workbooks (e.g. the month was open in Excel when zakat
    /// was logged, or a zakat workbook was edited in Excel).
    /// </summary>
    public bool CopyIsStale(YearMonth month)
    {
        if (!Excel.MonthFileExists(month))
            return false;

        // An unreadable zakat workbook leaves the copy alone, so don't keep trying.
        if (new[] { month.Year - 1, month.Year }.Any(y => _store.LoadZakat(y).Error is not null))
            return false;

        static IEnumerable<ZakatCopyLine> Ordered(IEnumerable<ZakatCopyLine> lines) =>
            lines.OrderBy(l => l.Date).ThenBy(l => l.PaidFrom).ThenBy(l => l.Amount);

        var expected = GivenIn(_store, month).Select(e => new ZakatCopyLine(e.Date, e.Amount, e.PaidFrom!.Value));
        var sheet = _store.LoadBankCash(month);
        return sheet.Error is null && !Ordered(expected).SequenceEqual(Ordered(sheet.StoredZakat));
    }

    private static string Day(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
