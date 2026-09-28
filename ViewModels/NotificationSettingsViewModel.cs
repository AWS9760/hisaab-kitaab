using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Services;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// The Notifications section of Settings. Changes save straight away.
/// </summary>
public partial class NotificationSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ReminderService? _reminders;
    private bool _loading;

    [ObservableProperty]
    private bool _dailyReminder;

    /// <summary>
    /// TimeSpan because that's what TimePicker binds to.
    /// </summary>
    [ObservableProperty]
    private TimeSpan? _reminderTime;

    [ObservableProperty]
    private bool _cardDueReminder;

    [ObservableProperty]
    private double _cardDueDaysBefore;

    [ObservableProperty]
    private bool _budgetAlerts;

    [ObservableProperty]
    private string? _message;

    public NotificationSettingsViewModel(SettingsService settings, ReminderService? reminders)
    {
        _settings = settings;
        _reminders = reminders;

        _loading = true;
        var n = settings.Notifications;
        DailyReminder = n.DailyReminder;
        ReminderTime = n.DailyReminderTime.ToTimeSpan();
        CardDueReminder = n.CardDueReminder;
        CardDueDaysBefore = n.CardDueDaysBefore;
        BudgetAlerts = n.BudgetAlerts;
        _loading = false;
    }

    public bool IsAvailable => _reminders?.Notifier.IsAvailable ?? false;

    public string AvailabilityText => IsAvailable
        ? "Notifications appear while Hisaab Kitaab is open, including when it's minimised."
        : (_reminders?.Notifier.UnavailableReason ?? "Desktop notifications aren't available on this system.") +
          " Until then, warnings still show inside the app.";

    partial void OnDailyReminderChanged(bool value) => Save();

    partial void OnReminderTimeChanged(TimeSpan? value) => Save();

    partial void OnCardDueReminderChanged(bool value) => Save();

    partial void OnCardDueDaysBeforeChanged(double value) => Save();

    partial void OnBudgetAlertsChanged(bool value) => Save();

    private void Save()
    {
        if (_loading)
            return;

        var time = TimeOnly.FromTimeSpan(ReminderTime ?? new TimeSpan(21, 0, 0));
        var days = double.IsNaN(CardDueDaysBefore) ? 3 : (int)Math.Clamp(Math.Round(CardDueDaysBefore), 0, 30);
        SettingsSave.TryAny(() => _settings.UpdateNotifications(DailyReminder, time, CardDueReminder, days, BudgetAlerts), out var error);
        Message = error;
    }

    [RelayCommand]
    private void SendTest() =>
        Message = _reminders?.SendTest() == true
            ? "Sent. It should appear in the corner of your screen."
            : _reminders?.Notifier.UnavailableReason ?? "This system can't show desktop notifications.";
}
