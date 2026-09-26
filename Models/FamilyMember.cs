namespace HisaabKitaab.Models;

/// <summary>
/// A person whose spending is tracked. The Id stays the same when the member
/// is renamed, so budgets and other settings can refer to it safely.
/// </summary>
public class FamilyMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
}
