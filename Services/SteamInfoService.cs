using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StellarModManager.Services;

internal sealed record SteamGameDirectory(DirectoryInfo Directory, uint AppId);

internal static partial class SteamInfoService
{
    private static readonly uint[] StellarDriveAppIds = [4183270, 4689730];

    [GeneratedRegex("""^\s*"(?<key>[^"]+)"\s*"(?<value>(?:\\.|[^"\\])*)"\s*(?://.*)?$""")]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex("""\\(["\\])""")]
    private static partial Regex EscapePattern();

    internal static IEnumerable<SteamGameDirectory> GetSteamGames(IEnumerable<string>? steamRoots = null)
    {
        foreach (var library in GetSteamLibraries(steamRoots ?? GetSteamRoots()))
        {
            var steamApps = Path.Combine(library, "steamapps");
            foreach (var appId in StellarDriveAppIds)
            {
                if (ReadGameDirectory(steamApps, appId) is { } game && game.Directory.Exists)
                    yield return game;
            }
        }
    }

    internal static uint? GetAppId(DirectoryInfo? directory)
    {
        if (directory?.Parent is not { } common || common.Parent is not { } steamApps ||
            !ProfileFiles.PathComparer.Equals(common.Name, "common") ||
            !ProfileFiles.PathComparer.Equals(steamApps.Name, "steamapps"))
            return null;

        foreach (var appId in StellarDriveAppIds)
        {
            if (ReadGameDirectory(steamApps.FullName, appId) is { } game &&
                ProfileFiles.PathComparer.Equals(game.Directory.FullName, directory.FullName))
                return game.AppId;
        }

        return null;
    }

    private static SteamGameDirectory? ReadGameDirectory(string steamApps, uint appId)
    {
        var manifest = ReadValues(Path.Combine(steamApps, $"appmanifest_{appId}.acf"));
        if (!manifest.TryGetValue("appid", out var id) || !uint.TryParse(id, out var manifestId) || manifestId != appId ||
            !manifest.TryGetValue("installdir", out var folder) || string.IsNullOrWhiteSpace(folder) ||
            folder is "." or ".." || folder.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            return null;

        return new(new DirectoryInfo(Path.Combine(steamApps, "common", folder)), appId);
    }

    private static IEnumerable<string> GetSteamLibraries(IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(ProfileFiles.PathComparer);
        foreach (var root in steamRoots)
        {
            if (Directory.Exists(Path.Combine(root, "steamapps")) && seen.Add(CanonicalDirectory(root)))
                yield return root;

            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (var path in ReadPairs(libraryFile)
                         .Where(pair => string.Equals(pair.Key, "path", StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Value))
            {
                if (Path.IsPathFullyQualified(path) && Directory.Exists(Path.Combine(path, "steamapps")) &&
                    seen.Add(CanonicalDirectory(path)))
                    yield return path;
            }
        }
    }

    private static string CanonicalDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        try
        {
            return Path.TrimEndingDirectorySeparator(
                directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName);
        }
        catch (IOException) { return directory.FullName; }
        catch (UnauthorizedAccessException) { return directory.FullName; }
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        if (OperatingSystem.IsWindows())
            return GetWindowsSteamRoots();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS() && !string.IsNullOrEmpty(home)
            ? [Path.Combine(home, "Library", "Application Support", "Steam")]
            : [];
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetWindowsSteamRoots()
    {
        (RegistryKey Root, string SubKey, string ValueName)[] keys =
        [
            (Registry.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
        ];
        foreach (var (root, subKey, valueName) in keys)
        {
            string? path;
            try
            {
                using var key = root.OpenSubKey(subKey);
                path = key?.GetValue(valueName) as string;
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (System.Security.SecurityException) { continue; }

            if (!string.IsNullOrEmpty(path))
                yield return path;
        }
    }

    private static Dictionary<string, string> ReadValues(string file)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ReadPairs(file))
            values.TryAdd(key, value);
        return values;
    }

    private static List<KeyValuePair<string, string>> ReadPairs(string file)
    {
        var result = new List<KeyValuePair<string, string>>();
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var pair = KeyValuePattern().Match(line);
                if (pair.Success)
                    result.Add(new(pair.Groups["key"].Value, EscapePattern().Replace(pair.Groups["value"].Value, "$1")));
            }
        }
        catch (IOException) { result.Clear(); }
        catch (UnauthorizedAccessException) { result.Clear(); }

        return result;
    }
}
