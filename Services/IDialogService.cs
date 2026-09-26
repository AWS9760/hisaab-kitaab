namespace HisaabKitaab.Services;

/// <summary>
/// Lets view models ask the user questions without referencing UI types.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a confirm/cancel dialog. Returns true if the user chose <paramref name="confirmText"/>.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel");
}
