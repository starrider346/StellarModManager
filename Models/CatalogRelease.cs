using System;
using System.Collections.Generic;
using System.Linq;

namespace StellarModManager.Models;

public enum SubmissionType
{
    Mod,
    Plugin
}

internal sealed record CatalogRelease(Guid Uuid, string Version, string Changelog, string Source, string? Sha256,
    ReleaseDependencies Dependencies)
{
    internal bool IsInstallable => Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit)
        && Uri.TryCreate(Source, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
        && url.Host == "github.com" && url.IsDefaultPort && url.UserInfo.Length == 0
        && url.Query.Length == 0 && url.Fragment.Length == 0
        && url.AbsolutePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ReleaseDependencies(List<ReleaseDependency> Hard, List<ReleaseDependency> Soft);
internal sealed record ReleaseDependency(Guid Uuid, string Name);

internal sealed record CatalogPage(List<OnlineModInfo> Items, int Page, int PageSize, int TotalCount);

internal sealed record SubmissionPage(List<Submission> Items, int Page, int PageSize, int TotalCount);
internal sealed record Submission(Guid Uuid, string Type, SubmissionAuthor Author, DateTime? ReleaseTime,
    string Name, string Description, string? Thumbnail, List<string> Gallery, List<string> Tags,
    string AiDisclosure, bool ActiveDevelopment, CatalogRelease? LatestRelease, CatalogRelease? MatchedRelease)
{
    public long UniqueDownloads { get; init; }

    internal OnlineModInfo ToOnline()
    {
        var result = new OnlineModInfo
        {
            Id = Uuid.ToString(),
            Type = Type == "plugin" ? SubmissionType.Plugin : SubmissionType.Mod,
            Name = Name,
            Author = Author.DisplayName,
            Description = Description,
            ThumbnailUrl = Thumbnail ?? "",
            ImageUrls = Gallery,
            Tags = Tags,
            Ai = AiDisclosure,
            Development = ActiveDevelopment ? "active" : "inactive",
            PublishedAt = ReleaseTime,
            Downloads = UniqueDownloads
        };
        result.SetRelease(LatestRelease);
        result.RebuildTags();
        return result;
    }
}
internal sealed record SubmissionAuthor(string DisplayName);
internal sealed record ReleasePage(List<CatalogRelease> Items, int Page, int PageSize, int TotalCount);
