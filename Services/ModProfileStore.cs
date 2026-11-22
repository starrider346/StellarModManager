using StellarModManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StellarModManager.Services;

internal sealed class ModProfileStore
{
    private sealed class LegacyProfiles
    {
        public string ActiveProfile { get; set; } = "Default";
        public List<ModProfile> Profiles { get; set; } = [];
    }

    private readonly string _appDirectory;
    private readonly string _gamePath;
    private readonly string _profilesRoot;
    private readonly string _storePath;
    private readonly string _statePath;
    private readonly string _journalPath;

    internal ModProfileStore(string appDirectory, string gamePath, string profilesRoot)
    {
        _appDirectory = appDirectory;
        _gamePath = CanonicalPath(gamePath);
        _profilesRoot = profilesRoot;
        var key = ProfileFiles.PathComparer == StringComparer.OrdinalIgnoreCase ? _gamePath.ToUpperInvariant() : _gamePath;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        _storePath = Path.Combine(_profilesRoot, Convert.ToHexString(hash));
        _statePath = Path.Combine(_storePath, "state.v2.json");
        _journalPath = Path.Combine(_storePath, "pending.v2.json");
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private FileStream Lock()
    {
        Directory.CreateDirectory(_storePath);
        return new FileStream(Path.Combine(_storePath, "state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal GameModProfiles Open(string? legacyGamePath)
    {
        using var fileLock = Lock();
        if (File.Exists(_statePath))
        {
            var existing = Load();
            PruneSnapshots(existing);
            return existing;
        }

        var legacyState = Path.Combine(_storePath, "state.json");
        GameModProfiles state;
        if (File.Exists(legacyState))
        {
            state = ProfileFiles.ReadJson<GameModProfiles>(legacyState);
            ProfileFileTransaction.Recover(_gamePath, Path.Combine(_storePath, "pending.json"), state.LastOperationId);
            state.LastOperationId = null;
            state.ManagedInstalls.Clear();
            foreach (var profile in state.Profiles)
                profile.LegacySnapshot = profile.SnapshotId.HasValue;
        }
        else
        {
            var legacyPath = Path.Combine(_appDirectory, "Data", "modprofiles.json");
            var useLegacy = File.Exists(legacyPath)
                && (string.IsNullOrWhiteSpace(legacyGamePath) || ProfileFiles.PathComparer.Equals(_gamePath, CanonicalPath(legacyGamePath)))
                && ClaimLegacyProfiles();
            var legacy = useLegacy ? ProfileFiles.ReadJson<LegacyProfiles>(legacyPath) : new();
            if (legacy.Profiles.Count == 0)
                legacy.Profiles.Add(new ModProfile { Name = "Default" });
            var active = legacy.Profiles.FirstOrDefault(profile => profile.Name == legacy.ActiveProfile) ?? legacy.Profiles[0];
            state = new GameModProfiles { Profiles = legacy.Profiles, ActiveProfileId = active.Id };
        }

        ProfileFiles.WriteJson(_statePath, state);
        return state;
    }

    private bool ClaimLegacyProfiles()
    {
        using var migrationLock = new FileStream(Path.Combine(_profilesRoot, "legacy.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var ownerFile = Path.Combine(_profilesRoot, "legacy-installation.json");
        if (File.Exists(ownerFile))
            return ProfileFiles.PathComparer.Equals(ProfileFiles.ReadJson<string>(ownerFile), _gamePath);

        ProfileFiles.WriteJson(ownerFile, _gamePath);
        return true;
    }

    private GameModProfiles Load(Guid? expectedProfile = null)
    {
        var state = ProfileFiles.ReadJson<GameModProfiles>(_statePath);
        ProfileFileTransaction.Recover(_gamePath, _journalPath, state.LastOperationId);
        if (expectedProfile.HasValue && expectedProfile != state.ActiveProfileId)
            throw new InvalidOperationException("The active profile changed in another window. Reload the game folder.");

        state.InstalledMods = LiveInstalls(_gamePath, state.InstalledMods);
        return state;
    }

    internal GameModProfiles Read()
    {
        using var fileLock = Lock();
        return Load();
    }

    internal FileStream LockForLaunch(Guid expectedProfile)
    {
        var fileLock = Lock();
        try
        {
            Load(expectedProfile);
            return fileLock;
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    private static List<ManagedInstall> LiveInstalls(string root, List<ManagedInstall> installs) =>
        installs.Where(install =>
        {
            try
            {
                return Matches(ProfileFiles.Resolve(root, install.RelativePath), install.Sha256);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }).ToList();

    private static bool Matches(string path, string sha256) =>
        File.Exists(path) && ModInstallerService.HashFile(path) == sha256;

    internal GameModProfiles Edit(Guid expectedProfile, Action<GameModProfiles> edit)
    {
        using var fileLock = Lock();
        var state = Load(expectedProfile);
        edit(state);
        foreach (var id in state.ManagedInstalls.Keys.Except(state.Profiles.Select(profile => profile.Id)).ToArray())
            state.ManagedInstalls.Remove(id);
        ProfileFiles.WriteJson(_statePath, state);
        PruneSnapshots(state);
        return state;
    }

    internal GameModProfiles Switch(Guid expectedProfile, Guid targetId, ModProfile? clone, bool deleteActive,
        IReadOnlyList<StagedMod> imported, IReadOnlyList<Guid> pendingIds, Action checkGameStopped)
    {
        using var fileLock = Lock();
        var state = Load(expectedProfile);
        var outgoing = state.Profiles.Single(profile => profile.Id == expectedProfile);
        var target = clone ?? state.Profiles.Single(profile => profile.Id == targetId);
        if (outgoing.Id == target.Id)
            return state;
        if (clone != null && state.Profiles.Any(profile => profile.Name == clone.Name))
            throw new InvalidOperationException("A profile with that name already exists.");
        if (!target.PendingSubmissionIds.SequenceEqual(pendingIds))
            throw new InvalidOperationException("The target profile changed in another window. Reload the game folder.");

        var generation = Guid.NewGuid();
        var snapshot = SnapshotPath(outgoing.Id, generation);
        try
        {
            Transact(state, transaction =>
            {
                checkGameStopped();
                foreach (var directory in ProfileFiles.ProfileDirectories)
                    ProfileFiles.CopyDirectory(ProfileFiles.Resolve(_gamePath, directory), Path.Combine(snapshot, directory));

                List<ManagedInstall> incoming;
                var empty = Path.Combine(transaction.WorkFolder, "empty");
                Directory.CreateDirectory(empty);
                if (target.SnapshotId is Guid saved)
                {
                    var folder = SnapshotPath(target.Id, saved, target.LegacySnapshot);
                    foreach (var directory in ProfileFiles.ProfileDirectories)
                    {
                        var incomingFolder = target.LegacySnapshot && directory == ProfileFiles.Plugins
                            ? Path.Combine(folder, "outside", directory)
                            : Path.Combine(folder, directory);
                        if (!Directory.Exists(incomingFolder) && (directory == ProfileFiles.Mods || File.Exists(incomingFolder)))
                            throw new IOException($"The saved {directory} folder is missing for {target.Name}.");
                        transaction.Add(directory, Directory.Exists(incomingFolder) ? incomingFolder : empty, isDirectory: true);
                    }
                    incoming = target.LegacySnapshot ? [] : LiveInstalls(folder, state.ManagedInstalls.GetValueOrDefault(target.Id) ?? []);
                }
                else
                {
                    var folder = Path.Combine(transaction.WorkFolder, "new-profile");
                    foreach (var directory in ProfileFiles.ProfileDirectories)
                        Directory.CreateDirectory(Path.Combine(folder, directory));
                    if (clone != null)
                    {
                        incoming = state.InstalledMods.ToList();
                        foreach (var install in incoming)
                            File.Copy(ProfileFiles.Resolve(snapshot, install.RelativePath), ProfileFiles.Resolve(folder, install.RelativePath));
                        state.Profiles.Add(clone);
                    }
                    else
                    {
                        incoming = PrepareInstalls(folder, [], imported, null, (path, source) =>
                            File.Copy(source, ProfileFiles.Resolve(folder, path)));
                    }
                    foreach (var directory in ProfileFiles.ProfileDirectories)
                        transaction.Add(directory, Path.Combine(folder, directory), isDirectory: true);
                }

                outgoing.SnapshotId = generation;
                outgoing.LegacySnapshot = false;
                target.PendingSubmissionIds = [];
                target.SnapshotId = null;
                target.LegacySnapshot = false;
                state.ActiveProfileId = target.Id;
                state.InstalledMods = incoming;
                if (deleteActive)
                {
                    state.Profiles.Remove(outgoing);
                    state.ManagedInstalls.Remove(outgoing.Id);
                }
                checkGameStopped();
            });
        }
        catch
        {
            ProfileFiles.TryDeleteDirectory(snapshot);
            throw;
        }

        PruneSnapshots(state);
        return state;
    }

    internal bool CanReuse(Guid expectedProfile, ManagedInstall install)
    {
        using var fileLock = Lock();
        return CheckDestination(_gamePath, Load(expectedProfile).InstalledMods, install);
    }

    internal GameModProfiles ChangeInstalledMods(Guid expectedProfile, IReadOnlyList<StagedMod> install, Guid? uninstall = null)
    {
        using var fileLock = Lock();
        var state = Load(expectedProfile);
        Transact(state, transaction =>
        {
            var previous = state.InstalledMods;
            var next = PrepareInstalls(_gamePath, previous, install, uninstall, (path, source) => transaction.Add(path, source));
            var remainingPaths = next.Select(item => item.RelativePath).ToHashSet(ProfileFiles.PathComparer);
            foreach (var existing in previous)
                if (!remainingPaths.Contains(existing.RelativePath) && Matches(ProfileFiles.Resolve(_gamePath, existing.RelativePath), existing.Sha256))
                    transaction.Add(existing.RelativePath, replacement: null);

            state.InstalledMods = next;
        });
        return state;
    }

    private static List<ManagedInstall> PrepareInstalls(string root, List<ManagedInstall> previous,
        IReadOnlyList<StagedMod> staged, Guid? uninstall, Action<string, string> addFile)
    {
        var replaced = staged.Select(package => package.Install.SubmissionUuid).ToHashSet();
        if (replaced.Count != staged.Count)
            throw new InvalidDataException("The install contains the same submission more than once.");
        if (uninstall is Guid id)
            replaced.Add(id);
        var next = previous.Where(install => !replaced.Contains(install.SubmissionUuid)).ToList();
        var paths = next.Select(install => install.RelativePath).ToHashSet(ProfileFiles.PathComparer);
        foreach (var package in staged)
        {
            var install = package.Install;
            if (!paths.Add(install.RelativePath))
                throw new IOException($"More than one submission uses {install.RelativePath}.");
            bool reuse;
            try
            {
                reuse = CheckDestination(root, previous, install);
            }
            catch (ExternalModConflictException conflict) when (
                package.ApprovedExternalHash != null && package.ApprovedExternalHash == conflict.Sha256)
            {
                reuse = false;
            }
            if (!reuse)
            {
                if (package.DownloadPath is not { } source)
                    throw new IOException($"{install.RelativePath} changed before it could be adopted. Retry the install.");
                addFile(install.RelativePath, source);
            }
            next.Add(install);
        }
        return next;
    }

    private static bool CheckDestination(string root, List<ManagedInstall> installed, ManagedInstall candidate)
    {
        var owner = installed.FirstOrDefault(item => ProfileFiles.PathComparer.Equals(item.RelativePath, candidate.RelativePath));
        if (owner != null && owner.SubmissionUuid != candidate.SubmissionUuid)
            throw new IOException($"{candidate.RelativePath} is installed by {owner.Name}.");

        var path = ProfileFiles.Resolve(root, candidate.RelativePath);
        if (!Path.Exists(path))
            return false;
        if (Matches(path, candidate.Sha256))
            return true;
        if (owner == null || !Matches(path, owner.Sha256))
        {
            if (File.Exists(path))
                throw new ExternalModConflictException(candidate.RelativePath, ModInstallerService.HashFile(path));
            throw new IOException($"An external folder is blocking {candidate.RelativePath}.");
        }
        return false;
    }

    private void Transact(GameModProfiles state, Action<ProfileFileTransaction> prepare)
    {
        var transaction = new ProfileFileTransaction(_gamePath, _journalPath);
        var previousOperation = state.LastOperationId;
        try
        {
            transaction.BeginStaging();
            prepare(transaction);
            transaction.Apply();
            state.LastOperationId = transaction.Id;
            ProfileFiles.WriteJson(_statePath, state);
        }
        catch (Exception failure)
        {
            try
            {
                ProfileFileTransaction.Recover(_gamePath, _journalPath, previousOperation);
                ProfileFiles.TryDeleteDirectory(transaction.WorkFolder);
            }
            catch (Exception recoveryFailure)
            {
                throw new IOException($"{failure.Message} Recovery could not finish. {recoveryFailure.Message}", recoveryFailure);
            }
            throw;
        }

        try
        {
            ProfileFileTransaction.Recover(_gamePath, _journalPath, state.LastOperationId);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string SnapshotPath(Guid profile, Guid generation, bool legacy = false) =>
        Path.Combine(_storePath, legacy ? "snapshots" : "snapshots.v2", profile.ToString("N"), generation.ToString("N"));

    private void PruneSnapshots(GameModProfiles state)
    {
        var root = Path.Combine(_storePath, "snapshots.v2");
        if (!Directory.Exists(root))
            return;

        var saved = state.Profiles.Where(profile => profile.SnapshotId.HasValue && !profile.LegacySnapshot)
            .Select(profile => SnapshotPath(profile.Id, profile.SnapshotId!.Value)).ToHashSet(ProfileFiles.PathComparer);
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(profile), "N", out _))
                    continue;
                foreach (var snapshot in Directory.EnumerateDirectories(profile))
                {
                    if (Guid.TryParseExact(Path.GetFileName(snapshot), "N", out _) && !saved.Contains(snapshot))
                        ProfileFiles.TryDeleteDirectory(snapshot);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
