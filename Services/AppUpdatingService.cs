using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using StellarModManager.Models;

namespace StellarModManager.Services;

public class AppUpdatingService
{
    private const string RepoApi = "https://api.github.com/repos";
    private const string DefaultUpdateRepository = "jollyname/StellarModManager";

    public async Task<UpdateInfo?> CheckAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StellarModManager");

        var assembly = typeof(AppUpdatingService).Assembly;
        var repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "AppUpdateRepository")?.Value ?? DefaultUpdateRepository;
        var release = await client.GetFromJsonAsync<GitHubRelease>($"{RepoApi}/{repository}/releases/latest");
        if (release is null) return null;

        var latest = Version.Parse(release.tag_name.TrimStart('v'));
        var current = assembly.GetName().Version!;

        if (latest <= current) return null;

        var update = new UpdateInfo(latest, release.assets, release.body, release.html_url);
        return OperatingSystem.IsMacOS() && FindPlatformDownload(update) is null ? null : update;
    }

    internal static Uri? FindPlatformDownload(UpdateInfo update)
    {
        const string suffix = "_osx-arm64.zip";
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return null;

        var asset = update.Assets.FirstOrDefault(asset =>
            asset.name.StartsWith("StellarModManager_", StringComparison.Ordinal) &&
            asset.name.EndsWith(suffix, StringComparison.Ordinal));
        return Uri.TryCreate(asset?.browser_download_url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri : null;
    }

    public async Task DownloadAndInstallUpdateAsync(UpdateInfo update, IProgress<double> progress)
    {
        GitHubAsset? installerAsset = null;

        foreach (var asset in update.Assets)
        {
            string nameLower = asset.name.ToLower();

            if (nameLower.EndsWith(".exe") && nameLower.Contains("setup"))
            {
                installerAsset = asset;
                break;
            }
        }

        string tempFilePath = Path.Combine(Path.GetTempPath(), installerAsset!.name);

        using var client = new HttpClient();
        using var response = await client.GetAsync(installerAsset.browser_download_url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? 0;

        using var httpStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);

        byte[] buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await httpStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                double percent = (double)totalRead / totalBytes * 100;
                progress.Report(percent);
            }
        }

        fileStream.Close();
        var startinfo = new ProcessStartInfo(tempFilePath)
        {
            UseShellExecute = true,
            //start silent install, but open application after completion
            Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /OPENWHENFINISHED"
        };

        Process.Start(startinfo);
    }

    //check if the app is installed or portable
    public static bool IsInstalled()
    {
        return OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
    }
}
