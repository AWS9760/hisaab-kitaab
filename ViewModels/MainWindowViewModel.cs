using CommunityToolkit.Mvvm.ComponentModel;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // Pages are created once and reused, so switching tabs keeps their state.
    private readonly Dictionary<AppPage, PageViewModelBase> _pages = new();
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ExcelService _excel;
    private readonly ExpenseStore _store;
    private readonly ILauncherService _launcher;

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private AppPage _currentPageKey;

    public MainWindowViewModel(SettingsService settings, IDialogService dialogs, ExcelService excel, ILauncherService launcher)
    {
        _settings = settings;
        _dialogs = dialogs;
        _excel = excel;
        _store = new ExpenseStore(excel);
        _launcher = launcher;

        _currentPage = GetOrCreatePage(AppPage.Dashboard);
        _currentPageKey = AppPage.Dashboard;
    }

    public void NavigateTo(AppPage page)
    {
        CurrentPage = GetOrCreatePage(page);
        CurrentPageKey = page;
        CurrentPage.OnNavigatedTo();
    }

    private PageViewModelBase GetOrCreatePage(AppPage page)
    {
        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = page switch
            {
                AppPage.Dashboard => new DashboardViewModel(),
                AppPage.Expenses => new ExpensesViewModel(_store, _settings, _dialogs, _launcher),
                AppPage.BankCash => new BankCashViewModel(),
                AppPage.Currency => new CurrencyViewModel(),
                AppPage.CreditCard => new CreditCardViewModel(),
                AppPage.Zakat => new ZakatViewModel(),
                AppPage.Settings => new SettingsViewModel(_settings, _dialogs, _excel),
                _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
            };
            _pages[page] = vm;
        }

        return vm;
    }
}
