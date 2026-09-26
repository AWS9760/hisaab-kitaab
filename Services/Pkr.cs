using System.Globalization;

namespace HisaabKitaab.Services;

/// <summary>
/// Formats Pakistani rupee amounts the same way everywhere in the app.
/// </summary>
public static class Pkr
{
    public const string Symbol = "₨";

    /// <summary>
    /// "₨ 1,250" for whole rupees, "₨ 1,250.50" when there are paisa, "-₨ 300" for negatives.
    /// </summary>
    public static string Format(decimal amount)
    {
        var abs = Math.Abs(amount);
        var digits = abs == decimal.Truncate(abs) ? "N0" : "N2";
        var text = $"{Symbol} {abs.ToString(digits, CultureInfo.InvariantCulture)}";
        return amount < 0 ? "-" + text : text;
    }
}
