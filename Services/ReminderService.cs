using System.Globalization;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// A notification that was shown.
/// </summary>
public record SentNotification(string Title, string Message);

/// <summary>
/// Everything that happens on its own while the app is open: adding due
/// recurring expenses, and the desktop notifications (daily logging
/// reminder, credit card due date, budget exceeded). Each notification is
/// shown once (remembered in settings, so not again after a restart).
///
/// Runs every minute and shortly after any workbook is saved.
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly WorkbookStore _store;
    private readonly RecurringService _recurring;
    private readonly CreditCardService _card;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private CancellationTokenSource? _timer;

    public ReminderService(SettingsService settings, WorkbookStore store, RecurringService recurring,
        INotifier notifier, TimeProvider? clock = null)
    {
        _settings = settings;
        _store = store;
        _recurring = recurring;
        _card = new CreditCardService(store);
        _notifier = notifier;
        _clock = clock ?? TimeProvider.System;
    }

    public INotifier Notifier => _notifier;

    /// <summary>
    /// Checks now, then every <paramref name="interval"/>, and after workbook saves.
    /// </summary>
    public void Start(TimeSpan interval)
    {
        _timer = new CancellationTokenSource();
        var token = _timer.Token;
        _store.Excel.MonthSaved += OnMonthSaved;

        _ = Task.Run(async () =>
        {
            using var ticks = new PeriodicTimer(interval, _clock);
            do
            {
                await CheckAsync();
            }
            while (await ticks.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
    }

    /// <summary>
    /// Runs one check in the background. Skipped if one is already running.
    /// </summary>
    public Task CheckAsync() => Task.Run(() =>
    {
        try
        {
            Check();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            // A workbook was busy; the next check tries again.
        }
        catch (Exception ex)
        {
            // Anything else mustn't stop the timer (which awaits this); the next check tries again.
            System.Diagnostics.Debug.WriteLine($"Reminder check failed: {ex}");
        }
    });

    /// <summary>
    /// Does everything that's due now and returns the notifications shown.
    /// </summary>
    public IReadOnlyList<SentNotification> Check()
    {
        if (!_checking.Wait(0))
            return Array.Empty<SentNotification>();

        try
        {
            var sent = new List<SentNotification>();
            var now = _clock.GetLocalNow();
            var today = DateOnly.FromDateTime(now.DateTime);
            var thisMonth = YearMonth.Of(today);
            var prefs = _settings.Notifications;

            AddRecurring(today, sent);

            if (prefs.BudgetAlerts)
                CheckBudgets(thisMonth, sent);

            if (prefs.CardDueReminder)
                CheckCardDue(today, thisMonth, prefs, sent);

            if (prefs.DailyReminder && TimeOnly.FromDateTime(now.DateTime) >= prefs.DailyReminderTime
                                    && prefs.LastDailyReminder != today)
            {
                if (!_store.LoadMonth(thisMonth).Expenses.Any(e => e.Date == today))
                    Notify(sent, "Anything to log today?", "You haven't added any expenses today. It only takes a moment.");
                _settings.MarkDailyReminderShown(today);
            }

            return sent;
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// Shows a sample notification, e.g. from Settings. Returns false if this
    /// system can't show desktop notifications.
    /// </summary>
    public bool SendTest()
    {
        if (!_notifier.IsAvailable)
            return false;
        _notifier.Show("Hisaab Kitaab", "Notifications are working. You'll see reminders like this one.");
        return true;
    }

    private void AddRecurring(DateOnly today, List<SentNotification> sent)
    {
        var result = _recurring.RunDue(today);
        if (result.Added.Count == 1)
        {
            var e = result.Added[0];
            Notify(sent, "Recurring expense added", $"{e.Note}: {Pkr.Format(e.Amount)} on {Day(e.Date)}.");
        }
        else if (result.Added.Count > 1)
        {
            Notify(sent, $"{result.Added.Count} recurring expenses added",
                string.Join(", ", result.Added.Select(e => $"{e.Note} {Pkr.Format(e.Amount)}")));
        }
    }

    private void CheckBudgets(YearMonth month, List<SentNotification> sent)
    {
        var statuses = BudgetService.Calculate(_settings.ResolvedBudgets(), _store.LoadMonth(month).Expenses);
        foreach (var s in statuses.Where(s => s.Level == BudgetLevel.Over))
        {
            if (_settings.WasBudgetAlertShown(s.Budget.Id, month))
                continue;

            Notify(sent, $"Over budget: {s.Budget.Label}",
                $"{Pkr.Format(s.Spent)} spent of a {Pkr.Format(s.Budget.Amount)} budget for {month.DisplayName}.");
            _settings.MarkBudgetAlertShown(s.Budget.Id, month);
        }
    }

    private void CheckCardDue(DateOnly today, YearMonth thisMonth, NotificationSettings prefs, List<SentNotification> sent)
    {
        if (_settings.CreditCard.NextDueDate(today) is not { } due || prefs.LastCardDueReminder == due)
            return;

        var days = due.DayNumber - today.DayNumber;
        if (days > prefs.CardDueDaysBefore)
            return;

        var owed = _card.GetMonth(thisMonth).Outstanding;
        if (owed <= 0)
            return;

        var when = days switch { 0 => "today", 1 => "tomorrow", _ => $"in {days} days" };
        Notify(sent, $"{_settings.CreditCard.Name} payment due {when}", $"{Pkr.Format(owed)} is due on {Day(due)}.");
        _settings.MarkCardDueReminderShown(due);
    }

    private void Notify(List<SentNotification> sent, string title, string message)
    {
        _notifier.Show(title, message);
        sent.Add(new SentNotification(title, message));
    }

    private static string Day(DateOnly date) => date.ToString("ddd, d MMM", CultureInfo.InvariantCulture);

    // A save may have pushed a budget over; check shortly (after the save's own work settles).
    private void OnMonthSaved(YearMonth month) => _ = Task.Delay(500).ContinueWith(_ => CheckAsync());

    public void Dispose()
    {
        _timer?.Cancel();
        _store.Excel.MonthSaved -= OnMonthSaved;
    }
}
