using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StellarModManager.ViewModels;

public sealed partial class BrowseCategoryFilterViewModel(string tag, string labelKey, Action selectionChanged) : ObservableObject
{
    private enum Selection { Neutral, Included, Excluded }

    private Selection _selection;

    public string Tag { get; } = tag;
    public string LabelKey { get; } = labelKey;
    public bool IsIncluded => _selection == Selection.Included;
    public bool IsExcluded => _selection == Selection.Excluded;

    [RelayCommand]
    private void ToggleInclude() => Toggle(Selection.Included);

    [RelayCommand]
    private void ToggleExclude() => Toggle(Selection.Excluded);

    private void Toggle(Selection selection)
    {
        _selection = _selection == selection ? Selection.Neutral : selection;
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(IsExcluded));
        selectionChanged();
    }
}
