using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

public enum CashCheckStatus
{
    NotCounted,
    Matches,
    MoreThanRecorded,
    LessThanRecorded,
}

/// <summary>
/// Compares a cash count with the cash in hand the records say there should be.
/// </summary>
public record CashCheck(DateOnly Date, decimal Counted, decimal Expected, bool HasCount)
{
    /// <summary>
    /// Differences smaller than this are treated as a match (paisa rounding).
    /// </summary>
    public const decimal Tolerance = 1m;

    /// <summary>
    /// Positive when there's more cash than recorded.
    /// </summary>
    public decimal Difference => Counted - Expected;

    public CashCheckStatus Status =>
        !HasCount ? CashCheckStatus.NotCounted
        : Math.Abs(Difference) < Tolerance ? CashCheckStatus.Matches
        : Difference > 0 ? CashCheckStatus.MoreThanRecorded
        : CashCheckStatus.LessThanRecorded;

    public static CashCheck For(CurrencyCount count, DateOnly date, MonthBalances balances) =>
        new(date, count.Total, balances.CashOnDate(date), HasCount: count.CountedOn is not null || count.Total != 0);
}

public static class MonthBalancesExtensions
{
    /// <summary>
    /// Cash in hand at the end of <paramref name="date"/>.
    /// </summary>
    public static decimal CashOnDate(this MonthBalances balances, DateOnly date) =>
        balances.Ledger.LastOrDefault(l => l.Date <= date)?.CashBalance ?? balances.OpeningCash;
}
