using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace StellarModManager.Models;

public partial class InstalledModInfo : ModInfo
{
    internal Guid ReleaseUuid { get; init; }
    internal string Sha256 { get; init; } = "";
    public SubmissionType Type { get; init; }

    [JsonIgnore]
    public string? ExternalPath { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogMatch))]
    public partial string? CatalogMatch { get; set; }

    public bool HasCatalogMatch => CatalogMatch != null;

    [JsonIgnore]
    public bool IsExternal => ExternalPath != null;

    [ObservableProperty]
    public partial bool IsRemoving { get; set; }

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial double UpdateProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrent))]
    public partial bool IsUpdateAvailable { get; set; }

    // Shown in the "!" tooltip, empty means no missing dependencies
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDependencyWarning))]
    public partial string DependencyWarning { get; set; } = "";

    [JsonIgnore]
    public bool HasDependencyWarning => !string.IsNullOrEmpty(DependencyWarning);

    [JsonIgnore]
    public bool IsCurrent => !IsExternal && !IsUpdateAvailable;

    [ObservableProperty]
    public partial string? LatestVersion { get; set; }

    [ObservableProperty]
    public partial string? UpdateNotes { get; set; }

    [ObservableProperty]
    public partial OnlineModInfo? Online { get; set; }
}
