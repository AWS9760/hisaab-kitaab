using System.Text.Json;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the user's
/// app-data folder, and owns the rules for editing the family member list.
/// Every change is written to disk immediately.
/// </summary>
public class SettingsService
{
    public const int MaxMemberNameLength = 40;

    private AppSettings _settings = new();

    public SettingsService(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// %APPDATA%\HisaabKitaab\settings.json on Windows,
    /// ~/.config/HisaabKitaab/settings.json on Linux.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "HisaabKitaab",
        "settings.json");

    public string FilePath { get; }

    /// <summary>
    /// Set when <see cref="Load"/> found a settings file it couldn't read.
    /// The unreadable file is kept alongside under a new name so nothing is lost.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public IReadOnlyList<FamilyMember> FamilyMembers => _settings.FamilyMembers;

    /// <summary>
    /// Raised after an add, rename or remove has been saved.
    /// </summary>
    public event EventHandler? FamilyMembersChanged;

    public void Load()
    {
        LoadWarning = null;

        if (!File.Exists(FilePath))
        {
            _settings = new AppSettings();
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
            _settings = new AppSettings();
            LoadWarning = "Your settings file couldn't be read, so Hisaab Kitaab started with empty settings. " +
                          $"The old file was kept as {Path.GetFileName(backupPath)}.";
        }
    }

    /// <summary>
    /// Returns a message explaining why <paramref name="name"/> can't be used,
    /// or null if it's fine. Pass <paramref name="ignoreId"/> when renaming so
    /// a member doesn't clash with its own current name.
    /// </summary>
    public string? ValidateMemberName(string? name, Guid? ignoreId = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "Enter a name.";

        if (trimmed.Length > MaxMemberNameLength)
            return $"Names can be at most {MaxMemberNameLength} characters.";

        var clash = _settings.FamilyMembers.FirstOrDefault(m =>
            m.Id != ignoreId && string.Equals(m.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        if (clash is not null)
            return $"There's already a family member called \"{clash.Name}\".";

        return null;
    }

    public FamilyMember AddFamilyMember(string name)
    {
        ThrowIfInvalid(name);

        var member = new FamilyMember { Name = name.Trim() };
        _settings.FamilyMembers.Add(member);
        SaveOrRollBack(() => _settings.FamilyMembers.Remove(member));

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
        return member;
    }

    public void RenameFamilyMember(Guid id, string newName)
    {
        var member = Find(id);
        ThrowIfInvalid(newName, id);

        var oldName = member.Name;
        member.Name = newName.Trim();
        SaveOrRollBack(() => member.Name = oldName);

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFamilyMember(Guid id)
    {
        var member = Find(id);
        var index = _settings.FamilyMembers.IndexOf(member);

        _settings.FamilyMembers.RemoveAt(index);
        SaveOrRollBack(() => _settings.FamilyMembers.Insert(index, member));

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Write to a temp file first so a crash mid-write can't leave a half-written settings.json.
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tempPath, FilePath, overwrite: true);
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

    private FamilyMember Find(Guid id) =>
        _settings.FamilyMembers.FirstOrDefault(m => m.Id == id)
        ?? throw new KeyNotFoundException($"No family member with id {id}.");

    private void ThrowIfInvalid(string name, Guid? ignoreId = null)
    {
        var error = ValidateMemberName(name, ignoreId);
        if (error is not null)
            throw new ArgumentException(error, nameof(name));
    }

    // Tidies up a file that was hand-edited or written by an older version:
    // drops blank and duplicate names, and repairs missing or repeated ids.
    private static void Normalize(AppSettings settings)
    {
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var cleaned = new List<FamilyMember>();

        foreach (var member in settings.FamilyMembers ?? new List<FamilyMember>())
        {
            if (member is null)
                continue;

            member.Name = (member.Name ?? string.Empty).Trim();
            if (member.Name.Length == 0 || !seenNames.Add(member.Name))
                continue;

            if (member.Id == Guid.Empty || !seenIds.Add(member.Id))
            {
                member.Id = Guid.NewGuid();
                seenIds.Add(member.Id);
            }

            cleaned.Add(member);
        }

        settings.FamilyMembers = cleaned;
        settings.Version = AppSettings.CurrentVersion;
    }
}
