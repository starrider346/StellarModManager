using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    private ModInfo? _detailsItem;
    private bool _detailsRefreshPending;
    private string? _currentGameVersion;
    private Task? _gameVersionRequest;

    private async Task RefreshGameVersionAsync()
    {
        var request = _gameVersionRequest ??= FetchGameVersionAsync();
        try { await request; }
        finally
        {
            if (ReferenceEquals(_gameVersionRequest, request))
                _gameVersionRequest = null;
        }
    }

    private async Task FetchGameVersionAsync()
    {
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            _currentGameVersion = await _repositoryService.GetCurrentGameVersionAsync(timeout.Token);
            foreach (var mod in OnlineMods)
            {
                mod.CurrentGameVersion = _currentGameVersion;
                foreach (var entry in mod.Changelog)
                    if (entry.Installation is { } installation)
                        installation.CurrentGameVersion = _currentGameVersion;
            }
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or
            OperationCanceledException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine($"Game version refresh failed: {exception.Message}");
        }
    }

    [ObservableProperty]
    public partial bool IsModDetailsOpen { get; private set; }

    [ObservableProperty]
    public partial OnlineModInfo? BrowseSelection { get; set; }

    [ObservableProperty]
    public partial InstalledModInfo? LibrarySelection { get; set; }

    [ObservableProperty]
    public partial InstalledModInfo? UpdatesSelection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsMod), nameof(DetailsDescription), nameof(HasSelection), nameof(CanInstallSelected))]
    public partial OnlineModInfo? SelectedMod { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsMod), nameof(DetailsDescription), nameof(HasSelection), nameof(CanInstallSelected), nameof(IsSelectedInstalled))]
    public partial InstalledModInfo? SelectedInstalledMod { get; set; }

    public ModInfo? DetailsMod => (ModInfo?)SelectedInstalledMod ?? SelectedMod;
    public string? DetailsDescription => SelectedMod?.Description ?? DetailsMod?.Description;

    public bool HasSelection => DetailsMod != null;

    public bool CanInstallSelected => SelectedMod != null && SelectedInstalledMod == null;

    public bool IsSelectedInstalled => SelectedInstalledMod is { IsExternal: false };

    partial void OnBrowseSelectionChanged(OnlineModInfo? value) => ShowDetails();

    partial void OnLibrarySelectionChanged(InstalledModInfo? value) => ShowDetails();

    partial void OnUpdatesSelectionChanged(InstalledModInfo? value) => ShowDetails();

    partial void OnSelectedModChanged(OnlineModInfo? value)
    {
        SelectDiscussion(value);
        if (value == null)
            return;

        _ = _repositoryService.LoadThumbnailAsync(value);
        _ = _repositoryService.LoadGalleryAsync(value);
        _ = LoadDetailsReleasesAsync(value);
    }

    private async Task LoadDetailsReleasesAsync(OnlineModInfo mod)
    {
        await RefreshGameVersionAsync();
        await _repositoryService.LoadChangelogAsync(mod);
        RefreshReleaseInstallStates(mod);
    }

    private void RefreshReleaseInstallStates(OnlineModInfo mod)
    {
        var installed = InstalledMods.FirstOrDefault(item => SameId(item.Id, mod.Id));
        foreach (var entry in mod.Changelog)
        {
            if (entry.Installation is { } installation)
                installation.IsInstalled = installed != null && installed.ReleaseUuid == entry.ReleaseId &&
                    string.Equals(installed.Sha256, installation.Release?.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ShowDetails()
    {
        if (!IsModDetailsOpen || _detailsRefreshPending)
            return;

        _detailsRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _detailsRefreshPending = false;
            if (IsModDetailsOpen && !RefreshDetails())
                CloseModDetails();
        });
    }

    internal void OpenModDetails(ModInfo item)
    {
        OnlineMods.FirstOrDefault(mod => SameId(mod.Id, item.Id))?.InvalidateChangelog();
        _detailsItem = item;
        if (RefreshDetails())
            IsModDetailsOpen = true;
        else
            CloseModDetails();
    }

    [RelayCommand]
    private void CloseModDetails()
    {
        IsModDetailsOpen = false;
        _detailsItem = null;
        SelectedMod = null;
        SelectedInstalledMod = null;
    }

    private bool RefreshDetails()
    {
        if (_detailsItem is not { } item)
            return false;

        switch (CurrentPage)
        {
            case AppPage.Browse:
                SelectedMod = VisibleOnlineMods.FirstOrDefault(mod => SameId(mod.Id, item.Id));
                SelectedInstalledMod = SelectedMod == null ? null : InstalledMods.FirstOrDefault(mod => SameId(mod.Id, item.Id));
                break;
            case AppPage.Library:
                SelectedInstalledMod = item is InstalledModInfo { IsExternal: true }
                    ? LibraryMods.FirstOrDefault(mod => mod.IsExternal && ProfileFiles.PathComparer.Equals(mod.Id, item.Id))
                    : LibraryMods.FirstOrDefault(mod => !mod.IsExternal && SameId(mod.Id, item.Id));
                SelectedMod = SelectedInstalledMod?.Online;
                break;
            case AppPage.Updates:
                SelectedInstalledMod = AvailableModUpdates.FirstOrDefault(mod => SameId(mod.Id, item.Id));
                SelectedMod = SelectedInstalledMod?.Online;
                break;
            default:
                return false;
        }

        OnPropertyChanged(nameof(DetailsDescription));
        return HasSelection;
    }

    [RelayCommand]
    private async Task OpenRepository()
    {
        if (SelectedMod?.RepoUrl is not string url)
            return;

        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window window })
        {
            await window.Launcher.LaunchUriAsync(new Uri(url));
        }
    }
}
