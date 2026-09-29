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
            SettingsService settings;
            string? locationWarning = null;
            if (string.IsNullOrWhiteSpace(home))
            {
                // settings.json may have been moved; a pointer file in the default folder says where.
                var pointer = SettingsLocation.DefaultPointerPath;
                settings = new SettingsService(SettingsLocation.Resolve(pointer, SettingsService.DefaultFilePath, out locationWarning))
                {
                    LocationPointer = pointer,
                };
            }
            else
            {
                settings = new SettingsService(Path.Combine(home, "settings.json"), Path.Combine(home, "Workbooks"))
                {
                    DefaultSettingsFilePath = Path.Combine(home, "settings.json"),
                };
            }

            settings.Load();
            if (locationWarning is not null)
                settings.AddLoadWarning(locationWarning);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(settings, new DialogService(), new ExcelService(settings.DataFolder, () => settings.CreditCard, settings.ResolvedBudgets), new LauncherService(),
                    Notifiers.ForThisPlatform()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
