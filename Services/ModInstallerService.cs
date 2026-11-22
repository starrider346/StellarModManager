using StellarModManager.Models;
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace StellarModManager.Services;

internal sealed record StagedMod(ManagedInstall Install, string? DownloadPath)
{
    internal string? ApprovedExternalHash { get; init; }
}

internal sealed class ExternalModConflictException(string relativePath, string sha256)
    : IOException($"An external file is blocking {relativePath}.")
{
    internal string RelativePath { get; } = relativePath;
    internal string Sha256 { get; } = sha256;
}

internal sealed class ModInstallerService : IDisposable
{
    private string? _workFolder;

    internal static ManagedInstall Describe(OnlineModInfo mod)
    {
        if (mod.Release is not { IsInstallable: true } release)
            throw new InvalidDataException($"{mod.Name} does not have a verifiable DLL release.");

        var source = new Uri(release.Source);
        var fileName = Uri.UnescapeDataString(source.Segments[^1]);
        var stem = Path.GetFileNameWithoutExtension(fileName).Split('.')[0].ToUpperInvariant();
        bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[^1] is >= '1' and <= '9';
        if (reserved || fileName.Length <= 4 || fileName.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The DLL filename for {mod.Name} is invalid.");

        var folder = mod.Type == SubmissionType.Plugin ? ProfileFiles.Plugins : ProfileFiles.Mods;
        return new ManagedInstall(Guid.Parse(mod.Id), release.Uuid, release.Version, release.Source,
            mod.Type, ProfileFiles.Normalize($"{folder}/{fileName}"), release.Sha256!.ToLowerInvariant(),
            mod.Name, mod.Author, mod.Description, release.Dependencies.Hard.Select(item => item.Uuid).ToList());
    }

    internal async Task<StagedMod> StageAsync(ManagedInstall install, ModRepositoryService repository, IProgress<double>? progress)
    {
        _workFolder ??= Directory.CreateTempSubdirectory("stellar-dll-").FullName;
        var path = Path.Combine(_workFolder, $"{Guid.NewGuid():N}.dll");
        await repository.DownloadModAsync(install.Source, path, progress);
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!string.Equals(hash, install.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded DLL for {install.Name} does not match the published SHA-256.");

        stream.Position = 0;
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || !pe.PEHeaders.IsDll || !pe.GetMetadataReader().IsAssembly)
            throw new InvalidDataException($"The download for {install.Name} is not a managed DLL.");

        await repository.LogDownloadAsync(install.SubmissionUuid, install.ReleaseUuid);
        return new StagedMod(install, path);
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_workFolder != null)
            ProfileFiles.TryDeleteDirectory(_workFolder);
    }
}
