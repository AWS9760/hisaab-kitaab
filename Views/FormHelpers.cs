using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;

namespace HisaabKitaab.Views;

/// <summary>
/// Keyboard and focus behaviour shared by the quick-entry forms.
/// </summary>
internal static class FormHelpers
{
    /// <summary>
    /// Puts the cursor in a NumberBox and selects its text, unless the cursor
    /// is already there (the user may have started typing the next amount
    /// while the last one was saving). Posted so it runs after bindings update.
    /// </summary>
    public static void FocusNumberBox(NumberBox box)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // NumberBox itself isn't focusable; its inner TextBox is.
            if (box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { IsFocused: false } text)
            {
                text.Focus();
                text.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Whether Enter/Escape from <paramref name="source"/> should act on the
    /// form. Dropdowns use those keys themselves, and a focused button already
    /// acts on Enter (saving here too would save twice).
    /// </summary>
    public static bool FormShouldHandleKey(object? source) =>
        source is Visual visual
        && visual.FindAncestorOfType<ComboBox>(includeSelf: true) is null
        && visual.FindAncestorOfType<Button>(includeSelf: true) is null
        && visual.FindAncestorOfType<CalendarDatePicker>(includeSelf: true) is not { IsDropDownOpen: true };
}
