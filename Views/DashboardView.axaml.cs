using Avalonia.Controls;

namespace HisaabKitaab.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();

        // Selecting a member asks the page to scroll that pill into view, which
        // scrolled the whole dashboard down on every load. The pills are always
        // visible anyway, so ignore the request.
        MemberPicker.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true);
    }
}
