namespace HisaabKitaab.Models;

/// <summary>
/// App-wide configuration that outlives the monthly workbooks.
/// Persisted as JSON by <see cref="Services.SettingsService"/>.
/// </summary>
public class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<FamilyMember> FamilyMembers { get; set; } = new();

    /// <summary>
    /// Where the monthly workbooks are kept. Null means the default
    /// (Documents/Hisaab Kitaab).
    /// </summary>
    public string? DataFolder { get; set; }
}
