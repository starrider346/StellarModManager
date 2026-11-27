using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using StellarModManager.Services;
using System;
using System.Collections.Generic; // Lists
using System.Collections.ObjectModel; // image collection
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;

namespace StellarModManager.Models;

// Reuse for regular tags and ai tag
public record ModTag(string Text, bool IsAssisted = false, bool IsVibecoded = false, bool IsActive = false, bool IsInactive = false);

public partial class OnlineModInfo : ModInfo
{
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(PublishedText));
        OnPropertyChanged(nameof(DownloadsText));
        foreach (var entry in Changelog)
            entry.Installation?.RefreshLocalizedText();
        NotifyCompatibilityChanged();
        RebuildTags();
    }

    public SubmissionType Type { get; init; }
    internal CatalogRelease? Release { get; private set; }
    internal (Guid?, string?, string?, string?) ChangelogRevision =>
        (Release?.Uuid, Release?.Version, Release?.Sha256,
            JsonSerializer.Serialize(new { Release?.Changelog, Release?.LatestTestedVersion }));
    [ObservableProperty]
    public partial string? CurrentGameVersion { get; set; }

    public string? LatestTestedVersion => Release?.LatestTestedVersion;
    public bool HasCompatibilityWarning => Release != null && CurrentGameVersion != null &&
        !string.Equals(CurrentGameVersion.Trim(), LatestTestedVersion?.Trim(), StringComparison.Ordinal);
    public bool IsGameVersionUnavailable => Release != null && CurrentGameVersion == null;
    public string CompatibilityWarning => LocalizationService.Instance[
        Type == SubmissionType.Plugin ? "PluginCompatibilityWarning" : "ModCompatibilityWarning"];
    public string CompatibilityVersions => LocalizationService.Instance.Format("CompatibilityVersions",
        CurrentGameVersion ?? LocalizationService.Instance["GameVersionUnavailable"],
        string.IsNullOrWhiteSpace(LatestTestedVersion) ? LocalizationService.Instance["TestedVersionNotSpecified"] : LatestTestedVersion);
    public string CompatibilityTooltip => CompatibilityWarning + "\n" + CompatibilityVersions;

    partial void OnCurrentGameVersionChanged(string? value) => NotifyCompatibilityChanged();

    private void NotifyCompatibilityChanged()
    {
        OnPropertyChanged(nameof(LatestTestedVersion));
        OnPropertyChanged(nameof(HasCompatibilityWarning));
        OnPropertyChanged(nameof(IsGameVersionUnavailable));
        OnPropertyChanged(nameof(CompatibilityWarning));
        OnPropertyChanged(nameof(CompatibilityVersions));
        OnPropertyChanged(nameof(CompatibilityTooltip));
    }

    internal int ChangelogGeneration { get; private set; }

    internal void InvalidateChangelog()
    {
        ChangelogGeneration++;
        ChangelogTask = null;
        IsChangelogLoading = false;
        ChangelogUnavailable = false;
    }

    public bool CanInstall => Release?.IsInstallable == true;
    public bool CanStartInstall => CanInstall && !IsInstalling;
    public bool ShowQuickInstall => CanStartInstall && ShowNoState;
    public string OptionalDependencies => string.Join(", ", Release?.Dependencies.Soft.Select(item => item.Name) ?? []);
    public bool HasOptionalDependencies => OptionalDependencies.Length > 0;

    public string ThumbnailUrl { get; set; } = "";
    internal string? IconUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListImage))]
    public partial Bitmap? ThumbnailImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListImage), nameof(ListImageStretch))]
    public partial Bitmap? IconImage { get; set; }

    public Bitmap? ListImage => IconImage ?? ThumbnailImage;
    public Avalonia.Media.Stretch ListImageStretch => IconImage is null
        ? Avalonia.Media.Stretch.UniformToFill : Avalonia.Media.Stretch.Uniform;

    internal Task? ThumbnailLoadTask { get; set; }
    internal Task? IconLoadTask { get; set; }
    internal int ThumbnailGeneration { get; set; }
    internal int IconGeneration { get; set; }

    public List<string> ImageUrls { get; set; } = [];
    public ObservableCollection<Bitmap> GalleryImages { get; } = [];
    public bool GalleryLoaded { get; set; }

    public string? RepoUrl { get; private set; }

    internal void SetRelease(CatalogRelease? release)
    {
        Release = release;
        NotifyCompatibilityChanged();
        Version = release?.Version ?? "";
        Needs = release?.Dependencies.Hard.Select(item => item.Uuid.ToString()).ToList() ?? [];
        RepoUrl = null;
        if (Uri.TryCreate(release?.Source, UriKind.Absolute, out var url) && url.Host == "github.com" &&
            url.Scheme == Uri.UriSchemeHttps && url.Segments.Length >= 3)
        {
            RepoUrl = new Uri(url, string.Concat(url.Segments.Take(3))).AbsoluteUri.TrimEnd('/');
        }

        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanStartInstall));
        OnPropertyChanged(nameof(ShowQuickInstall));
        OnPropertyChanged(nameof(OptionalDependencies));
        OnPropertyChanged(nameof(HasOptionalDependencies));
        OnPropertyChanged(nameof(RepoUrl));
    }

    // I just put AI tags in here so they can't be used as regular tags
    private static readonly HashSet<string> ReservedTags = new(StringComparer.OrdinalIgnoreCase)
        { "self-written", "ai-assisted", "mostly-generated", "active", "inactive" };

    [JsonIgnore]
    public List<ModTag> _Tags { get; private set; } = []; // TODO: please rename sensibly

    public string TagSummary => string.Join(", ", _Tags.Select(tag => tag.Text));

    public void RebuildTags()
    {
        var list = new List<ModTag>();
        var loc = LocalizationService.Instance;

        // AI badge
        switch ((Ai ?? "").Trim().ToLowerInvariant())
        {
            case "self-written": list.Add(new ModTag(loc["AiFree"])); break;
            case "ai-assisted": list.Add(new ModTag(loc["AiAssisted"], IsAssisted: true)); break;
            case "mostly-generated": list.Add(new ModTag(loc["AiVibecoded"], IsVibecoded: true)); break;
        }

        switch ((Development ?? "").Trim().ToLowerInvariant())
        {
            case "active":
                list.Add(new ModTag(LocalizationService.Instance["DevelopmentActive"], IsActive: true));
                break;
            case "inactive":
                list.Add(new ModTag(LocalizationService.Instance["DevelopmentInactive"], IsInactive: true));
                break;
        }

        list.AddRange((Tags ?? [])
            .Select(tag => (tag ?? "").Trim())
            .Where(tag => tag.Length is > 0 and <= 24 && !ReservedTags.Contains(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .Select(tag => new ModTag(tag)));

        _Tags = list;
        OnPropertyChanged(nameof(_Tags));
        OnPropertyChanged(nameof(TagSummary));
    }

    [JsonIgnore]
    public ObservableCollection<ChangelogEntry> Changelog { get; } = [];

    [JsonIgnore]
    public Task? ChangelogTask { get; set; }

    [ObservableProperty]
    public partial bool IsChangelogLoading { get; set; }

    [ObservableProperty]
    public partial bool ChangelogUnavailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublishedText))]
    public partial DateTime? PublishedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadsText))]
    public partial long Downloads { get; set; }

    public string DownloadsText => string.Format(LocalizationService.Instance["DownloadsFormat"], Downloads);

    public string PublishedText => PublishedAt is DateTime published ? Ago(published) : "";

    private static string Ago(DateTime updated)
    {
        var loc = LocalizationService.Instance;
        int days = (int)(DateTime.UtcNow - updated.ToUniversalTime()).TotalDays;
        return days switch
        {
            < 1 => loc["AgoToday"],
            1 => loc["AgoYesterday"],
            < 30 => string.Format(loc["AgoDays"], days),
            _ => updated.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentUICulture)
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstalledState), nameof(ShowUpdateState), nameof(ShowNoState), nameof(CanStartInstall), nameof(ShowQuickInstall))]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstalledState), nameof(ShowUpdateState), nameof(ShowNoState), nameof(ShowQuickInstall))]
    public partial bool IsInstalled { get; set; }

    // Set when the installed copy is older than this version
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstalledState), nameof(ShowUpdateState), nameof(ShowQuickInstall))]
    public partial bool HasUpdate { get; set; }

    // State column: exactly one of these (or IsInstalling) is shown
    [JsonIgnore]
    public bool ShowInstalledState => IsInstalled && !HasUpdate && !IsInstalling;

    [JsonIgnore]
    public bool ShowUpdateState => HasUpdate && !IsInstalling;

    [JsonIgnore]
    public bool ShowNoState => !IsInstalled && !IsInstalling;

    [JsonIgnore]
    public string InstallButtonText => IsInstalled ? LocalizationService.Instance["Reinstall"] : LocalizationService.Instance["Install"];

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(InstallButtonText));
    }
}
