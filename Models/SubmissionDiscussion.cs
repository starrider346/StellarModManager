using System;
using System.Collections.Generic;

namespace StellarModManager.Models;

public sealed record SteamAccount(Guid Uuid, string DisplayName)
{
    public short PermissionLevel { get; init; }
}
public sealed record SubmissionComment(Guid Uuid, Guid UserUuid, string DisplayName, bool IsAuthor, string Comment,
    int? Rating, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, int ReplyCount, DiscussionReply? FirstReply);

public sealed record DiscussionReply(Guid Uuid, string DisplayName, bool IsAuthor, string Comment)
{
    public Guid UserUuid { get; init; }
}

internal sealed record SubmissionDiscussion(List<SubmissionComment> Items, int Page, int PageSize, int TotalCount,
    Guid SubmissionAuthorUuid, double? AverageRating, long RatingCount, SubmissionComment? MyRatedComment);

internal sealed record SubmissionReplies(List<DiscussionReply> Items, int Page, int PageSize, int TotalCount);
