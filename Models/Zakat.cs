using System.Globalization;

namespace HisaabKitaab.Models;

/// <summary>
/// Kinds of row in a year's zakat log.
/// </summary>
public enum ZakatEntryType
{
    /// <summary>Zakat worked out and put aside to give. Moves no money by itself.</summary>
    SetAside,

    /// <summary>Zakat given to someone, from the bank or in cash.</summary>
    Given,
}

public static class ZakatEntryTypes
{
    public static readonly IReadOnlyList<ZakatEntryType> All = new[] { ZakatEntryType.SetAside, ZakatEntryType.Given };

    /// <summary>
    /// The text written in the Type column of the sheet.
    /// </summary>
    public static string ToDisplayName(this ZakatEntryType type) => type switch
    {
        ZakatEntryType.SetAside => "Set aside",
        ZakatEntryType.Given => "Given",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Accepts the display names plus common words typed by hand. Case and spaces are ignored.
    /// </summary>
    public static bool TryParse(string? text, out ZakatEntryType type)
    {
        var key = new string((text ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();
        switch (key)
        {
            case "setaside" or "aside" or "takenout" or "reserved" or "calculated":
                type = ZakatEntryType.SetAside;
                return true;
            case "given" or "give" or "paid" or "disbursed" or "disbursement":
                type = ZakatEntryType.Given;
                return true;
            default:
                type = default;
                return false;
        }
    }
}

/// <summary>
/// One row of a year's zakat log.
/// </summary>
public record ZakatEntry
{
    public Guid Id { get; init; }

    public DateOnly Date { get; init; }

    public ZakatEntryType Type { get; init; }

    /// <summary>
    /// Always positive.
    /// </summary>
    public decimal Amount { get; init; }

    /// <summary>
    /// For zakat given: where the money came from (it comes off that balance
    /// on Bank &amp; Cash). Null for zakat set aside.
    /// </summary>
    public Account? PaidFrom { get; init; }

    /// <summary>
    /// Who it was given to (zakat given only).
    /// </summary>
    public string Recipient { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// The dates a zakat year runs over, picked by the user each year (e.g.
/// Ramadan to Ramadan). A year's file is named after the year it starts in.
/// </summary>
public readonly record struct ZakatPeriod(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>
    /// 1 January to 31 December.
    /// </summary>
    public static ZakatPeriod CalendarYear(int year) => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    /// <summary>
    /// A year starting on <paramref name="start"/>: up to the day before the same date next year.
    /// </summary>
    public static ZakatPeriod YearFrom(DateOnly start) => new(start, start.AddYears(1).AddDays(-1));

    /// <summary>
    /// The longest a zakat year may be: a year starting on <paramref name="start"/>.
    /// </summary>
    public static DateOnly LatestEnd(DateOnly start) => start.AddYears(1).AddDays(-1);

    public override string ToString() =>
        $"{Start.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {End.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Everything read from one year's zakat workbook.
/// </summary>
/// <param name="Period">The dates stored in the file, or null if there's no file (or they couldn't be read).</param>
/// <param name="ManualCarried">Amount carried from last year that the user typed in, or null to carry forward.</param>
public record ZakatYearData(
    int Year,
    bool FileExists,
    ZakatPeriod? Period,
    decimal? ManualCarried,
    IReadOnlyList<ZakatEntry> Entries,
    IReadOnlyList<SheetProblem> Problems,
    string? Error = null)
{
    /// <summary>
    /// The carried amount currently written in the file, typed or carried.
    /// </summary>
    public decimal? StoredCarried { get; init; }

    public static ZakatYearData Empty(int year, bool fileExists = false, string? error = null) =>
        new(year, fileExists, null, null, Array.Empty<ZakatEntry>(), Array.Empty<SheetProblem>(), error);
}

/// <summary>
/// A zakat payment as copied onto a month's Bank &amp; Cash sheet.
/// </summary>
public readonly record struct ZakatCopyLine(DateOnly Date, decimal Amount, Account PaidFrom);
