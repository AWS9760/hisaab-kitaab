using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using HisaabKitaab.Services;
using HisaabKitaab.ViewModels;

namespace HisaabKitaab.Views;

public partial class MainWindow : AppWindow
{
    private bool _readyToClose;

    public MainWindow()
    {
        InitializeComponent();

        // Selecting the first item raises SelectionChanged, which loads the Dashboard.
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel vm)
            return;

        // Windows notifications come from a notification-area icon owned by this window.
        if (vm.Notifier is WindowsNotifier windows && TryGetPlatformHandle()?.Handle is { } handle)
            windows.AttachTo(handle);

        vm.StartBackgroundWork();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_readyToClose || e.Cancel || DataContext is not MainWindowViewModel vm)
            return;

        // Let the current page save anything pending (e.g. a cash count typed
        // moments ago), then close for real.
        e.Cancel = true;
        try
        {
            await vm.PrepareToCloseAsync();
        }
        finally
        {
            (vm.Notifier as IDisposable)?.Dispose();
            _readyToClose = true;
            Close();
        }
    }

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (e.IsSettingsSelected)
        {
            vm.NavigateTo(AppPage.Settings);
        }
        else if (e.SelectedItem is NavigationViewItem { Tag: string tag }
                 && Enum.TryParse<AppPage>(tag, out var page))
        {
            vm.NavigateTo(page);
        }
    }
}
