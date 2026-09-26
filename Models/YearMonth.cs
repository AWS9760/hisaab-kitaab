namespace HisaabKitaab.Models;

/// <summary>
/// Identifies one monthly workbook.
/// </summary>
public readonly record struct YearMonth : IComparable<YearMonth>
{
    // Fixed English abbreviations so file names don't change with the OS language.
    // "Sept" rather than "Sep" to match the naming the app was specified with (Sept_2026.xlsx).
    private static readonly string[] ShortNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "June", "July", "Aug", "Sept", "Oct", "Nov", "Dec" };

    private static readonly string[] LongNames =
    {
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    };

    public YearMonth(int year, int month)
    {
        if (year is < 1900 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(year), year, "Year must be between 1900 and 9999.");
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be between 1 and 12.");

        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    public static YearMonth Of(DateOnly date) => new(date.Year, date.Month);

    public static YearMonth Of(DateTime date) => new(date.Year, date.Month);

    public DateOnly FirstDay => new(Year, Month, 1);

    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);

    public bool Contains(DateOnly date) => date.Year == Year && date.Month == Month;

    /// <summary>
    /// e.g. "Sept_2026.xlsx".
    /// </summary>
    public string FileName => $"{ShortNames[Month - 1]}_{Year}.xlsx";

    /// <summary>
    /// e.g. "September 2026".
    /// </summary>
    public string DisplayName => $"{LongNames[Month - 1]} {Year}";

    public YearMonth AddMonths(int months)
    {
        var d = FirstDay.AddMonths(months);
        return new YearMonth(d.Year, d.Month);
    }

    /// <summary>
    /// Recognises names produced by <see cref="FileName"/>, ignoring case.
    /// </summary>
    public static bool TryParseFileName(string fileName, out YearMonth result)
    {
        result = default;
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = name.Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var year) || year is < 1900 or > 9999)
            return false;

        var month = Array.FindIndex(ShortNames, s => string.Equals(s, parts[0], StringComparison.OrdinalIgnoreCase)) + 1;
        if (month == 0)
            return false;

        result = new YearMonth(year, month);
        return true;
    }

    public int CompareTo(YearMonth other) =>
        Year != other.Year ? Year.CompareTo(other.Year) : Month.CompareTo(other.Month);

    public static bool operator <(YearMonth left, YearMonth right) => left.CompareTo(right) < 0;

    public static bool operator >(YearMonth left, YearMonth right) => left.CompareTo(right) > 0;

    public static bool operator <=(YearMonth left, YearMonth right) => left.CompareTo(right) <= 0;

    public static bool operator >=(YearMonth left, YearMonth right) => left.CompareTo(right) >= 0;

    public override string ToString() => DisplayName;
}
