using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    private Process? GameProcess
    {
        get;
        set
        {
            if (field == value)
                return;

            if (field is { } previous)
            {
                previous.Exited -= OnExitGameProcess;
                previous.Dispose();
            }

            field = value;
            IsGameRunning = value is not null;

            if (value is not null)
            {
                value.Exited += OnExitGameProcess;
                try
                {
                    value.EnableRaisingEvents = true;
                }
                catch (InvalidOperationException)
                {
                    GameProcess = null;
                }
            }
        }
    }

    [ObservableProperty]
    public partial bool IsGameRunning { get; private set; }

    private void OnExitGameProcess(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, GameProcess))
                GameProcess = null;
        });
    }

    [RelayCommand]
    private async Task ToggleGame()
    {
        if (!TryBeginModOperation(requireProfile: !IsGameRunning))
            return;

        try
        {
            if (IsGameRunning)
                StopGame();
            else
                await LaunchGame();
        }
        finally
        {
            IsModOperationInProgress = false;
        }
    }

    private async Task LaunchGame()
    {
        if (GameLocatorService.FindExecutable(GamePath) is not { } game)
        {
            ReportError("StatusGameNotFound");
            return;
        }

        try
        {
            using var fileLock = _profileStore!.LockForLaunch(ActiveProfile!.Id);
            GameProcess = await GameLaunchService.Launch(game);
        }
        catch (Exception ex)
        {
            ReportError("StatusLaunchFailed", ex.Message);
        }
    }

    private void StopGame()
    {
        if (GameProcess is not { } process)
            return;

        try
        {
            if (process.HasExited)
            {
                GameProcess = null;
                return;
            }

            if (GameLocatorService.FindExecutable(GamePath) is not { } game || !GameProcessIdentity.Matches(process, game))
            {
                ReportError("StatusGameProcessUnverified");
                return;
            }

            process.Kill(entireProcessTree: OperatingSystem.IsWindows());
        }
        catch (Exception ex)
        {
            ReportError("StatusStopFailed", ex.Message);
        }
    }

    private void SearchGameProcessIfNull()
    {
        if (GameProcess is null && GameLocatorService.FindExecutable(GamePath) is { } game)
            GameProcess = GameLaunchService.DetectExisting(game);
    }
}
