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
    private readonly WorkbookStore _store;
    private readonly CarryForwardService _carryForward;
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
        _store = new WorkbookStore(excel);
        _carryForward = new CarryForwardService(_store);
        _launcher = launcher;

        _currentPage = GetOrCreatePage(AppPage.Dashboard);
        _currentPageKey = AppPage.Dashboard;
        _currentPage.OnNavigatedTo();

        // Bring carried-forward figures in every workbook up to date, in the background.
        _carryForward.StartSyncAll();
    }

    /// <summary>
    /// Lets the current page save anything pending and gives background work a
    /// moment to finish before the app closes.
    /// </summary>
    public async Task PrepareToCloseAsync()
    {
        await CurrentPage.OnNavigatedFromAsync();
        await Task.WhenAny(_carryForward.IdleAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
        _carryForward.Dispose();
    }

    public void NavigateTo(AppPage page)
    {
        _ = CurrentPage.OnNavigatedFromAsync();
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
                AppPage.Dashboard => new DashboardViewModel(new DashboardService(_store), _settings),
                AppPage.Expenses => new ExpensesViewModel(_store, _settings, _dialogs, _launcher),
                AppPage.BankCash => new BankCashViewModel(new BankCashService(_store), _settings, _dialogs, _launcher),
                AppPage.Currency => new CurrencyViewModel(new BankCashService(_store), _launcher),
                AppPage.CreditCard => new CreditCardViewModel(new CreditCardService(_store), _settings, _dialogs, _launcher),
                AppPage.Zakat => new ZakatViewModel(),
                AppPage.Settings => new SettingsViewModel(_settings, _dialogs, _excel),
                _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
            };
            _pages[page] = vm;
        }

        return vm;
    }
}
