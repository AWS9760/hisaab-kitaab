using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace HisaabKitaab.Views;

/// <summary>
/// Attached property: <c>views:TextBoxFocus.FocusWhenVisible="True"</c> focuses
/// the TextBox and selects its text whenever it becomes visible. Used for
/// inline rename boxes that are shown on demand.
/// </summary>
public static class TextBoxFocus
{
    public static readonly AttachedProperty<bool> FocusWhenVisibleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("FocusWhenVisible", typeof(TextBoxFocus));

    static TextBoxFocus()
    {
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((textBox, _) =>
        {
            if (!textBox.IsVisible || !GetFocusWhenVisible(textBox))
                return;

            // Wait until layout has run, otherwise the TextBox can't take focus yet.
            Dispatcher.UIThread.Post(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }, DispatcherPriority.Input);
        });
    }

    public static bool GetFocusWhenVisible(TextBox element) => element.GetValue(FocusWhenVisibleProperty);

    public static void SetFocusWhenVisible(TextBox element, bool value) => element.SetValue(FocusWhenVisibleProperty, value);
}
