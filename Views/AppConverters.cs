using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HisaabKitaab.Views;

public static class AppConverters
{
    /// <summary>
    /// "#RRGGBB" to a solid brush (grey if the text isn't a colour).
    /// </summary>
    public static readonly IValueConverter HexToBrush =
        new FuncValueConverter<string?, IBrush>(hex => new SolidColorBrush(ParseOrGrey(hex)));

    /// <summary>
    /// "#RRGGBB" to a translucent brush, for the soft circle behind category icons.
    /// Works on both light and dark backgrounds.
    /// </summary>
    public static readonly IValueConverter HexToTint =
        new FuncValueConverter<string?, IBrush>(hex => new SolidColorBrush(ParseOrGrey(hex), 0.22));

    private static Color ParseOrGrey(string? hex) => Color.TryParse(hex, out var color) ? color : Colors.Gray;
}
