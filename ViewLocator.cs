using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HisaabKitaab.ViewModels;
using HisaabKitaab.Views;

namespace HisaabKitaab;

/// <summary>
/// Maps page view models to their views. Explicit mapping (rather than
/// reflection on type names) keeps this trim-safe for dotnet publish.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        DashboardViewModel => new DashboardView(),
        ExpensesViewModel => new ExpensesView(),
        BankCashViewModel => new BankCashView(),
        CurrencyViewModel => new CurrencyView(),
        CreditCardViewModel => new CreditCardView(),
        ZakatViewModel => new ZakatView(),
        SettingsViewModel => new SettingsView(),
        null => null,
        _ => new TextBlock { Text = $"No view registered for {data.GetType().Name}" },
    };

    public bool Match(object? data) => data is PageViewModelBase;
}
