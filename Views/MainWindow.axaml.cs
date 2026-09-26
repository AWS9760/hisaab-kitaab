using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using HisaabKitaab.ViewModels;

namespace HisaabKitaab.Views;

public partial class MainWindow : AppWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Selecting the first item raises SelectionChanged, which loads the Dashboard.
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
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
