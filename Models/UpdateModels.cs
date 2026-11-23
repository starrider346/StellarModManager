using System;

namespace StellarModManager.Models;

#pragma warning disable IDE1006 // Naming Styles

public record UpdateInfo(Version Version, GitHubAsset[] Assets, string ReleaseNotes, string ReleaseUrl);
public record GitHubRelease(string tag_name, GitHubAsset[] assets, string body, DateTime? published_at, string html_url);
public record GitHubAsset(string name, string browser_download_url, long size, long download_count);
public record ChangelogEntry(Guid ReleaseId, string Version, string Notes)
{
    public bool IsNewest { get; init; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public OnlineModInfo? Installation { get; init; }
    public System.Collections.Generic.List<ReleaseDependencyInfo> Dependencies { get; init; } = [];
    public bool HasDependencies => Dependencies.Count > 0;
}

public record ReleaseDependencyInfo(Guid Uuid, string Name, bool IsHard);

#pragma warning restore IDE1006 // Naming Styles
