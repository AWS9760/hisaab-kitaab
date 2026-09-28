using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HisaabKitaab.Services;
using HisaabKitaab.ViewModels;
using HisaabKitaab.Views;

namespace HisaabKitaab;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // HISAAB_KITAAB_HOME keeps settings and workbooks together in one folder
            // (portable installs, or trying the app without touching real data).
            var home = Environment.GetEnvironmentVariable("HISAAB_KITAAB_HOME");
            var settings = string.IsNullOrWhiteSpace(home)
                ? new SettingsService(SettingsService.DefaultFilePath)
                : new SettingsService(Path.Combine(home, "settings.json"), Path.Combine(home, "Workbooks"));
            settings.Load();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(settings, new DialogService(), new ExcelService(settings.DataFolder, () => settings.CreditCard), new LauncherService()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
