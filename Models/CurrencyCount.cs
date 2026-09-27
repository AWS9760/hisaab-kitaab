namespace HisaabKitaab.Models;

/// <summary>
/// The Pakistani rupee notes counted on the Currency Denominations sheet.
/// </summary>
public static class PkrNotes
{
    /// <summary>
    /// Largest first, as they're usually counted.
    /// </summary>
    public static readonly IReadOnlyList<int> Denominations = new[] { 5000, 1000, 500, 100, 50, 20, 10 };

    /// <summary>
    /// Roughly the colour of each note, so the list is easy to scan.
    /// </summary>
    public static string ColorOf(int denomination) => denomination switch
    {
        5000 => "#B8860B", // mustard
        1000 => "#1E6FB8", // blue
        500 => "#2E7D32",  // dark green
        100 => "#C62828",  // red
        50 => "#7B1FA2",   // purple
        20 => "#EF6C00",   // orange
        10 => "#558B2F",   // green
        _ => CategoryStyles.DefaultColor,
    };

    public const int MaxCount = 100_000;
}

/// <summary>
/// A cash count: how many of each note, plus coins and loose change.
/// </summary>
public record CurrencyCount
{
    public static readonly CurrencyCount Empty = new();

    /// <summary>
    /// The day the cash was counted. It's compared with cash in hand at the
    /// end of that day. Null means not counted yet.
    /// </summary>
    public DateOnly? CountedOn { get; init; }

    /// <summary>
    /// Denomination → number of notes. Missing denominations count as zero.
    /// </summary>
    public IReadOnlyDictionary<int, int> Notes { get; init; } = new Dictionary<int, int>();

    public decimal Coins { get; init; }

    public int CountOf(int denomination) => Notes.TryGetValue(denomination, out var n) ? n : 0;

    public decimal Total => Notes.Sum(kv => (decimal)kv.Key * kv.Value) + Coins;

    public bool IsEmpty => CountedOn is null && Coins == 0 && Notes.Values.All(n => n == 0);
}

/// <summary>
/// What was read from a month's Currency Denominations sheet.
/// </summary>
public record CurrencySheetData(
    YearMonth Month,
    bool FileExists,
    CurrencyCount Count,
    IReadOnlyList<SheetProblem> Problems,
    string? Error = null)
{
    public static CurrencySheetData None(YearMonth month, bool fileExists = false, string? error = null) =>
        new(month, fileExists, CurrencyCount.Empty, Array.Empty<SheetProblem>(), error);
}
