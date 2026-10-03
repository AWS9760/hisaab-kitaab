using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using HisaabKitaab.ViewModels;

namespace HisaabKitaab.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _vm;

    public DashboardView()
    {
        InitializeComponent();

        // Selecting a member asks the page to scroll that pill into view, which
        // scrolled the whole dashboard down on every load. The pills are always
        // visible anyway, so ignore the request.
        MemberPicker.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as DashboardViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;
    }

    // Each chart is hidden while there's nothing to show. When it's shown again
    // in the same pass that gives it data (e.g. the first load at startup),
    // LiveCharts has measured it as hidden and draws nothing, so hand the
    // series over again once layout has run.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is not { } vm)
            return;

        var shown = e.PropertyName switch
        {
            nameof(DashboardViewModel.HasCategoryData) => vm.HasCategoryData,
            nameof(DashboardViewModel.HasTrendData) => vm.HasTrendData,
            nameof(DashboardViewModel.HasMemberData) => vm.HasMemberData,
            _ => false,
        };

        if (shown)
            Dispatcher.UIThread.Post(() => _vm?.RedrawCharts(), DispatcherPriority.Background);
    }
}
