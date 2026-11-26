using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StellarModManager.Services;
using StellarModManager.ViewModels;

namespace StellarModManager.Views;

public partial class LogsView : UserControl
{
    private LogViewerViewModel? _viewModel;
    private bool _pendingScroll;
    private bool _copyInProgress;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateViewModel();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
                UpdateViewModel();
        };
        AttachedToVisualTree += (_, _) => UpdateViewModel();
        DetachedFromVisualTree += (_, _) => UnbindViewModel();
    }

    private void UpdateViewModel()
    {
        var next = this.IsAttachedToVisualTree() && IsVisible ? DataContext as LogViewerViewModel : null;
        if (next == _viewModel)
            return;

        UnbindViewModel();
        _viewModel = next;
        if (_viewModel != null)
        {
            _viewModel.EntriesChanging += OnEntriesChanging;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.Start();
        }
    }

    private void UnbindViewModel()
    {
        _pendingScroll = false;
        CopyStatus.Text = null;
        if (_viewModel != null)
        {
            _viewModel.EntriesChanging -= OnEntriesChanging;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Stop();
            _viewModel = null;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewerViewModel.SelectedFile))
            CopyStatus.Text = null;
    }

    private void OnEntriesChanging(LogViewerViewModel.ScrollPosition position)
    {
        if (_pendingScroll)
            return;
        if (position == LogViewerViewModel.ScrollPosition.Follow && Entries.Scroll is ScrollViewer scroll &&
            scroll.Offset.Y + 2 < scroll.ScrollBarMaximum.Y)
            return;

        _pendingScroll = true;
        Dispatcher.UIThread.Post(ApplyScroll, DispatcherPriority.Loaded);
    }

    private void ApplyScroll()
    {
        var queued = _pendingScroll;
        _pendingScroll = false;
        if (!queued || Entries.ItemCount == 0)
            return;

        Entries.ScrollIntoView(Entries.ItemCount - 1);
        if (Entries.Scroll is ScrollViewer scroll)
            scroll.ScrollToEnd();
    }

    private async void CopyLog(object? sender, RoutedEventArgs e)
    {
        if (_copyInProgress || _viewModel is not { SelectedFile: { } file } viewModel)
            return;

        _copyInProgress = true;
        CopyStatus.Text = null;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                throw new InvalidOperationException("Clipboard unavailable.");

            var text = await viewModel.CopyTextAsync(file);
            if (text == null)
                return;

            await clipboard.SetTextAsync(text);
            if (_viewModel == viewModel && viewModel.SelectedFile == file)
                CopyStatus.Text = LocalizationService.Instance["LogCopied"];
        }
        catch (Exception)
        {
            if (_viewModel == viewModel && viewModel.SelectedFile == file)
                CopyStatus.Text = LocalizationService.Instance["LogCopyFailed"];
        }
        finally
        {
            _copyInProgress = false;
        }
    }
}
