using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace StellarModManager.Services;

public static class GameLaunchService
{
    public static async Task<Process> Launch(FileInfo game)
    {
        if (DetectExisting(game) is { } running)
            return running;

        var isMac = OperatingSystem.IsMacOS();
        if (SteamInfoService.GetAppId(game.Directory) is { } appId)
        {
            if (await RequestSteamLaunch(appId))
            {
                var timeout = TimeSpan.FromSeconds(isMac ? 30 : 10);
                var timer = Stopwatch.StartNew();
                do
                {
                    if (DetectExisting(game) is { } process)
                        return process;
                    await Task.Delay(350);
                } while (timer.Elapsed < timeout);

                if (isMac)
                    throw new InvalidOperationException(LocalizationService.Instance["StatusSteamLaunchTimeout"]);
            }
            else if (isMac)
            {
                throw new InvalidOperationException(LocalizationService.Instance["StatusSteamOpenFailed"]);
            }
        }
        else if (isMac)
        {
            throw new InvalidOperationException(LocalizationService.Instance["StatusSteamGameRequired"]);
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = game.FullName,
            WorkingDirectory = game.DirectoryName,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("The game process could not be started.");
    }

    public static Process? DetectExisting(FileInfo game)
    {
        Process? result = null;
        foreach (var process in Process.GetProcesses())
        {
            if (result is null && GameProcessIdentity.Matches(process, game))
                result = process;
            else
                process.Dispose();
        }
        return result;
    }

    internal static string? GetSteamLaunchOptions(FileInfo game) =>
        OperatingSystem.IsMacOS() && SteamInfoService.GetAppId(game.Directory) is not null
            ? "WINEDLLOVERRIDES=\"version=n,b\" %command%"
            : null;

    private static async Task<bool> RequestSteamLaunch(uint appId)
    {
        var uri = FormattableString.Invariant($"steam://rungameid/{appId}");
        var start = new ProcessStartInfo { UseShellExecute = OperatingSystem.IsWindows() };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = uri;
        }
        else
        {
            start.FileName = "/usr/bin/open";
            start.ArgumentList.Add("-b");
            start.ArgumentList.Add("com.valvesoftware.steam");
            start.ArgumentList.Add(uri);
        }

        try
        {
            using var opener = Process.Start(start);
            if (OperatingSystem.IsWindows())
                return true;

            if (opener is null)
                return false;
            await opener.WaitForExitAsync();
            return opener.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
