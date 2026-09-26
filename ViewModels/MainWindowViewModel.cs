using CommunityToolkit.Mvvm.ComponentModel;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // Pages are created once and reused, so switching tabs keeps their state.
    private readonly Dictionary<AppPage, PageViewModelBase> _pages = new();
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private AppPage _currentPageKey;

    public MainWindowViewModel(SettingsService settings, IDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;

        _currentPage = GetOrCreatePage(AppPage.Dashboard);
        _currentPageKey = AppPage.Dashboard;
    }

    public void NavigateTo(AppPage page)
    {
        CurrentPage = GetOrCreatePage(page);
        CurrentPageKey = page;
    }

    private PageViewModelBase GetOrCreatePage(AppPage page)
    {
        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = page switch
            {
                AppPage.Dashboard => new DashboardViewModel(),
                AppPage.Expenses => new ExpensesViewModel(),
                AppPage.BankCash => new BankCashViewModel(),
                AppPage.Currency => new CurrencyViewModel(),
                AppPage.CreditCard => new CreditCardViewModel(),
                AppPage.Zakat => new ZakatViewModel(),
                AppPage.Settings => new SettingsViewModel(_settings, _dialogs),
                _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
            };
            _pages[page] = vm;
        }

        return vm;
    }
}
