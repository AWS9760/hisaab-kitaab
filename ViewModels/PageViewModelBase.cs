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
}
