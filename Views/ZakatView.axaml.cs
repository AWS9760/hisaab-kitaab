using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HisaabKitaab.ViewModels;

namespace HisaabKitaab.Views;

public partial class ZakatView : UserControl
{
    private ZakatViewModel? _vm;

    public ZakatView()
    {
        InitializeComponent();

        // Key-up because NumberBox commits its value on key-up; see ExpensesView.
        EntryForm.AddHandler(KeyUpEvent, OnEntryKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
            _vm.EntryFocusRequested -= OnEntryFocusRequested;

        _vm = DataContext as ZakatViewModel;

        if (_vm is not null)
            _vm.EntryFocusRequested += OnEntryFocusRequested;
    }

    private void OnEntryFocusRequested(object? sender, EventArgs e) => FormHelpers.FocusNumberBox(AmountBox);

    private void OnEntryKeyUp(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !FormHelpers.FormShouldHandleKey(e.Source))
            return;

        if (e.Key == Key.Enter && _vm.SaveEntryCommand.CanExecute(null))
        {
            _vm.SaveEntryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.IsEditing)
        {
            _vm.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null && ZakatGrid.SelectedItem is ZakatRowViewModel row)
            _vm.BeginEditCommand.Execute(row);
    }
}
