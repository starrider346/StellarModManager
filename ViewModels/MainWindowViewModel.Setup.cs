using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    public IReadOnlyList<GameInstallationInfo> DetectedGameInstalls { get; } = GameLocatorService.Locate();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogsPage))]
    public partial bool SetupRequired { get; set; } = true;

    [ObservableProperty]
    public partial bool IsMelonLoaderValid { get; set; }

    [ObservableProperty]
    public partial bool IsInstallingMelonLoader { get; set; }

    [ObservableProperty]
    public partial string InstallProgressText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSteamLaunchOptions))]
    public partial string? SteamLaunchOptions { get; private set; }

    public bool HasSteamLaunchOptions => SteamLaunchOptions is not null;

    private readonly MelonLoaderInstaller _melonLoaderInstaller = new();

    partial void OnGamePathChanged(string value)
    {
        InstallMelonLoaderCommand.NotifyCanExecuteChanged();
        Logs.SetGamePath(value);
    }

    partial void OnIsMelonLoaderValidChanged(bool value)
    {
        CloseSetupCommand.NotifyCanExecuteChanged();
        InstallMelonLoaderCommand.NotifyCanExecuteChanged();
        ApplyAutoCheckTimerState();
    }

    partial void OnIsInstallingMelonLoaderChanged(bool value) => InstallMelonLoaderCommand.NotifyCanExecuteChanged();

    private bool ResolveGameFolder(string path)
    {
        GameProcess = null;
        GamePath = path;
        var executable = GameLocatorService.FindExecutable(path);
        SteamLaunchOptions = executable is null ? null : GameLaunchService.GetSteamLaunchOptions(executable);
        RefreshMelonLoaderStatus(executable);
        InstallMelonLoaderCommand.NotifyCanExecuteChanged();
        return IsMelonLoaderValid;
    }

    private void RefreshMelonLoaderStatus(FileInfo? executable)
    {
        if (executable is null)
        {
            IsMelonLoaderValid = false;
            MelonLoaderStatusText = LocalizationService.Instance[
                GameLocatorService.IsSupportedPlatform ? "StatusGameNotFound" : "StatusUnsupportedGameBuild"];
            return;
        }

        IsMelonLoaderValid = new MelonLoaderDetector().CheckInstallation(GamePath) == MelonLoaderStatus.Installed;
        MelonLoaderStatusText = LocalizationService.Instance[
            IsMelonLoaderValid ? "MelonLoaderInstalledStatus" : "MelonLoaderMissingStatus"];
    }

    [RelayCommand]
    private async Task CopySteamLaunchOptions()
    {
        if (SteamLaunchOptions is not { } options ||
            App.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            return;
        try
        {
            await clipboard.SetTextAsync(options);
            ReportSuccess("StatusSteamLaunchOptionsCopied");
        }
        catch (Exception ex)
        {
            ReportError("StatusSteamLaunchOptionsCopyFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void SelectSuggestedFolder(GameInstallationInfo info) => UseGameFolder(info.Directory.FullName);

    [RelayCommand]
    private async Task SelectGameFolder()
    {
        Window? window = null;
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            window = desktop.MainWindow;
        }

        if (window == null || IsModOperationInProgress)
            return;

        if (!TryBeginModOperation(requireProfile: false))
            return;
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = LocalizationService.Instance["SelectStellarDriveFolder"],
                    AllowMultiple = false
                });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                SetGameFolder(path);
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    private void UseGameFolder(string path)
    {
        if (!TryBeginModOperation(requireProfile: false))
            return;
        try
        {
            SetGameFolder(path);
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    private void SetGameFolder(string path)
    {
        if (ResolveGameFolder(path))
        {
            ApplyMelonLoaderConsoleSetting();
            OpenGameProfiles(path, _settingsService.LoadGameProfile()?.GamePath);
            _settingsService.SaveGameProfile(new GameProfile { GamePath = path });
            SearchGameProcessIfNull();
        }
        else
        {
            ClearGameProfiles();
            SetupRequired = true;
        }
    }

    private bool CanInstallMelonLoader() =>
        !IsMelonLoaderValid && !IsInstallingMelonLoader && GameLocatorService.FindExecutable(GamePath) is not null;

    [RelayCommand(CanExecute = nameof(CanInstallMelonLoader))]
    private async Task InstallMelonLoader()
    {
        if (GameLocatorService.FindExecutable(GamePath) is not { } game || !TryBeginModOperation(requireProfile: false))
            return;
        IsInstallingMelonLoader = true;
        InstallProgressText = "Starting...";

        try
        {
            using (var running = GameLaunchService.DetectExisting(game))
            {
                if (running is not null)
                {
                    MelonLoaderStatusText = LocalizationService.Instance["StatusLoaderGameRunning"];
                    return;
                }
            }

            var progress = new Progress<string>(text => InstallProgressText = text);
            await _melonLoaderInstaller.InstallAsync(GamePath, progress);
            RefreshMelonLoaderStatus(GameLocatorService.FindExecutable(GamePath));

            if (IsMelonLoaderValid)
            {
                ApplyMelonLoaderConsoleSetting();
                OpenGameProfiles(GamePath, _settingsService.LoadGameProfile()?.GamePath);
                _settingsService.SaveGameProfile(new GameProfile { GamePath = GamePath });
            }
        }
        catch (Exception ex)
        {
            MelonLoaderStatusText = LocalizationService.Instance.Format("StatusLoaderInstallFailed", ex.Message);
        }
        finally
        {
            IsInstallingMelonLoader = false;
            IsModOperationInProgress = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsMelonLoaderValid))]
    private void CloseSetup()
    {
        if (IsModOperationInProgress)
            return;
        SetupRequired = false;
        _ = RefreshMods();
    }
}
