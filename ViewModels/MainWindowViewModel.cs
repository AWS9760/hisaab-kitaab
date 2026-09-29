using CommunityToolkit.Mvvm.ComponentModel;
using HisaabKitaab.Models;
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
    private readonly RecurringService _recurring;
    private readonly ReminderService _reminders;
    private readonly ILauncherService _launcher;

    // Background work (recurring expenses) reports back here to update pages on the UI thread.
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    // Added before the Expenses page was first opened.
    private readonly List<Expense> _unseenRecurring = new();

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private AppPage _currentPageKey;

    public MainWindowViewModel(SettingsService settings, IDialogService dialogs, ExcelService excel, ILauncherService launcher,
        INotifier? notifier = null)
    {
        _settings = settings;
        _dialogs = dialogs;
        _excel = excel;
        _store = new WorkbookStore(excel);
        _carryForward = new CarryForwardService(_store);
        _recurring = new RecurringService(settings, excel);
        _reminders = new ReminderService(settings, _store, _recurring, notifier ?? new NullNotifier());
        _launcher = launcher;

        _recurring.ExpensesAdded += (_, result) => OnUiThread(() => OnRecurringAdded(result));

        _currentPage = GetOrCreatePage(AppPage.Dashboard);
        _currentPageKey = AppPage.Dashboard;
        _currentPage.OnNavigatedTo();

        // Bring carried-forward figures in every workbook up to date, in the background.
        _carryForward.StartSyncAll();
    }

    public INotifier Notifier => _reminders.Notifier;

    /// <summary>
    /// Starts reminders and recurring expenses. Called once the window is shown,
    /// so notifications have somewhere to come from.
    /// </summary>
    public void StartBackgroundWork() => _reminders.Start(TimeSpan.FromMinutes(1));

    /// <summary>
    /// Lets the current page save anything pending and gives background work a
    /// moment to finish before the app closes.
    /// </summary>
    public async Task PrepareToCloseAsync()
    {
        await CurrentPage.OnNavigatedFromAsync();
        _reminders.Dispose();
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

    private void OnRecurringAdded(RecurringRunResult result)
    {
        // Pages not opened yet load fresh when opened, but the Expenses page
        // still says what was added (e.g. at startup) the first time it opens.
        if (_pages.TryGetValue(AppPage.Expenses, out var expenses))
            ((ExpensesViewModel)expenses).ShowRecurringAdded(result);
        else
            _unseenRecurring.AddRange(result.Added);
        if (_pages.TryGetValue(AppPage.Dashboard, out var dashboard))
            ((DashboardViewModel)dashboard).Refresh();
        if (_pages.TryGetValue(AppPage.Settings, out var settings))
            ((SettingsViewModel)settings).Recurring.RefreshItems();
    }

    private void OnUiThread(Action action)
    {
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
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
                AppPage.Zakat => new ZakatViewModel(new ZakatService(_store), _dialogs, _launcher),
                AppPage.Settings => new SettingsViewModel(_settings, _dialogs, _excel, reminders: _reminders),
                _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
            };
            _pages[page] = vm;

            if (vm is ExpensesViewModel expenses && _unseenRecurring.Count > 0)
            {
                expenses.ShowRecurringAdded(new RecurringRunResult(_unseenRecurring.ToList(), Array.Empty<string>()));
                _unseenRecurring.Clear();
            }
        }

        return vm;
    }
}
