using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace StellarModManager.Models;

internal sealed class GameModProfiles
{
    public Guid ActiveProfileId { get; set; }
    public Guid? LastOperationId { get; set; }
    public List<ModProfile> Profiles { get; set; } = [];
    public Dictionary<Guid, List<ManagedInstall>> ManagedInstalls { get; set; } = [];

    [JsonIgnore]
    public List<ManagedInstall> InstalledMods
    {
        get => ManagedInstalls.GetValueOrDefault(ActiveProfileId) ?? [];
        set => ManagedInstalls[ActiveProfileId] = value;
    }
}

internal sealed record ManagedInstall(Guid SubmissionUuid, Guid ReleaseUuid, string Version, string Source,
    SubmissionType Type, string RelativePath, string Sha256, string Name, string Author, string Description,
    List<Guid> HardDependencies)
{
    internal InstalledModInfo ToModInfo() => new()
    {
        Id = SubmissionUuid.ToString(),
        ReleaseUuid = ReleaseUuid,
        Sha256 = Sha256,
        Type = Type,
        Version = Version,
        Name = Name,
        Author = Author,
        Description = Description,
        Needs = HardDependencies.Select(uuid => uuid.ToString()).ToList()
    };
}
