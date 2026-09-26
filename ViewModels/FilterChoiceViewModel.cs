using CommunityToolkit.Mvvm.ComponentModel;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One tickable option in a multi-select filter (a member, category or payment method).
/// </summary>
public partial class FilterChoiceViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    [ObservableProperty]
    private bool _isSelected;

    public FilterChoiceViewModel(string value, string label, Action onChanged, bool isSelected = false)
    {
        Value = value;
        Label = label;
        _onChanged = onChanged;
        _isSelected = isSelected;
    }

    /// <summary>
    /// What the filter matches on (an empty string means "none set").
    /// </summary>
    public string Value { get; }

    public string Label { get; }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}
