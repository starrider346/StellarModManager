using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<InstalledModInfo> InstalledMods { get; } = [];
    public ObservableCollection<InstalledModInfo> LibraryMods { get; } = [];
    public ObservableCollection<InstalledModInfo> AvailableModUpdates { get; } = [];
    private HashSet<string> _managedModPaths = new(ProfileFiles.PathComparer);

    private CancellationTokenSource? _externalIdentification;
    private InstalledModInfo? _modPendingRemoval;
    private OnlineModInfo? _modPendingInstall;
    private TaskCompletionSource<bool>? _replacementConfirmation;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AreModActionsBlocked))] public partial bool IsConfirmReplacementOpen { get; set; }
    [ObservableProperty] public partial string ConfirmReplacementMessage { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AreModActionsBlocked))] public partial bool IsConfirmRemoveOpen { get; set; }
    [ObservableProperty] public partial string ConfirmRemoveTitle { get; set; } = "";
    [ObservableProperty] public partial string ConfirmRemoveButton { get; set; } = "";
    [ObservableProperty] public partial string ConfirmRemoveMessage { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AreModActionsBlocked))] public partial bool IsConfirmDependenciesOpen { get; set; }
    [ObservableProperty] public partial string ConfirmDependenciesMessage { get; set; } = "";

    public bool AreModActionsBlocked => IsModOperationInProgress || IsConfirmReplacementOpen || IsConfirmRemoveOpen || IsConfirmDependenciesOpen;

    private void ShowInstalledMods(List<ManagedInstall> mods)
    {
        var updatesId = UpdatesSelection?.Id;
        _managedModPaths = mods.Select(mod => mod.RelativePath).ToHashSet(ProfileFiles.PathComparer);
        InstalledMods.Clear();
        foreach (var installed in mods)
            InstalledMods.Add(installed.ToModInfo());
        ShowLibraryMods();
        RefreshUpdateStatuses(updatesId);
        _ = RefreshInstalledCatalogAsync();
    }

    [RelayCommand]
    private void RefreshLibrary()
    {
        if (IsModOperationInProgress || _profileStore == null)
            return;
        try
        {
            ShowProfileState(_profileStore.Read());
        }
        catch (Exception ex)
        {
            ReportError("StatusProfileOpenFailed", ex.Message);
        }
    }

    private void ShowLibraryMods()
    {
        var selection = LibrarySelection;
        var external = _profileStore == null ? [] : ExternalModScanner.Scan(GamePath, _managedModPaths);
        LibraryMods.Clear();
        foreach (var mod in InstalledMods.Concat(external))
            LibraryMods.Add(mod);

        // Hmm...
        LibrarySelection = selection == null ? null : LibraryMods.FirstOrDefault(mod =>
            mod.IsExternal == selection.IsExternal && (mod.IsExternal
                ? ProfileFiles.PathComparer.Equals(mod.Id, selection.Id)
                : SameId(mod.Id, selection.Id)));

        _externalIdentification?.Cancel();
        _externalIdentification?.Dispose();
        _externalIdentification = new CancellationTokenSource();
        _ = IdentifyExternalModsAsync(external, GamePath, _externalIdentification.Token);
    }

    private async Task IdentifyExternalModsAsync(IEnumerable<InstalledModInfo> mods, string gamePath, CancellationToken cancellationToken)
    {
        foreach (var mod in mods)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            try
            {
                var path = ProfileFiles.Resolve(gamePath, mod.ExternalPath!);
                var hash = await Task.Run(() => ModInstallerService.HashFile(path), cancellationToken);
                var match = await _repositoryService.IdentifyAsync(hash, cancellationToken);
                if (!cancellationToken.IsCancellationRequested && match is { } recognized)
                    mod.CatalogMatch = $"{recognized.Submission.Name} ({recognized.Release.Version})";
            }
            catch (OperationCanceledException) { return; }
            catch (HttpRequestException) { return; }
            catch (JsonException) { return; }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    private static bool SameId(string id, string? other) =>
        string.Equals(id, other, StringComparison.OrdinalIgnoreCase);

    private void RefreshUpdateStatuses(string? previousSelection = null)
    {
        var updatesId = previousSelection ?? UpdatesSelection?.Id;
        foreach (var online in OnlineMods)
        {
            online.IsInstalled = InstalledMods.Any(m => SameId(m.Id, online.Id));
            online.HasUpdate = false;
            RefreshReleaseInstallStates(online);
        }

        foreach (var installed in InstalledMods)
        {
            installed.Online = OnlineMods.FirstOrDefault(m => SameId(m.Id, installed.Id));
            var online = installed.Online;
            var latest = online?.Release;
            installed.IsUpdateAvailable = latest is { IsInstallable: true } &&
                (latest.Uuid.CompareTo(installed.ReleaseUuid) > 0 ||
                 latest.Uuid == installed.ReleaseUuid && !string.Equals(latest.Sha256, installed.Sha256, StringComparison.OrdinalIgnoreCase));
            installed.LatestVersion = installed.IsUpdateAvailable ? latest?.Version : null;
            installed.UpdateNotes = null;
            if (online != null && installed.IsUpdateAvailable)
            {
                online.HasUpdate = true;
                _ = LoadUpdateNotesAsync(installed, online);
            }
        }

        RefreshDependencyStatuses();
        AvailableModUpdates.Clear();
        foreach (var installed in InstalledMods.Where(m => m.IsUpdateAvailable))
            AvailableModUpdates.Add(installed);
        UpdatesSelection = AvailableModUpdates.FirstOrDefault(m => SameId(m.Id, updatesId));
        ShowDetails();
    }

    private async Task LoadUpdateNotesAsync(InstalledModInfo installed, OnlineModInfo online)
    {
        await _repositoryService.LoadChangelogAsync(online);
        RefreshReleaseInstallStates(online);
        installed.UpdateNotes = string.Join("\n\n", online.Changelog
            .Where(entry => entry.ReleaseId.CompareTo(installed.ReleaseUuid) > 0 ||
                entry.ReleaseId == installed.ReleaseUuid && online.Release?.Uuid == installed.ReleaseUuid)
            .Select(entry => $"{entry.Version}\n{entry.Notes}"));
    }

    private void RefreshDependencyStatuses()
    {
        var loc = LocalizationService.Instance;
        foreach (var installed in InstalledMods)
        {
            var (_, missing) = DependencyResolver.ResolveInstall(installed, InstalledMods, _ => null);
            var lines = missing.Select(id => loc.Format("DependencyMissingLine", DisplayNameFor(id)));
            installed.DependencyWarning = string.Join("\n", lines);
        }
    }

    private string DisplayNameFor(string id) => OnlineMods.FirstOrDefault(m => SameId(m.Id, id))?.Name ?? id;

    [RelayCommand]
    private Task InstallMod(OnlineModInfo mod) => InstallModAsync(mod, dependenciesConfirmed: false);

    private async Task InstallModAsync(OnlineModInfo mod, bool dependenciesConfirmed)
    {
        if (!mod.CanInstall || !TryBeginModOperation())
            return;
        try
        {
            var (dependencies, missing) = await _repositoryService.ResolveInstallAsync(mod, InstalledMods);
            foreach (var dependency in dependencies)
                RegisterOnlineMod(dependency);
            if (missing.Count > 0)
            {
                ReportError("StatusDependencyNotInList", mod.Name, string.Join(", ", missing.Select(DisplayNameFor)));
                return;
            }

            var names = string.Join(", ", dependencies.Select(d => d.Name));
            if (dependencies.Count > 0 && !AutoDeployDependencies && !dependenciesConfirmed)
            {
                _modPendingInstall = mod;
                ConfirmDependenciesMessage = LocalizationService.Instance.Format("ConfirmDependenciesMessage", mod.Name, names);
                IsConfirmDependenciesOpen = true;
                return;
            }

            await DeployModsAsync(dependencies.Cast<OnlineModInfo>().Append(mod));
            if (dependencies.Count == 0)
                ReportSuccess("StatusInstalled", mod.Name);
            else
                ReportSuccess("StatusInstalledWithDependencies", mod.Name, names);
        }
        catch (ExternalModReplacementCancelledException) { }
        catch (Exception ex)
        {
            ReportError("StatusInstallFailed", ex.Message);
        }
        finally
        {
            mod.IsInstalling = false;
            IsModOperationInProgress = false;
        }
    }

    private async Task DeployModsAsync(IEnumerable<OnlineModInfo> mods, IProgress<double>? updateProgress = null)
    {
        var store = _profileStore!;
        var active = ActiveProfile!.Id;
        using var installer = new ModInstallerService();
        var staged = new List<StagedMod>();
        foreach (var mod in mods)
        {
            var install = ModInstallerService.Describe(mod);
            string? approvedExternalHash = null;
            try
            {
                if (await Task.Run(() => store.CanReuse(active, install)))
                {
                    staged.Add(new StagedMod(install, null));
                    continue;
                }
            }
            catch (ExternalModConflictException conflict)
            {
                ConfirmReplacementMessage = LocalizationService.Instance.Format(
                    "ConfirmReplacementMessage", conflict.RelativePath, mod.Name);
                _replacementConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                IsConfirmReplacementOpen = true;
                try
                {
                    if (!await _replacementConfirmation.Task)
                        throw new ExternalModReplacementCancelledException();
                    approvedExternalHash = conflict.Sha256;
                }
                finally
                {
                    IsConfirmReplacementOpen = false;
                    _replacementConfirmation = null;
                }
            }

            mod.IsInstalling = true;
            mod.DownloadProgress = 0;
            try
            {
                var progress = new Progress<double>(pct =>
                {
                    mod.DownloadProgress = pct;
                    updateProgress?.Report(pct);
                });
                var package = await installer.StageAsync(install, _repositoryService, progress);
                staged.Add(package with { ApprovedExternalHash = approvedExternalHash });
            }
            finally
            {
                mod.IsInstalling = false;
            }
        }
        ShowProfileState(await Task.Run(() => store.ChangeInstalledMods(active, staged)));
    }

    private sealed class ExternalModReplacementCancelledException : OperationCanceledException { }

    [RelayCommand]
    private void ConfirmReplacement() => _replacementConfirmation?.TrySetResult(true);

    [RelayCommand]
    private void CancelReplacement() => _replacementConfirmation?.TrySetResult(false);

    [RelayCommand]
    private async Task ConfirmDependencies()
    {
        var mod = _modPendingInstall;
        CancelDependencies();
        if (mod != null)
            await InstallModAsync(mod, dependenciesConfirmed: true);
    }

    [RelayCommand]
    private void CancelDependencies()
    {
        IsConfirmDependenciesOpen = false;
        _modPendingInstall = null;
    }

    [RelayCommand]
    private async Task RequestRemoveMod(InstalledModInfo mod)
    {
        if (mod.IsExternal || IsModOperationInProgress || IsConfirmRemoveOpen || IsConfirmDependenciesOpen)
            return;

        var dependents = DependencyResolver.FindDependents(mod, InstalledMods);
        if (dependents.Count > 0)
        {
            var names = string.Join(", ", dependents.Select(d => d.Name));
            _modPendingRemoval = mod;
            ShowRemoveConfirmation("ConfirmRemoveTitle", "ConfirmRemoveButton", "ConfirmRemoveDependentsMessage", names, mod.Name);
        }
        else if (ConfirmBeforeRemove)
        {
            _modPendingRemoval = mod;
            ShowRemoveConfirmation("ConfirmRemoveTitle", "ConfirmRemoveButton", "ConfirmRemoveMessage", mod.Name);
        }
        else
        {
            await UninstallMod(mod);
        }
    }

    private void ShowRemoveConfirmation(string title, string button, string message, params object[] args)
    {
        var loc = LocalizationService.Instance;
        ConfirmRemoveTitle = loc[title];
        ConfirmRemoveButton = loc[button];
        ConfirmRemoveMessage = loc.Format(message, args);
        IsConfirmRemoveOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmRemove()
    {
        var profile = _profilePendingDeletion;
        var mod = _modPendingRemoval;
        var discussionComment = _discussionCommentPendingDeletion;
        CancelRemove();
        if (discussionComment is { } comment && CanDeleteDiscussionComment(comment.Author))
            await DeleteDiscussionCommentAsync(comment.Comment, comment.Parent);
        else if (profile != null)
            await DeleteProfileAsync(profile);
        else if (mod != null)
            await UninstallMod(mod);
    }

    [RelayCommand]
    private void CancelRemove()
    {
        IsConfirmRemoveOpen = false;
        _modPendingRemoval = null;
        _profilePendingDeletion = null;
        _discussionCommentPendingDeletion = null;
    }

    private async Task UninstallMod(InstalledModInfo mod)
    {
        if (!TryBeginModOperation())
            return;
        mod.IsRemoving = true;
        try
        {
            var store = _profileStore!;
            var active = ActiveProfile!.Id;
            ShowProfileState(await Task.Run(() => store.ChangeInstalledMods(active, [], Guid.Parse(mod.Id))));
            ReportSuccess("StatusRemoved", mod.Name);
        }
        catch (Exception ex)
        {
            ReportError("StatusRemoveFailed", ex.Message);
        }
        finally
        {
            mod.IsRemoving = false;
            IsModOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task UpdateMod(InstalledModInfo mod)
    {
        if (!TryBeginModOperation())
            return;
        try
        {
            await UpdateInstalledMod(mod);
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    private async Task UpdateInstalledMod(InstalledModInfo mod)
    {
        var version = mod.LatestVersion;
        if (!mod.IsUpdateAvailable || version == null)
            return;

        mod.IsUpdating = true;
        mod.UpdateProgress = 0;
        try
        {
            if (await _repositoryService.GetSubmissionAsync(Guid.Parse(mod.Id)) is not { CanInstall: true } online)
            {
                ReportError("StatusNotInRepository");
                return;
            }

            RegisterOnlineMod(online);
            var (dependencies, missing) = await _repositoryService.ResolveInstallAsync(online, InstalledMods);
            foreach (var dependency in dependencies)
                RegisterOnlineMod(dependency);
            if (missing.Count > 0)
            {
                ReportError("StatusDependencyNotInList", mod.Name, string.Join(", ", missing.Select(DisplayNameFor)));
                return;
            }

            var progress = new Progress<double>(pct => mod.UpdateProgress = pct);
            await DeployModsAsync(dependencies.Cast<OnlineModInfo>().Append(online), progress);
            ReportSuccess("StatusUpdated", mod.Name, version);
        }
        catch (ExternalModReplacementCancelledException) { }
        catch (Exception ex)
        {
            ReportError("StatusUpdateFailed", ex.Message);
        }
        finally
        {
            mod.IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllMods()
    {
        if (!TryBeginModOperation())
            return;
        try
        {
            foreach (var id in AvailableModUpdates.Select(m => m.Id).ToList())
            {
                var mod = InstalledMods.FirstOrDefault(m => SameId(m.Id, id));
                if (mod != null)
                    await UpdateInstalledMod(mod);
            }
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }
}
