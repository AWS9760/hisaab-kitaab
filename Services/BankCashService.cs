using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Works out bank balance and cash in hand for any month, carrying each
/// month's closing balances forward as the next month's opening balances
/// (unless the user has typed their own opening figure).
/// </summary>
public class BankCashService
{
    /// <summary>
    /// How far back the carry-forward chain looks.
    /// </summary>
    public const int MaxCarryMonths = 120;

    private readonly WorkbookStore _store;

    public BankCashService(WorkbookStore store)
    {
        _store = store;
    }

    public WorkbookStore Store => _store;

    /// <summary>
    /// Balances for <paramref name="month"/>. Walks forward from the earliest
    /// workbook on disk so every opening balance is the previous closing one.
    /// Unchanged months come from memory, so this is cheap to call often.
    /// </summary>
    public MonthBalances GetBalances(YearMonth month)
    {
        var start = _store.Excel.GetExistingMonths().FirstOrDefault(m => m <= month);
        if (start == default || start > month)
            start = month;
        if (month.AddMonths(-MaxCarryMonths) > start)
            start = month.AddMonths(-MaxCarryMonths);

        decimal carriedBank = 0, carriedCash = 0;
        MonthBalances? balances = null;
        for (var m = start; m <= month; m = m.AddMonths(1))
        {
            var sheet = _store.LoadBankCash(m);
            var expenses = _store.LoadMonth(m).Expenses;
            balances = BalanceCalculator.Calculate(
                m,
                sheet.ManualOpeningBank ?? carriedBank, sheet.ManualOpeningBank is not null,
                sheet.ManualOpeningCash ?? carriedCash, sheet.ManualOpeningCash is not null,
                sheet.Entries, expenses);
            (carriedBank, carriedCash) = (balances.ClosingBank, balances.ClosingCash);
        }

        return balances!;
    }

    /// <summary>
    /// If the month's workbook shows carried-forward opening balances that no
    /// longer match last month's closing ones (e.g. last month was edited
    /// since), writes the current figures so the sheet is right when opened in
    /// Excel. Does nothing for months without a workbook. Returns true if it wrote.
    /// </summary>
    public bool SyncCarriedOpenings(MonthBalances balances)
    {
        var month = balances.Month;
        if (!_store.Excel.MonthFileExists(month))
            return false;

        var sheet = _store.LoadBankCash(month);
        var wrote = false;

        if (!balances.BankOpeningIsManual && sheet.StoredOpeningBank != balances.OpeningBank)
        {
            _store.Excel.SetOpeningBalance(month, Account.Bank, balances.OpeningBank, isManual: false);
            wrote = true;
        }

        if (!balances.CashOpeningIsManual && sheet.StoredOpeningCash != balances.OpeningCash)
        {
            _store.Excel.SetOpeningBalance(month, Account.Cash, balances.OpeningCash, isManual: false);
            wrote = true;
        }

        if (wrote)
            _store.Invalidate(month);
        return wrote;
    }
}
