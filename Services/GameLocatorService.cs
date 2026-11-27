using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace StellarModManager.Services;

public sealed record GameInstallationInfo(DirectoryInfo Directory, AvaloniaBitmap? Icon)
{
    public string Source => OperatingSystem.IsMacOS() ? "Steam · NotProton" : "Steam";
}

public static class GameLocatorService
{
    public const string ExecutableName = "StellarDrive.exe";

    internal static bool IsSupportedPlatform => OperatingSystem.IsWindows() ||
        (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64);

    public static IReadOnlyList<GameInstallationInfo> Locate() => IsSupportedPlatform
        ? SteamInfoService.GetSteamGames()
            .Select(game => FindExecutable(game.Directory.FullName))
            .OfType<FileInfo>()
            .Select(executable => new GameInstallationInfo(executable.Directory!,
                OperatingSystem.IsWindowsVersionAtLeast(6, 1) ? ReadWindowsIcon(executable.FullName) : null))
            .ToList()
        : [];

    internal static FileInfo? FindExecutable(string path)
    {
        if (!IsSupportedPlatform || !Path.IsPathFullyQualified(path))
            return null;

        var executablePath = Path.Combine(path, ExecutableName);
        return File.Exists(executablePath) ? new FileInfo(executablePath) : null;
    }

    [SupportedOSPlatform("windows6.1")]
    private static AvaloniaBitmap? ReadWindowsIcon(string executablePath)
    {
        try
        {
            using var fileIcon = Icon.ExtractAssociatedIcon(executablePath);
            using var bitmap = fileIcon?.ToBitmap();
            if (bitmap is null)
                return null;

            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                return new AvaloniaBitmap(
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul,
                    data.Scan0, new Avalonia.PixelSize(data.Width, data.Height), new Avalonia.Vector(96, 96), data.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (ArgumentException) { return null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
