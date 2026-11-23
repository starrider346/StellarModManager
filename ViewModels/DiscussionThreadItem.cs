using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StellarModManager.Models;
using StellarModManager.Services;

namespace StellarModManager.ViewModels;

public sealed partial class DiscussionThreadItem(SubmissionComment entry, bool canEdit, bool canReply) : ObservableObject
{
    public SubmissionComment Entry { get; } = entry;
    public bool CanEdit { get; } = canEdit;
    public bool CanReply { get; } = canReply;
    public ObservableCollection<DiscussionReply> Replies { get; } = entry.FirstReply is { } reply ? [reply] : [];
    public string? RatingLabel => Entry.Rating is { } rating ? LocalizationService.Instance.Format("DiscussionStars", rating) : null;
    internal int ReplyPage { get; set; }
    internal int ReplyRequestVersion { get; set; }

    [ObservableProperty]
    public partial bool HasMoreReplies { get; set; } = entry.ReplyCount > (entry.FirstReply is null ? 0 : 1);

    [ObservableProperty]
    public partial bool IsLoadingReplies { get; set; }

    [ObservableProperty]
    public partial string? ReplyStatus { get; set; }
}
