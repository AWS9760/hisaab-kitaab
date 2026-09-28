using System.Text.Json;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the user's
/// app-data folder, and owns the rules for editing family members and
/// categories. Every change is written to disk immediately.
/// </summary>
public class SettingsService
{
    public const int MaxMemberNameLength = 40;

    public const int MaxCategoryNameLength = 40;

    private AppSettings _settings = CreateDefaultSettings();

    private readonly string _defaultDataFolder;

    /// <param name="defaultDataFolder">Where workbooks go unless the user picks a folder. Defaults to <see cref="DefaultDataFolder"/>.</param>
    public SettingsService(string filePath, string? defaultDataFolder = null)
    {
        FilePath = filePath;
        _defaultDataFolder = defaultDataFolder ?? DefaultDataFolder;
    }

    /// <summary>
    /// %APPDATA%\HisaabKitaab\settings.json on Windows,
    /// ~/.config/HisaabKitaab/settings.json on Linux.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "HisaabKitaab",
        "settings.json");

    /// <summary>
    /// Documents/Hisaab Kitaab, falling back to the home folder on Linux
    /// systems without a Documents folder.
    /// </summary>
    public static string DefaultDataFolder
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(documents, "Hisaab Kitaab");
        }
    }

    public string FilePath { get; }

    /// <summary>
    /// Folder holding the monthly workbooks.
    /// </summary>
    public string DataFolder =>
        string.IsNullOrWhiteSpace(_settings.DataFolder) ? _defaultDataFolder : _settings.DataFolder;

    /// <summary>
    /// Set when <see cref="Load"/> found a settings file it couldn't read.
    /// The unreadable file is kept alongside under a new name so nothing is lost.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public IReadOnlyList<FamilyMember> FamilyMembers => _settings.FamilyMembers;

    public IReadOnlyList<Category> Categories => _settings.Categories!;

    /// <summary>
    /// Raised after a family member add, rename or remove has been saved.
    /// </summary>
    public event EventHandler? FamilyMembersChanged;

    /// <summary>
    /// Raised after any category change (including icon or colour) has been saved.
    /// </summary>
    public event EventHandler? CategoriesChanged;

    public void Load()
    {
        LoadWarning = null;

        if (!File.Exists(FilePath))
        {
            _settings = CreateDefaultSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            _settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                        ?? throw new JsonException("Settings file is empty.");
            Normalize(_settings);
        }
        catch (JsonException)
        {
            var backupPath = Path.Combine(
                Path.GetDirectoryName(FilePath)!,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(FilePath, backupPath, overwrite: true);
            _settings = CreateDefaultSettings();
            LoadWarning = "Your settings file couldn't be read, so Hisaab Kitaab started with default settings. " +
                          $"The old file was kept as {Path.GetFileName(backupPath)}.";
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Write to a temp file first so a crash mid-write can't leave a half-written settings.json.
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tempPath, FilePath, overwrite: true);
    }

    // ---- Family members -----------------------------------------------------

    /// <summary>
    /// Returns a message explaining why <paramref name="name"/> can't be used,
    /// or null if it's fine. Pass <paramref name="ignoreId"/> when renaming so
    /// a member doesn't clash with its own current name.
    /// </summary>
    public string? ValidateMemberName(string? name, Guid? ignoreId = null) =>
        ValidateName(name, ignoreId, MaxMemberNameLength, "family member",
            _settings.FamilyMembers.Select(m => (m.Id, m.Name)));

    public FamilyMember AddFamilyMember(string name)
    {
        ThrowIfInvalid(ValidateMemberName(name));

        var member = new FamilyMember { Name = name.Trim() };
        _settings.FamilyMembers.Add(member);
        SaveOrRollBack(() => _settings.FamilyMembers.Remove(member));

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
        return member;
    }

    public void RenameFamilyMember(Guid id, string newName)
    {
        var member = FindMember(id);
        ThrowIfInvalid(ValidateMemberName(newName, id));

        var oldName = member.Name;
        member.Name = newName.Trim();
        SaveOrRollBack(() => member.Name = oldName);

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFamilyMember(Guid id)
    {
        var member = FindMember(id);
        var index = _settings.FamilyMembers.IndexOf(member);

        _settings.FamilyMembers.RemoveAt(index);
        SaveOrRollBack(() => _settings.FamilyMembers.Insert(index, member));

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Categories ---------------------------------------------------------

    public string? ValidateCategoryName(string? name, Guid? ignoreId = null) =>
        ValidateName(name, ignoreId, MaxCategoryNameLength, "category",
            _settings.Categories!.Select(c => (c.Id, c.Name)));

    /// <summary>
    /// Finds a category by name, ignoring case and surrounding spaces.
    /// Returns null for names that aren't (or are no longer) a category.
    /// </summary>
    public Category? FindCategory(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : _settings.Categories!.FirstOrDefault(c =>
                string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Adds a category. Without an explicit colour it takes the next one in
    /// the palette so neighbouring categories look different.
    /// </summary>
    public Category AddCategory(string name, string? icon = null, string? color = null)
    {
        ThrowIfInvalid(ValidateCategoryName(name));

        var category = new Category
        {
            Name = name.Trim(),
            Icon = string.IsNullOrWhiteSpace(icon) ? CategoryStyles.DefaultIcon : icon.Trim(),
            Color = CategoryStyles.IsValidColor(color)
                ? color!
                : CategoryStyles.Colors[_settings.Categories!.Count % CategoryStyles.Colors.Count],
        };
        _settings.Categories!.Add(category);
        SaveOrRollBack(() => _settings.Categories!.Remove(category));

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
        return category;
    }

    public void RenameCategory(Guid id, string newName)
    {
        var category = FindCategory(id);
        ThrowIfInvalid(ValidateCategoryName(newName, id));

        var oldName = category.Name;
        category.Name = newName.Trim();
        SaveOrRollBack(() => category.Name = oldName);

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCategoryAppearance(Guid id, string icon, string color)
    {
        if (string.IsNullOrWhiteSpace(icon))
            throw new ArgumentException("Choose an icon.", nameof(icon));
        if (!CategoryStyles.IsValidColor(color))
            throw new ArgumentException("Colour must look like #RRGGBB.", nameof(color));

        var category = FindCategory(id);
        var (oldIcon, oldColor) = (category.Icon, category.Color);
        category.Icon = icon.Trim();
        category.Color = color;
        SaveOrRollBack(() => (category.Icon, category.Color) = (oldIcon, oldColor));

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveCategory(Guid id)
    {
        var category = FindCategory(id);
        var index = _settings.Categories!.IndexOf(category);

        _settings.Categories.RemoveAt(index);
        SaveOrRollBack(() => _settings.Categories.Insert(index, category));

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Credit card --------------------------------------------------------

    public CardSettings CreditCard => _settings.CreditCard;

    /// <summary>
    /// Raised after the card's name, limit or due day has been saved.
    /// </summary>
    public event EventHandler? CardSettingsChanged;

    /// <summary>
    /// Saves the card's details. Pass null to clear the limit or due day.
    /// </summary>
    public void UpdateCreditCard(string? name, decimal? limit, int? dueDay)
    {
        if (limit is < 0)
            throw new ArgumentException("The credit limit can't be negative.", nameof(limit));
        if (dueDay is < 1 or > 31)
            throw new ArgumentException("The due day must be between 1 and 31.", nameof(dueDay));

        var card = _settings.CreditCard;
        var old = (card.Name, card.Limit, card.DueDay);
        card.Name = string.IsNullOrWhiteSpace(name) ? "Credit card" : name.Trim();
        card.Limit = limit is { } l ? Math.Round(l, 2) : null;
        card.DueDay = dueDay;
        SaveOrRollBack(() => (card.Name, card.Limit, card.DueDay) = old);

        CardSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Helpers ------------------------------------------------------------

    private static AppSettings CreateDefaultSettings() => new() { Categories = CategoryStyles.CreateDefaults() };

    private static string? ValidateName(string? name, Guid? ignoreId, int maxLength, string noun,
        IEnumerable<(Guid Id, string Name)> existing)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "Enter a name.";

        if (trimmed.Length > maxLength)
            return $"Names can be at most {maxLength} characters.";

        var clash = existing.FirstOrDefault(e =>
            e.Id != ignoreId && string.Equals(e.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        if (clash.Name is not null)
            return $"There's already a {noun} called \"{clash.Name}\".";

        return null;
    }

    private static void ThrowIfInvalid(string? error)
    {
        if (error is not null)
            throw new ArgumentException(error, "name");
    }

    private void SaveOrRollBack(Action rollBack)
    {
        try
        {
            Save();
        }
        catch
        {
            rollBack();
            throw;
        }
    }

    private FamilyMember FindMember(Guid id) =>
        _settings.FamilyMembers.FirstOrDefault(m => m.Id == id)
        ?? throw new KeyNotFoundException($"No family member with id {id}.");

    private Category FindCategory(Guid id) =>
        _settings.Categories!.FirstOrDefault(c => c.Id == id)
        ?? throw new KeyNotFoundException($"No category with id {id}.");

    // Tidies up a file that was hand-edited or written by an older version:
    // drops blank and duplicate names, repairs missing or repeated ids, and
    // adds the default categories if the file predates them.
    private static void Normalize(AppSettings settings)
    {
        settings.FamilyMembers = CleanList(settings.FamilyMembers, m => m.Name, (m, n) => m.Name = n,
            m => m.Id, (m, id) => m.Id = id);

        if (settings.Categories is null)
        {
            settings.Categories = CategoryStyles.CreateDefaults();
        }
        else
        {
            settings.Categories = CleanList(settings.Categories, c => c.Name, (c, n) => c.Name = n,
                c => c.Id, (c, id) => c.Id = id);
            foreach (var category in settings.Categories)
            {
                category.Icon = string.IsNullOrWhiteSpace(category.Icon) ? CategoryStyles.DefaultIcon : category.Icon.Trim();
                if (!CategoryStyles.IsValidColor(category.Color))
                    category.Color = CategoryStyles.DefaultColor;
            }
        }

        settings.CreditCard ??= new CardSettings();
        var card = settings.CreditCard;
        if (string.IsNullOrWhiteSpace(card.Name))
            card.Name = "Credit card";
        if (card.Limit is < 0)
            card.Limit = null;
        if (card.DueDay is < 1 or > 31)
            card.DueDay = null;

        settings.Version = AppSettings.CurrentVersion;
    }

    private static List<T> CleanList<T>(List<T>? items, Func<T, string?> getName, Action<T, string> setName,
        Func<T, Guid> getId, Action<T, Guid> setId) where T : class
    {
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var cleaned = new List<T>();

        foreach (var item in items ?? new List<T>())
        {
            if (item is null)
                continue;

            var name = (getName(item) ?? string.Empty).Trim();
            setName(item, name);
            if (name.Length == 0 || !seenNames.Add(name))
                continue;

            if (getId(item) == Guid.Empty || !seenIds.Add(getId(item)))
            {
                setId(item, Guid.NewGuid());
                seenIds.Add(getId(item));
            }

            cleaned.Add(item);
        }

        return cleaned;
    }
}
