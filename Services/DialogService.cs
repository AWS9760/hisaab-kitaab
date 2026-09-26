using FluentAvalonia.UI.Controls;

namespace HisaabKitaab.Services;

public class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            // Safer default: pressing Enter cancels rather than confirming.
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
