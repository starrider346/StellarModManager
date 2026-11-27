using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StellarModManager.Services;

internal static class GameProcessIdentity
{
    internal static bool Matches(Process process, FileInfo game)
    {
        try
        {
            if (process.HasExited)
                return false;

            var executable = process.MainModule?.FileName;
            if (MatchesPath(executable, game.FullName))
                return true;
            if (!OperatingSystem.IsMacOS() || !IsWineHost(Path.GetFileName(executable)))
                return false;

            var (image, prefix) = ReadMacLaunch(process.Id);
            return MatchesPath(ToHostPath(image, prefix), game.FullName);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsWineHost(string? name) =>
        name is "wine" or "wine64" or "wine-preloader" or "wine64-preloader" or "FEXInterpreter" or "FEXLoader" or "FEX";

    private static string? ToHostPath(string? path, string? prefix)
    {
        if (path is null || Path.IsPathFullyQualified(path))
            return path;

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            prefix is null || !Path.IsPathFullyQualified(prefix))
            return null;

        var drivePath = Path.Combine(prefix, "dosdevices", $"{char.ToLowerInvariant(path[0])}:");
        var drive = Directory.ResolveLinkTarget(drivePath, returnFinalTarget: true);
        return drive is null ? null : Path.Combine(drive.FullName, path[3..].Replace('\\', '/'));
    }

    private static bool MatchesPath(string? path, string target)
    {
        if (path is null || !Path.IsPathFullyQualified(path))
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, target, comparison))
            return true;
        if (!string.Equals(Path.GetFileName(path), Path.GetFileName(target), comparison))
            return false;

        return string.Equals(ResolveLinks(path), ResolveLinks(target), comparison);
    }

    private static string ResolveLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        var parts = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            var entry = index == parts.Length - 1
                ? File.ResolveLinkTarget(current, returnFinalTarget: true)
                : Directory.ResolveLinkTarget(current, returnFinalTarget: true);
            current = entry?.FullName ?? current;
        }
        return current;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sysctl")]
    private static extern int Sysctl(int[] name, uint nameLength, IntPtr buffer, ref nuint size, IntPtr newValue, nuint newSize);

    private static (string? Image, string? Prefix) ReadMacLaunch(int pid)
    {
        const int ctlKern = 1;
        const int kernProcArgs2 = 49;
        int[] name = [ctlKern, kernProcArgs2, pid];
        nuint length = 0;
        if (Sysctl(name, (uint)name.Length, IntPtr.Zero, ref length, IntPtr.Zero, 0) != 0 ||
            length < sizeof(int) || length > 1024 * 1024)
            return default;

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (Sysctl(name, (uint)name.Length, buffer, ref length, IntPtr.Zero, 0) != 0 || length < sizeof(int))
                return default;

            var count = Marshal.ReadInt32(buffer);
            var bytes = new byte[(int)length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var position = Array.IndexOf(bytes, (byte)0, sizeof(int));
            if (position < 0 || count < 1)
                return default;
            while (position < bytes.Length && bytes[position] == 0)
                position++;

            string? image = null;
            for (var index = 0; index < count; index++)
            {
                var end = Array.IndexOf(bytes, (byte)0, position);
                if (end < 0)
                    return default;

                if (index == 0 || (index == 1 && IsWineHost(Path.GetFileName(image))))
                    image = Encoding.UTF8.GetString(bytes, position, end - position);
                position = end + 1;
            }

            if (image is null || Path.IsPathFullyQualified(image))
                return (image, null);

            var prefixKey = "WINEPREFIX="u8;
            while (position < bytes.Length)
            {
                var end = Array.IndexOf(bytes, (byte)0, position);
                if (end < 0)
                    break;

                var entry = bytes.AsSpan(position, end - position);
                if (entry.StartsWith(prefixKey))
                    return (image, Encoding.UTF8.GetString(entry[prefixKey.Length..]));
                position = end + 1;
            }

            return (image, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
