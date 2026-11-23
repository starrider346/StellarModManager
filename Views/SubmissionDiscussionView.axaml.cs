using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using StellarModManager.Models;
using StellarModManager.ViewModels;

namespace StellarModManager.Views;

public partial class SubmissionDiscussionView : UserControl
{
    public static readonly IMultiValueConverter IsStarFilled = new FuncMultiValueConverter<object, bool>(
        values => values is [int starIndex, int selectedIndex] && starIndex <= selectedIndex);

    public static readonly IMultiValueConverter CanDeleteComment = new FuncMultiValueConverter<object, bool>(
        values => values is [bool canModerate, SteamAccount account, Guid author] && (canModerate || account.Uuid == author));

    private MainWindowViewModel? _viewModel;

    public SubmissionDiscussionView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnDiscussionChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnDiscussionChanged;
    }

    private void OnDiscussionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.EditingComment) && _viewModel?.EditingComment is not null ||
            e.PropertyName == nameof(MainWindowViewModel.ReplyingTo) && _viewModel?.ReplyingTo is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Composer.BringIntoView();
                CommentEditor.Focus();
            });
        }
    }
}
