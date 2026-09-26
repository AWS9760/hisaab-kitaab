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
            var settings = new SettingsService(SettingsService.DefaultFilePath);
            settings.Load();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(settings, new DialogService(), new ExcelService(settings.DataFolder)),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
