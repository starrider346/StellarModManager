using StellarModManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StellarModManager.Services;

internal sealed record ImportedProfilePack(string Name, IReadOnlyList<ReleaseInstallLink> ManagedReleases,
    IReadOnlyList<string> ExternalPaths, IReadOnlyList<InstalledModInfo> ExternalMods);

internal static class ProfilePack
{
    internal const string Extension = ".sdpack";
    private const int SchemaVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string ExternalPrefix = "external/";

    private sealed record Manifest(int SchemaVersion, string Name, List<ManifestMod> Mods);

    private sealed record ManifestMod(string Kind, Guid? SubmissionUuid = null, Guid? ReleaseUuid = null,
        string? Path = null, string[]? Names = null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowDuplicateProperties = false,
        WriteIndented = true
    };

    internal static void Write(Stream destination, string name, string gamePath, IReadOnlyList<ManagedInstall> installed)
    {
        var managedPaths = installed.Select(mod => mod.RelativePath).ToHashSet(ProfileFiles.PathComparer);
        var mods = installed.OrderBy(mod => mod.SubmissionUuid)
            .Select(mod => new ManifestMod("managed", mod.SubmissionUuid, mod.ReleaseUuid)).ToList();
        var external = ExternalModScanner.Scan(gamePath, managedPaths)
            .GroupBy(mod => mod.ExternalPath!, ProfileFiles.PathComparer)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        mods.AddRange(external.Select(group => new ManifestMod("external", Path: group.Key,
            Names: group.Select(mod => mod.Name).Order(StringComparer.Ordinal).ToArray())));
        var manifest = new Manifest(SchemaVersion, name, mods);
        var (_, externalFiles) = ValidateManifest(manifest);
        ValidateDestinations(managedPaths.Concat(externalFiles.Keys));

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        using (var content = archive.CreateEntry(ManifestEntry).Open())
            JsonSerializer.Serialize(content, manifest, JsonOptions);
        foreach (var path in externalFiles.Keys)
        {
            using var input = File.OpenRead(ProfileFiles.Resolve(gamePath, path));
            using var output = archive.CreateEntry(ExternalPrefix + path).Open();
            input.CopyTo(output);
        }
    }

    internal static ImportedProfilePack Read(Stream source, string workspace, string externalRoot)
    {
        using var buffered = source.CanSeek ? null : new FileStream(Path.Combine(workspace, "input.sdpack"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        if (buffered != null)
        {
            source.CopyTo(buffered);
            buffered.Position = 0;
        }

        using var archive = new ZipArchive(buffered ?? source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            const int unixTypeMask = 0xF000;
            const int unixRegularFile = 0x8000;
            var unixType = (entry.ExternalAttributes >> 16) & unixTypeMask;
            if (unixType is not (0 or unixRegularFile) ||
                (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !entries.TryAdd(entry.FullName, entry))
                throw Invalid($"Invalid ZIP entry {entry.FullName}.");
        }

        if (!entries.Remove(ManifestEntry, out var manifestEntry))
            throw Invalid($"Missing {ManifestEntry}.");

        Manifest? manifest;
        using (var content = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(content, JsonOptions);
        var (managed, externalFiles) = ValidateManifest(manifest);
        if (entries.Count != externalFiles.Count || externalFiles.Keys.Any(path => !entries.ContainsKey(ExternalPrefix + path)))
            throw Invalid("ZIP payloads do not match the manifest.");

        foreach (var path in externalFiles.Keys)
        {
            var destination = ProfileFiles.Resolve(externalRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var entry = entries[ExternalPrefix + path];
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (output.Length != entry.Length)
                throw Invalid($"ZIP entry {entry.FullName} is incomplete.");
        }

        var externalMods = ExternalModScanner.Scan(externalRoot, new HashSet<string>(ProfileFiles.PathComparer));
        var namesByPath = externalMods.ToLookup(mod => mod.ExternalPath!, ProfileFiles.PathComparer);
        foreach (var (path, names) in externalFiles)
        {
            if (!namesByPath[path].Select(mod => mod.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Invalid($"External mod metadata does not match the manifest for {path}.");
        }

        return new ImportedProfilePack(manifest!.Name.Trim(), managed, externalFiles.Keys.ToArray(), externalMods);
    }

    private static (List<ReleaseInstallLink> Managed, Dictionary<string, string[]> External) ValidateManifest(Manifest? manifest)
    {
        if (manifest is not { SchemaVersion: SchemaVersion, Mods: not null } || string.IsNullOrWhiteSpace(manifest.Name))
            throw Invalid("The profile name, mod list, or schema version is invalid.");

        var managed = new List<ReleaseInstallLink>();
        var submissions = new HashSet<Guid>();
        var external = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.Mods)
        {
            switch (mod)
            {
                case { Kind: "managed", SubmissionUuid: Guid submission, ReleaseUuid: Guid release, Path: null, Names: null }
                    when submission != Guid.Empty && release != Guid.Empty && submissions.Add(submission):
                    managed.Add(new ReleaseInstallLink(submission, release));
                    break;
                case { Kind: "external", SubmissionUuid: null, ReleaseUuid: null, Path: string path, Names: { Length: > 0 } names }
                    when external.TryAdd(path, names):
                    break;
                default:
                    throw Invalid("The manifest contains an invalid or duplicate mod entry.");
            }
        }

        ValidateDestinations(external.Keys);
        return (managed, external);
    }

    internal static void ValidateDestinations(IEnumerable<string> destinations)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in destinations)
        {
            var parts = path.Split('/');
            if (parts.Length < 2 || parts[0] is not (ProfileFiles.Mods or ProfileFiles.Plugins) ||
                !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || parts.Any(IsInvalidPathSegment))
                throw Invalid($"Invalid mod path {path}.");

            if (!files.Add(path) || directories.Contains(path))
                throw Invalid($"More than one mod uses {path}.");

            for (var separator = path.LastIndexOf('/'); separator > 0; separator = path.LastIndexOf('/', separator - 1))
            {
                var directory = path[..separator];
                if (files.Contains(directory))
                    throw Invalid($"A mod conflicts with the directory {directory}.");
                directories.Add(directory);
            }
        }
    }

    private static bool IsInvalidPathSegment(string segment)
    {
        if (segment.Length == 0 || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c)))
            return true;

        var stem = segment.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(stem[^1]);
    }

    private static InvalidDataException Invalid(string detail) =>
        new($"{LocalizationService.Instance["StatusProfileInvalid"]} {detail}");
}
