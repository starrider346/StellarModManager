using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StellarModManager.Services;
using StellarModManager.ViewModels;

namespace StellarModManager.Views;

public partial class OnlineModsView : UserControl
{
    private MainWindowViewModel? _viewModel;

    public OnlineModsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
        CardList.SizeChanged += (_, _) => UpdateCompactLayout();
        TableList.SizeChanged += (_, _) => UpdateListLayout();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
            {
                UpdateCompactLayout();
                UpdateListLayout();
            }
        };
        AttachedToVisualTree += (_, _) => BindViewModel();
        DetachedFromVisualTree += (_, _) => UnbindViewModel();
    }

    private void BindViewModel()
    {
        UnbindViewModel();
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.BrowsePageChanged += OnPageChanged;
            UpdateCompactLayout();
            UpdateListLayout();
        }
    }

    private void UnbindViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.BrowsePageChanged -= OnPageChanged;
            _viewModel = null;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsCompactView))
            Dispatcher.UIThread.Post(() =>
            {
                UpdateCompactLayout();
                UpdateListLayout();
            }, DispatcherPriority.Loaded);
    }

    private void UpdateListLayout()
    {
        if (_viewModel is not { IsCompactView: false } || !IsVisible || TableList.Bounds.Height <= 0)
            return;

        _viewModel.SetListCapacity(BrowseLayout.CalculateListCapacity(TableList.Bounds.Height,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
    }

    private void UpdateCompactLayout()
    {
        if (_viewModel is not { IsCompactView: true } || !IsVisible ||
            CardList.Bounds.Width <= 0 || CardList.Bounds.Height <= 0)
            return;

        _viewModel.SetCompactLayout(BrowseLayout.Calculate(CardList.Bounds.Width, CardList.Bounds.Height,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
    }

    private void OnPageChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var scroll in TableList.GetVisualDescendants().OfType<ScrollViewer>()
                .Concat(CardList.GetVisualDescendants().OfType<ScrollViewer>()))
                scroll.Offset = default;
        }, DispatcherPriority.Loaded);
}
