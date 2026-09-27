namespace HisaabKitaab.ViewModels;

/// <summary>
/// Base class for every screen reachable from the main navigation.
/// </summary>
public abstract class PageViewModelBase : ViewModelBase
{
    public abstract string Title { get; }

    /// <summary>
    /// Short description shown under the page header.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// Called each time the page is shown.
    /// </summary>
    public virtual void OnNavigatedTo()
    {
    }

    /// <summary>
    /// Called when another page is about to be shown, e.g. to save pending changes.
    /// </summary>
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}
