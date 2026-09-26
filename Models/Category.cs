namespace HisaabKitaab.Models;

/// <summary>
/// A spending category. Expenses store the category's name; the icon and
/// colour only live here and are looked up by name for display.
/// </summary>
public class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A single emoji, e.g. "🛒".
    /// </summary>
    public string Icon { get; set; } = CategoryStyles.DefaultIcon;

    /// <summary>
    /// "#RRGGBB".
    /// </summary>
    public string Color { get; set; } = CategoryStyles.DefaultColor;
}

/// <summary>
/// The icons and colours offered when customising a category, and the
/// categories a new install starts with.
/// </summary>
public static class CategoryStyles
{
    public const string DefaultIcon = "📦";

    public const string DefaultColor = "#64748B";

    // Emoji chosen to render as colour glyphs without variation selectors,
    // so they look the same on Windows (Segoe UI Emoji) and Linux (Noto Color Emoji).
    public static readonly IReadOnlyList<string> Icons = new[]
    {
        "🛒", "🏠", "💡", "🔥", "💧", "⛽", "🚗", "🚌", "🎓", "📚",
        "💊", "🏥", "🍔", "🍕", "☕", "📱", "💻", "👕", "👟", "💇",
        "🎁", "🎉", "🎬", "🎮", "🧳", "🔧", "🧹", "👶", "🐐", "🕌",
        "💰", "💳", "🧾", "🐾", "💪", "📦",
    };

    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "#E11D48", "#F97316", "#EAB308", "#16A34A", "#0D9488",
        "#0EA5E9", "#2563EB", "#7C3AED", "#DB2777", "#64748B",
    };

    public static List<Category> CreateDefaults() => new()
    {
        new() { Name = "Groceries", Icon = "🛒", Color = "#16A34A" },
        new() { Name = "Rent", Icon = "🏠", Color = "#2563EB" },
        new() { Name = "Utilities", Icon = "💡", Color = "#EAB308" },
        new() { Name = "Transport", Icon = "⛽", Color = "#F97316" },
        new() { Name = "Education", Icon = "🎓", Color = "#7C3AED" },
        new() { Name = "Health", Icon = "💊", Color = "#E11D48" },
        new() { Name = "Dining", Icon = "🍔", Color = "#DB2777" },
        new() { Name = "Mobile & Internet", Icon = "📱", Color = "#0EA5E9" },
        new() { Name = "Clothing", Icon = "👕", Color = "#0D9488" },
        new() { Name = "Other", Icon = "📦", Color = "#64748B" },
    };

    public static bool IsValidColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
}
