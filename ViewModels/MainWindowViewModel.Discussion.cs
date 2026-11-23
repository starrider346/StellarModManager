using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    private Guid? _discussionUuid;
    private Guid? _submissionAuthorUuid;
    private int _discussionPage;
    private int _discussionLoadVersion;
    private CancellationTokenSource? _discussionRequest;
    private Guid? _draftAccount;
    private (Guid Comment, Guid? Parent, Guid Author)? _discussionCommentPendingDeletion;

    public ObservableCollection<DiscussionThreadItem> DiscussionThreads { get; } = [];
    public bool ShowsDiscussion => _discussionUuid.HasValue;
    public bool CanPostDiscussion => HasSteamUser && _submissionAuthorUuid.HasValue &&
        (!_draftAccount.HasValue || _draftAccount == SteamUser?.Uuid);
    public bool CanDeleteDiscussionComments => SteamUser is { PermissionLevel: >= 3 };
    public bool CanRateDiscussion => CanPostDiscussion && ReplyingTo is null &&
        (MyRatedComment is null || EditingComment?.Uuid == MyRatedComment.Uuid);

    public IReadOnlyList<DiscussionRatingOption> DiscussionRatings => Enumerable.Range(1, 5).Concat([0, -1])
        .Select(rating => new DiscussionRatingOption(rating < 0 ? null : rating,
            rating < 0 ? LocalizationService.Instance["DiscussionNoRating"] :
                LocalizationService.Instance.Format("DiscussionStars", rating))).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiscussionDraft))]
    public partial string CommentDraft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiscussionDraft))]
    public partial int SelectedDiscussionRating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRateDiscussion), nameof(HasDiscussionDraft), nameof(DiscussionComposerLabel), nameof(DiscussionPostLabel))]
    public partial SubmissionComment? EditingComment { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRateDiscussion), nameof(HasDiscussionDraft), nameof(DiscussionComposerLabel), nameof(DiscussionPostLabel))]
    public partial SubmissionComment? ReplyingTo { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRateDiscussion))]
    public partial SubmissionComment? MyRatedComment { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscussionScore))]
    public partial double? AverageDiscussionRating { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscussionScore))]
    public partial long DiscussionRatingCount { get; private set; }

    [ObservableProperty]
    public partial bool IsDiscussionLoading { get; private set; }

    [ObservableProperty]
    public partial bool HasMoreDiscussion { get; private set; }

    [ObservableProperty]
    public partial bool IsPostingDiscussion { get; private set; }

    [ObservableProperty]
    public partial string? DiscussionLoadStatus { get; private set; }

    [ObservableProperty]
    public partial string? DiscussionWriteStatus { get; private set; }

    public string? DiscussionScore => _discussionPage == 0 ? null : AverageDiscussionRating is { } average
        ? LocalizationService.Instance.Format("DiscussionAverage", average, DiscussionRatingCount)
        : LocalizationService.Instance["DiscussionNoRatings"];

    public bool HasDiscussionDraft => EditingComment is not null || ReplyingTo is not null ||
        CommentDraft.Length > 0 || SelectedDiscussionRating > 0;

    public string DiscussionPostLabel => LocalizationService.Instance[EditingComment is null ? "DiscussionPost" : "DiscussionSave"];

    public string DiscussionComposerLabel => ReplyingTo is { } parent
        ? LocalizationService.Instance.Format("DiscussionReplyTo", parent.DisplayName)
        : LocalizationService.Instance[EditingComment is null ? "DiscussionLeaveComment" : "DiscussionEditComment"];

    private void SelectDiscussion(OnlineModInfo? selection)
    {
        Guid? uuid = Guid.TryParse(selection?.Id, out var id) ? id : null;
        if (_discussionUuid == uuid)
            return;

        if (_discussionCommentPendingDeletion is not null)
            CancelRemove();

        _discussionUuid = uuid;
        _discussionRequest?.Cancel();
        _discussionRequest?.Dispose();
        _discussionRequest = uuid.HasValue ? new CancellationTokenSource() : null;
        _submissionAuthorUuid = null;
        _discussionPage = 0;
        AverageDiscussionRating = null;
        DiscussionRatingCount = 0;
        MyRatedComment = null;
        DiscussionThreads.Clear();
        HasMoreDiscussion = false;
        IsDiscussionLoading = false;
        IsPostingDiscussion = false;
        DiscussionLoadStatus = null;
        ResetDiscussionDraft();
        OnPropertyChanged(nameof(ShowsDiscussion));
        OnPropertyChanged(nameof(CanPostDiscussion));
        OnPropertyChanged(nameof(DiscussionScore));
        _ = ReloadDiscussionAsync();
    }

    private Task ReloadDiscussionAsync() => LoadDiscussionAsync(reset: true);

    [RelayCommand]
    private Task LoadMoreDiscussionAsync() => LoadDiscussionAsync(reset: _discussionPage == 0);

    private async Task LoadDiscussionAsync(bool reset)
    {
        if (_discussionUuid is not { } uuid || _discussionRequest is not { } selection || IsDiscussionLoading && !reset)
            return;

        var cancellation = selection.Token;
        var session = _desktopSession;
        var version = ++_discussionLoadVersion;
        var page = reset ? 1 : _discussionPage + 1;
        IsDiscussionLoading = true;
        DiscussionLoadStatus = null;
        try
        {
            var result = await _desktopAccount.DiscussionAsync(uuid, page, session, cancellation);
            if (selection != _discussionRequest || version != _discussionLoadVersion || !ReferenceEquals(session, _desktopSession))
                return;

            _submissionAuthorUuid = result.SubmissionAuthorUuid;
            _discussionPage = result.Page;
            AverageDiscussionRating = result.AverageRating;
            DiscussionRatingCount = result.RatingCount;
            MyRatedComment = result.MyRatedComment;
            HasMoreDiscussion = (long)result.Page * result.PageSize < result.TotalCount;
            if (reset)
                DiscussionThreads.Clear();

            foreach (var comment in result.Items)
            {
                if (DiscussionThreads.Any(thread => thread.Entry.Uuid == comment.Uuid))
                    continue;
                DiscussionThreads.Add(new DiscussionThreadItem(comment, SteamUser?.Uuid == comment.UserUuid,
                    SteamUser?.Uuid == result.SubmissionAuthorUuid));
            }

            if (DiscussionThreads.Count == 0)
                DiscussionLoadStatus = LocalizationService.Instance["DiscussionEmpty"];
            OnPropertyChanged(nameof(DiscussionScore));
            OnPropertyChanged(nameof(CanPostDiscussion));
            OnPropertyChanged(nameof(CanRateDiscussion));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsAccountError(exception))
        {
            if (selection == _discussionRequest && version == _discussionLoadVersion && ReferenceEquals(session, _desktopSession))
            {
                DiscussionLoadStatus = LocalizationService.Instance["DiscussionLoadFailed"];
                HasMoreDiscussion = true;
            }
        }
        finally
        {
            if (selection == _discussionRequest && version == _discussionLoadVersion && ReferenceEquals(session, _desktopSession))
                IsDiscussionLoading = false;
        }
    }

    [RelayCommand]
    private Task LoadDiscussionRepliesAsync(DiscussionThreadItem? thread) => LoadRepliesAsync(thread, reset: false);

    private async Task LoadRepliesAsync(DiscussionThreadItem? thread, bool reset)
    {
        if (_discussionUuid is not { } uuid || _discussionRequest is not { } selection || thread is null ||
            !reset && (thread.IsLoadingReplies || !thread.HasMoreReplies))
            return;

        var version = ++thread.ReplyRequestVersion;
        thread.IsLoadingReplies = true;
        thread.ReplyStatus = null;
        var cancellation = selection.Token;
        try
        {
            var result = await _desktopAccount.RepliesAsync(uuid, thread.Entry.Uuid, reset ? 1 : thread.ReplyPage + 1, cancellation);
            if (selection != _discussionRequest || version != thread.ReplyRequestVersion || !DiscussionThreads.Contains(thread))
                return;

            if (reset)
                thread.Replies.Clear();
            foreach (var reply in result.Items.Where(reply => thread.Replies.All(shown => shown.Uuid != reply.Uuid)))
                thread.Replies.Add(reply);
            thread.ReplyPage = result.Page;
            thread.HasMoreReplies = (long)result.Page * result.PageSize < result.TotalCount;
            if (thread.Replies.Count == 0)
                thread.ReplyStatus = LocalizationService.Instance["DiscussionNoReplies"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsAccountError(exception))
        {
            if (version == thread.ReplyRequestVersion)
            {
                thread.ReplyStatus = LocalizationService.Instance["DiscussionLoadFailed"];
                thread.HasMoreReplies = true;
            }
        }
        finally
        {
            if (version == thread.ReplyRequestVersion)
                thread.IsLoadingReplies = false;
        }
    }

    [RelayCommand]
    private void EditDiscussionComment(SubmissionComment? comment)
    {
        if (comment is null || SteamUser?.Uuid != comment.UserUuid || IsPostingDiscussion)
            return;

        ReplyingTo = null;
        EditingComment = comment;
        _draftAccount = SteamUser.Uuid;
        CommentDraft = comment.Comment;
        SelectedDiscussionRating = (comment.Rating ?? -1) + 1;
        DiscussionWriteStatus = null;
    }

    [RelayCommand]
    private void ReplyToDiscussionComment(SubmissionComment? comment)
    {
        if (comment is null || SteamUser is null || SteamUser.Uuid != _submissionAuthorUuid || IsPostingDiscussion)
            return;

        EditingComment = null;
        ReplyingTo = comment;
        _draftAccount = SteamUser.Uuid;
        CommentDraft = "";
        SelectedDiscussionRating = 0;
        DiscussionWriteStatus = null;
    }

    [RelayCommand]
    private void ResetDiscussionDraft()
    {
        EditingComment = null;
        ReplyingTo = null;
        CommentDraft = "";
        SelectedDiscussionRating = 0;
        DiscussionWriteStatus = null;
        _draftAccount = null;
        OnPropertyChanged(nameof(CanPostDiscussion));
    }

    [RelayCommand]
    private async Task PostDiscussionAsync()
    {
        if (_discussionUuid is not { } uuid || _discussionRequest is not { } selection ||
            _desktopSession is not { } session || !CanPostDiscussion || IsPostingDiscussion)
            return;

        var comment = CommentDraft.Trim();
        if (comment.Length == 0)
        {
            DiscussionWriteStatus = LocalizationService.Instance["DiscussionCommentRequired"];
            return;
        }

        var cancellation = selection.Token;
        var edit = EditingComment?.Uuid;
        var parent = ReplyingTo?.Uuid;
        int? rating = CanRateDiscussion && SelectedDiscussionRating > 0 ? SelectedDiscussionRating - 1 : null;
        IsPostingDiscussion = true;
        DiscussionWriteStatus = null;
        _draftAccount = SteamUser?.Uuid;
        try
        {
            using var result = await _desktopAccount.WriteCommentAsync(uuid, edit, parent, comment, rating, session, cancellation);
            if (selection != _discussionRequest || !ReferenceEquals(session, _desktopSession))
                return;

            if (result.StatusCode == HttpStatusCode.Unauthorized)
            {
                ExpireSteamSession(session);
                DiscussionWriteStatus = LocalizationService.Instance["SteamSessionExpired"];
                return;
            }

            if (result.StatusCode == HttpStatusCode.Conflict)
            {
                DiscussionWriteStatus = LocalizationService.Instance["DiscussionAlreadyRated"];
                await ReloadDiscussionAsync();
                return;
            }

            result.EnsureSuccessStatusCode();
            ResetDiscussionDraft();
            if (parent is { } parentUuid && DiscussionThreads.FirstOrDefault(thread => thread.Entry.Uuid == parentUuid) is { } thread)
            {
                thread.ReplyPage = 0;
                await LoadRepliesAsync(thread, reset: true);
            }
            else
                await ReloadDiscussionAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsAccountError(exception))
        {
            if (selection == _discussionRequest)
                DiscussionWriteStatus = LocalizationService.Instance["DiscussionWriteFailed"];
        }
        finally
        {
            if (selection == _discussionRequest)
                IsPostingDiscussion = false;
        }
    }

    [RelayCommand]
    private void RequestDeleteDiscussionComment(Guid? commentUuid)
    {
        if (commentUuid is not { } comment || !HasSteamUser || IsPostingDiscussion || IsConfirmRemoveOpen)
            return;

        var thread = DiscussionThreads.FirstOrDefault(item => item.Entry.Uuid == comment || item.Replies.Any(reply => reply.Uuid == comment));
        if (thread is null)
            return;

        Guid? parent = thread.Entry.Uuid == comment ? null : thread.Entry.Uuid;
        var author = parent.HasValue ? thread.Replies.First(reply => reply.Uuid == comment).UserUuid : thread.Entry.UserUuid;
        if (!CanDeleteDiscussionComment(author))
            return;

        _discussionCommentPendingDeletion = (comment, parent, author);
        ShowRemoveConfirmation("DiscussionDeleteTitle", "DiscussionDelete",
            parent.HasValue ? "DiscussionDeleteReplyMessage" : "DiscussionDeleteCommentMessage");
    }

    private bool CanDeleteDiscussionComment(Guid author) => CanDeleteDiscussionComments || SteamUser?.Uuid == author;

    private async Task DeleteDiscussionCommentAsync(Guid comment, Guid? parent)
    {
        if (_discussionUuid is not { } uuid || _discussionRequest is not { } selection ||
            _desktopSession is not { } session || IsPostingDiscussion)
            return;

        var cancellation = selection.Token;
        IsPostingDiscussion = true;
        DiscussionWriteStatus = null;
        try
        {
            using var response = await _desktopAccount.DeleteCommentAsync(uuid, comment, session, cancellation);
            if (selection != _discussionRequest || !ReferenceEquals(session, _desktopSession))
                return;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ExpireSteamSession(session);
                DiscussionWriteStatus = LocalizationService.Instance["SteamSessionExpired"];
                return;
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
                response.EnsureSuccessStatusCode();

            if (parent is { } parentUuid && DiscussionThreads.FirstOrDefault(item => item.Entry.Uuid == parentUuid) is { } thread)
            {
                if (thread.Replies.FirstOrDefault(reply => reply.Uuid == comment) is { } reply)
                    thread.Replies.Remove(reply);
                await LoadRepliesAsync(thread, reset: true);
            }
            else
            {
                if (EditingComment?.Uuid == comment || ReplyingTo?.Uuid == comment)
                    ResetDiscussionDraft();
                if (DiscussionThreads.FirstOrDefault(item => item.Entry.Uuid == comment) is { } deletedThread)
                    DiscussionThreads.Remove(deletedThread);
                if (MyRatedComment?.Uuid == comment)
                    MyRatedComment = null;
                await ReloadDiscussionAsync();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsAccountError(exception))
        {
            if (selection == _discussionRequest && ReferenceEquals(session, _desktopSession))
                DiscussionWriteStatus = LocalizationService.Instance["DiscussionDeleteFailed"];
        }
        finally
        {
            if (selection == _discussionRequest)
                IsPostingDiscussion = false;
        }
    }

    public sealed record DiscussionRatingOption(int? Rating, string Label)
    {
        public int SelectionIndex => (Rating ?? -1) + 1;
        public bool IsStar => Rating > 0;
    }
}
