using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Keeps the carried-forward opening figures written in each workbook (bank
/// balance, cash in hand, amount owed on the card) in step with the previous
/// month's closing figures, so the sheets are right when opened in Excel.
///
/// The app never relies on those written figures itself (it always works them
/// out), so this is only about what Excel shows. It runs in the background
/// after every save and once at startup.
/// </summary>
public sealed class CarryForwardService : IDisposable
{
    private readonly WorkbookStore _store;
    private readonly BankCashService _bankCash;
    private readonly CreditCardService _card;
    private readonly object _lock = new();

    // Earliest month whose later months need checking, or null when idle.
    private YearMonth? _pendingFrom;
    private Task _running = Task.CompletedTask;
    private bool _draining;
    private bool _disposed;

    // Set on the thread doing a sync, so its own saves don't queue more syncs.
    [ThreadStatic]
    private static bool _syncing;

    public CarryForwardService(WorkbookStore store)
    {
        _store = store;
        _bankCash = new BankCashService(store);
        _card = new CreditCardService(store);
        _store.Excel.MonthSaved += OnMonthSaved;
    }

    /// <summary>
    /// Brings every month after <paramref name="changed"/> up to date. Returns
    /// how many workbooks were rewritten. Months open in Excel are skipped.
    /// </summary>
    public int SyncAfter(YearMonth changed) =>
        Sync(_store.Excel.GetExistingMonths().Where(m => m > changed));

    /// <summary>
    /// Brings every existing month up to date (used at startup, to catch
    /// changes made while the app was closed).
    /// </summary>
    public int SyncAll() => Sync(_store.Excel.GetExistingMonths());

    /// <summary>
    /// Queues a background <see cref="SyncAll"/>.
    /// </summary>
    public void StartSyncAll()
    {
        var earliest = _store.Excel.GetExistingMonths().FirstOrDefault();
        if (earliest != default)
            Queue(earliest.AddMonths(-1));
    }

    /// <summary>
    /// Waits for queued background work to finish (for tests and shutdown).
    /// </summary>
    public Task IdleAsync()
    {
        lock (_lock)
            return _running;
    }

    private int Sync(IEnumerable<YearMonth> months)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            var written = 0;
            foreach (var month in months.OrderBy(m => m))
            {
                try
                {
                    if (_bankCash.SyncCarriedOpenings(_bankCash.GetBalances(month)))
                        written++;
                    if (_card.SyncCarriedOpening(_card.GetMonth(month)))
                        written++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                {
                    // Open in Excel or unreadable: leave it; the next save or startup tries again.
                }
            }

            return written;
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void OnMonthSaved(YearMonth month)
    {
        if (!_syncing)
            Queue(month);
    }

    /// <summary>
    /// Runs one background sync at a time. Requests made while one is running
    /// are merged (the earliest month wins) and handled when it finishes.
    /// </summary>
    private void Queue(YearMonth from)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _pendingFrom = _pendingFrom is { } p && p < from ? p : from;
            if (!_draining)
            {
                _draining = true;
                _running = Task.Run(Drain);
            }
        }
    }

    private void Drain()
    {
        while (true)
        {
            YearMonth from;
            lock (_lock)
            {
                if (_pendingFrom is not { } next || _disposed)
                {
                    // Cleared under the lock, so a request arriving now starts a new worker.
                    _pendingFrom = null;
                    _draining = false;
                    return;
                }

                from = next;
                _pendingFrom = null;
            }

            SyncAfter(from);
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _disposed = true;
        _store.Excel.MonthSaved -= OnMonthSaved;
    }
}
