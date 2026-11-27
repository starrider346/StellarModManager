using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _directInstallLookup;

    [ObservableProperty] public partial bool IsDirectInstallOpen { get; private set; }
    [ObservableProperty] public partial string? DirectInstallCode { get; set; }
    [ObservableProperty] public partial string? DirectInstallStatus { get; private set; }
    [ObservableProperty] public partial string? DirectInstallReleaseSummary { get; private set; }
    [ObservableProperty] public partial string? DirectInstallProfileSummary { get; private set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallDirectReleaseCommand))]
    [NotifyPropertyChangedFor(nameof(DirectInstallActionText))]
    public partial OnlineModInfo? DirectInstallRelease { get; private set; }

    public string DirectInstallActionText => DirectInstallRelease?.InstallButtonText ?? LocalizationService.Instance["Install"];

    [RelayCommand]
    private void OpenDirectInstall()
    {
        if (AreModActionsBlocked || SetupRequired)
            return;

        if (_profileStore == null || ActiveProfile == null)
        {
            ReportError("StatusSelectGameFirst");
            return;
        }

        IsProfilesDropdownOpen = false;
        DirectInstallProfileSummary = LocalizationService.Instance.Format("DirectInstallProfile", ActiveProfileName);
        IsDirectInstallOpen = true;
    }

    [RelayCommand]
    private void CloseDirectInstall()
    {
        IsDirectInstallOpen = false;
        DirectInstallCode = null;
    }

    partial void OnDirectInstallCodeChanged(string? value)
    {
        var previous = _directInstallLookup;
        _directInstallLookup = null;
        previous?.Cancel();
        DirectInstallRelease = null;
        DirectInstallReleaseSummary = null;
        DirectInstallStatus = null;

        if (!IsDirectInstallOpen || string.IsNullOrWhiteSpace(value))
            return;

        var parts = value.Split(':', 2);
        var loc = LocalizationService.Instance;
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0].Trim(), "D", out var submissionUuid) ||
            !Guid.TryParseExact(parts[1].Trim(), "D", out var releaseUuid) || submissionUuid == Guid.Empty || releaseUuid == Guid.Empty)
        {
            DirectInstallStatus = loc["DirectInstallInvalidCode"];
            return;
        }

        DirectInstallStatus = loc["DirectInstallLookingUp"];
        var lookup = new CancellationTokenSource();
        _directInstallLookup = lookup;
        _ = LoadDirectInstallReleaseAsync(submissionUuid, releaseUuid, lookup);
    }

    private async Task LoadDirectInstallReleaseAsync(Guid submissionUuid, Guid releaseUuid, CancellationTokenSource lookup)
    {
        var loc = LocalizationService.Instance;
        try
        {
            await Task.Delay(250, lookup.Token);
            lookup.CancelAfter(TimeSpan.FromSeconds(30));
            var mod = await _repositoryService.GetReleaseAsync(submissionUuid, releaseUuid, lookup.Token);
            if (_directInstallLookup != lookup)
                return;
            lookup.Token.ThrowIfCancellationRequested();

            if (mod == null)
            {
                DirectInstallStatus = loc["DirectInstallNotFound"];
                return;
            }

            var installed = InstalledMods.FirstOrDefault(item => SameId(item.Id, mod.Id));
            mod.IsInstalled = installed?.ReleaseUuid == releaseUuid &&
                string.Equals(installed.Sha256, mod.Release!.Sha256, StringComparison.OrdinalIgnoreCase);
            var type = loc[mod.Type == SubmissionType.Plugin ? "DirectInstallPlugin" : "DirectInstallMod"];
            DirectInstallReleaseSummary = loc.Format("DirectInstallReleaseSummary", type, mod.Version, mod.Author);
            DirectInstallStatus = !mod.CanInstall ? loc["DirectInstallUnavailable"] :
                mod.IsInstalled ? loc["DirectInstallAlreadyInstalled"] :
                installed != null ? loc.Format("DirectInstallReplacesVersion", installed.Version) : null;
            DirectInstallRelease = mod;
        }
        catch (Exception ex)
        {
            if (_directInstallLookup == lookup)
            {
                Trace.WriteLine($"Direct install lookup failed: {ex.Message}");
                DirectInstallStatus = loc["DirectInstallLookupFailed"];
            }
        }
        finally
        {
            if (_directInstallLookup == lookup)
                _directInstallLookup = null;
            lookup.Dispose();
        }
    }

    private bool CanInstallDirectRelease() => DirectInstallRelease is { CanInstall: true };

    [RelayCommand(CanExecute = nameof(CanInstallDirectRelease))]
    private async Task InstallDirectRelease()
    {
        if (!CanInstallDirectRelease() || AreModActionsBlocked)
            return;

        var release = DirectInstallRelease!;
        CloseDirectInstall();
        await InstallModAsync(release, dependenciesConfirmed: false);
    }
}
