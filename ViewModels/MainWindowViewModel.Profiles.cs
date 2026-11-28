using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly FilePickerFileType PackFileType = new("Stellar Profile Pack")
    {
        Patterns = ["*" + ProfilePack.Extension]
    };

    private ModProfileStore? _profileStore;
    private ModProfile? _profilePendingDeletion;
    private TaskCompletionSource<bool>? _profileImportConfirmation;

    public ObservableCollection<ModProfile> ModProfiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProfileName), nameof(LibraryShortcutText))]
    public partial ModProfile? ActiveProfile { get; set; }

    public string ActiveProfileName => ActiveProfile?.Name ?? "Profile";
    public string LibraryShortcutText => LocalizationService.Instance.Format("ProfileLibraryCount", ActiveProfileName, LibraryMods.Count);

    [ObservableProperty]
    public partial bool IsProfilesDropdownOpen { get; set; }

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreModActionsBlocked))]
    public partial bool IsModOperationInProgress { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmProfileImportOpen { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<InstalledModInfo> ProfileImportExternalMods { get; set; } = [];

    private void OpenGameProfiles(string gamePath, string? legacyGamePath)
    {
        try
        {
            var store = new ModProfileStore(AppContext.BaseDirectory, gamePath, AppPaths.ProfilesRoot);
            var state = store.Open(legacyGamePath);
            _profileStore = store;
            ShowProfileState(state);
        }
        catch (Exception ex)
        {
            ClearGameProfiles();
            ReportError("StatusProfileOpenFailed", ex.Message);
        }
    }

    private void ClearGameProfiles()
    {
        IsProfilesDropdownOpen = false;
        CloseModDetails();
        _profileStore = null;
        ActiveProfile = null;
        ModProfiles.Clear();
        ShowInstalledMods([]);
    }

    private void ShowProfileState(GameModProfiles state)
    {
        ModProfiles.Clear();
        foreach (var profile in state.Profiles)
        {
            profile.IsActive = profile.Id == state.ActiveProfileId;
            ModProfiles.Add(profile);
        }

        ActiveProfile = ModProfiles.Single(p => p.IsActive);
        ShowInstalledMods(state.InstalledMods);
    }

    private bool TryBeginModOperation(bool requireProfile = true)
    {
        if (IsModOperationInProgress || IsInstallingMelonLoader || IsConfirmRemoveOpen || IsConfirmDependenciesOpen)
        {
            ReportError("StatusModOperationBusy");
            return false;
        }

        if (requireProfile && (_profileStore == null || ActiveProfile == null))
        {
            ReportError("StatusSelectGameFirst");
            return false;
        }

        IsModOperationInProgress = true;
        return true;
    }

    [RelayCommand]
    private void ToggleProfilesDropdown() => IsProfilesDropdownOpen = !IsProfilesDropdownOpen;

    [RelayCommand]
    private Task SwitchProfile(ModProfile? profile)
    {
        IsProfilesDropdownOpen = false;
        if (profile == null || profile.Id == ActiveProfile?.Id)
            return Task.CompletedTask;
        return ApplyProfileSwitch(profile.Id);
    }

    [RelayCommand]
    private async Task CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0)
            return;
        if (ModProfiles.Any(p => p.Name == name))
        {
            ReportError("StatusProfileNameTaken");
            return;
        }

        IsProfilesDropdownOpen = false;
        if (await ApplyProfileSwitch(Guid.Empty, clone: new ModProfile { Name = name }))
            NewProfileName = "";
    }

    [RelayCommand]
    private async Task DeleteProfile(ModProfile? profile)
    {
        if (profile == null || ModProfiles.Count <= 1 || IsModOperationInProgress ||
            IsConfirmRemoveOpen || IsConfirmDependenciesOpen)
            return;

        if (profile.Id == ActiveProfile?.Id || profile.SnapshotId.HasValue)
        {
            IsProfilesDropdownOpen = false;
            _profilePendingDeletion = profile;
            ShowRemoveConfirmation("ProfileDeleteTitle", "ProfileDelete", "ProfileDeleteMessage", profile.Name);
            return;
        }
        await DeleteProfileAsync(profile);
    }

    private Task DeleteProfileAsync(ModProfile profile)
    {
        if (ModProfiles.Count <= 1)
            return Task.CompletedTask;
        if (profile.Id == ActiveProfile?.Id)
            return ApplyProfileSwitch(ModProfiles.First(p => p.Id != profile.Id).Id, deleteActive: true);

        return EditProfiles(state => state.Profiles.RemoveAll(p => p.Id == profile.Id), "StatusProfileDeleteFailed");
    }

    private async Task<bool> ApplyProfileSwitch(Guid target, ModProfile? clone = null, bool deleteActive = false)
    {
        if (IsGameRunning)
        {
            ReportError("StatusProfileGameRunning");
            return false;
        }

        if (!TryBeginModOperation())
            return false;
        try
        {
            var store = _profileStore!;
            var active = ActiveProfile!.Id;
            var gamePath = GamePath;
            var pending = ModProfiles.FirstOrDefault(profile => profile.Id == target)?.PendingSubmissionIds.ToArray() ?? [];
            using var installer = new ModInstallerService();
            var staged = new List<StagedMod>();
            if (pending.Length > 0)
            {
                var missing = new List<string>();
                var wanted = new Dictionary<string, OnlineModInfo>();
                foreach (var id in pending)
                {
                    var root = await _repositoryService.GetSubmissionAsync(id);
                    if (root is not { CanInstall: true })
                    {
                        missing.Add(id.ToString());
                        continue;
                    }
                    RegisterOnlineMod(root);
                    var (dependencies, unavailable) = await _repositoryService.ResolveInstallAsync(root, []);
                    missing.AddRange(unavailable);
                    foreach (var online in dependencies.Append(root))
                    {
                        RegisterOnlineMod(online);
                        wanted.TryAdd(online.Id, online);
                    }
                }

                if (missing.Count > 0)
                {
                    ReportError("StatusProfileMissingPackages", string.Join(", ", missing.Distinct()
                        .Select(DisplayNameFor)));
                    return false;
                }
                foreach (var mod in wanted.Values)
                    staged.Add(await installer.StageAsync(ModInstallerService.Describe(mod), _repositoryService, null));
            }
            var state = await Task.Run(() => store.Switch(active, target, clone, deleteActive, staged, pending, CheckGameStopped));
            ShowProfileState(state);
            ReportSuccess("StatusProfileSwitched", ActiveProfileName);
            return true;

            void CheckGameStopped()
            {
                var executable = GameLocatorService.FindExecutable(gamePath)
                    ?? throw new InvalidOperationException(LocalizationService.Instance["StatusGameNotFound"]);
                using var running = GameLaunchService.DetectExisting(executable);
                if (running != null)
                    throw new InvalidOperationException(LocalizationService.Instance["StatusProfileGameRunning"]);
            }
        }
        catch (Exception ex)
        {
            ReportError("StatusProfileSwitchFailed", ex.Message);
            return false;
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    [RelayCommand]
    private void StartRename(ModProfile? profile)
    {
        if (profile == null || IsModOperationInProgress)
            return;
        profile.EditName = profile.Name;
        profile.IsEditing = true;
    }

    [RelayCommand]
    private async Task CommitRename(ModProfile? profile)
    {
        if (profile == null)
            return;
        var name = profile.EditName.Trim();
        profile.IsEditing = false;
        if (name.Length == 0 || name == profile.Name)
            return;

        await EditProfiles(state =>
        {
            EnsureUniqueProfileName(state, name, profile.Id);
            state.Profiles.Single(p => p.Id == profile.Id).Name = name;
        }, "StatusProfileRenameFailed");
    }

    [RelayCommand]
    private static void CancelRename(ModProfile? profile)
    {
        profile?.IsEditing = false;
    }

    private async Task EditProfiles(Action<GameModProfiles> edit, string errorKey)
    {
        if (!TryBeginModOperation())
            return;
        try
        {
            var store = _profileStore!;
            var active = ActiveProfile!.Id;
            ShowProfileState(await Task.Run(() => store.Edit(active, edit)));
        }
        catch (Exception ex)
        {
            ReportError(errorKey, ex.Message);
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    private static void EnsureUniqueProfileName(GameModProfiles state, string name, Guid? except = null)
    {
        if (state.Profiles.Any(p => p.Name == name && p.Id != except))
            throw new InvalidOperationException(LocalizationService.Instance["StatusProfileNameTaken"]);
    }

    private static Window? GetMainWindow() =>
        (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    [RelayCommand]
    private async Task ExportProfile()
    {
        if (ActiveProfile is not { } profile || GetMainWindow() is not Window window || !TryBeginModOperation())
            return;

        IsProfilesDropdownOpen = false;
        try
        {
            var store = _profileStore!;
            using var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["ProfileExport"],
                SuggestedFileName = profile.Name + ProfilePack.Extension,
                DefaultExtension = ProfilePack.Extension[1..],
                FileTypeChoices = [PackFileType],
                ShowOverwritePrompt = true
            });

            if (file == null)
                return;
            RequirePackExtension(file.Name);

            var temporary = Path.Combine(Path.GetTempPath(), $"stellar-pack-{Guid.NewGuid():N}.sdpack");
            await using var archive = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await Task.Run(() => store.ExportPack(profile.Id, archive));
            archive.Position = 0;

            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await archive.CopyToAsync(stream);
            await stream.FlushAsync();
            ReportSuccess("StatusProfileExported", profile.Name);
        }
        catch (Exception ex)
        {
            ReportError("StatusProfileExportFailed", ex.Message);
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task ImportProfile()
    {
        if (GetMainWindow() is not Window window || !TryBeginModOperation())
            return;

        DirectoryInfo? workspace = null;
        IsProfilesDropdownOpen = false;
        try
        {
            var store = _profileStore!;
            var active = ActiveProfile!.Id;
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["ProfileImport"],
                AllowMultiple = false,
                FileTypeFilter = [PackFileType]
            });

            if (files.Count == 0)
                return;

            using var file = files[0];
            RequirePackExtension(file.Name);
            workspace = Directory.CreateTempSubdirectory("stellar-pack-");
            var workspacePath = workspace.FullName;
            var externalRoot = Path.Combine(workspacePath, "external");
            ImportedProfilePack pack;
            await using (var stream = await file.OpenReadAsync())
                pack = await Task.Run(() => ProfilePack.Read(stream, workspacePath, externalRoot));

            if (ModProfiles.Any(profile => profile.Name == pack.Name))
            {
                ReportError("StatusProfileNameTaken");
                return;
            }

            if (pack.ExternalMods.Count > 0 && !await ConfirmExternalModsAsync(pack.ExternalMods))
                return;

            var installs = new List<ManagedInstall>();
            var missing = new List<string>();
            foreach (var release in pack.ManagedReleases)
            {
                var online = await _repositoryService.GetReleaseAsync(release.SubmissionUuid, release.ReleaseUuid, default);
                if (online is { CanInstall: true })
                    installs.Add(ModInstallerService.Describe(online));
                else
                    missing.Add(release.Code);
            }

            if (missing.Count > 0)
            {
                ReportError("StatusProfileImportMissingReleases", string.Join(", ", missing));
                return;
            }

            using var installer = new ModInstallerService();
            var staged = new List<StagedMod>();
            foreach (var install in installs)
                staged.Add(await installer.StageAsync(install, _repositoryService, null));

            var state = await Task.Run(() => store.ImportPack(active, pack, externalRoot, staged));
            ShowProfileState(state);
            ReportSuccess("StatusProfileImported", pack.Name);
        }
        catch (Exception ex)
        {
            ReportError("StatusProfileImportFailed", ex.Message);
        }
        finally
        {
            if (workspace != null)
                ProfileFiles.TryDeleteDirectory(workspace.FullName);
            IsModOperationInProgress = false;
        }
    }

    private static void RequirePackExtension(string fileName)
    {
        if (!fileName.EndsWith(ProfilePack.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(LocalizationService.Instance["StatusProfilePackExtension"]);
    }

    private async Task<bool> ConfirmExternalModsAsync(IReadOnlyList<InstalledModInfo> mods)
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _profileImportConfirmation = confirmation;
        ProfileImportExternalMods = mods;
        IsConfirmProfileImportOpen = true;
        try
        {
            return await confirmation.Task;
        }
        finally
        {
            IsConfirmProfileImportOpen = false;
            ProfileImportExternalMods = [];
            _profileImportConfirmation = null;
        }
    }

    [RelayCommand]
    private void ConfirmProfileImport() => _profileImportConfirmation?.TrySetResult(true);

    [RelayCommand]
    private void CancelProfileImport() => _profileImportConfirmation?.TrySetResult(false);
}
