using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HisaabKitaab.ViewModels;

namespace HisaabKitaab.Views;

public partial class ExpensesView : UserControl
{
    private ExpensesViewModel? _vm;
    private TopLevel? _topLevel;

    public ExpensesView()
    {
        InitializeComponent();

        // Key-up, not key-down: NumberBox commits its value (and evaluates "250+200")
        // on key-up, so saving any earlier would miss the amount just typed.
        // handledEventsToo because NumberBox marks the event handled.
        EntryForm.AddHandler(KeyUpEvent, OnEntryKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(KeyDownEvent, OnPageKeyDown);
        _topLevel = null;
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
            _vm.EntryFocusRequested -= OnEntryFocusRequested;

        _vm = DataContext as ExpensesViewModel;

        if (_vm is not null)
            _vm.EntryFocusRequested += OnEntryFocusRequested;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnEntryFocusRequested(this, EventArgs.Empty);

        // Ctrl+F jumps to search. Listened for on the window rather than this page,
        // because focus can end up outside the page (e.g. when the button that had
        // it is disabled during a load) and the shortcut should still work.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnEntryFocusRequested(object? sender, EventArgs e)
    {
        // Posted so it runs after the form's bindings have updated.
        Dispatcher.UIThread.Post(() =>
        {
            // NumberBox itself isn't focusable; its inner TextBox is. If the cursor is
            // already there (the user started typing the next amount while the last
            // one saved), leave it alone rather than selecting what they typed.
            if (AmountBox.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { IsFocused: false } text)
            {
                text.Focus();
                text.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnEntryKeyUp(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Source is not Visual source)
            return;

        // Enter/Escape in a dropdown belong to the dropdown, and a focused
        // button already acts on Enter by itself (saving here too would add twice).
        if (source.FindAncestorOfType<ComboBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<CalendarDatePicker>(includeSelf: true) is { IsDropDownOpen: true })
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
        if (_vm is not null && ExpensesGrid.SelectedItem is ExpenseRowViewModel row)
            _vm.BeginEditCommand.Execute(row);
    }
}
