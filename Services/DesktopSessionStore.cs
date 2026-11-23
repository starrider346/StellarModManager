using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StellarModManager.Services;

internal sealed record DesktopSession(string Token, DateTimeOffset ExpiresAt);

internal sealed class DesktopSessionStore(Uri origin)
{
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly byte[] _originHash = SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri));

    private string FilePath => Path.Combine(AppPaths.UserDataDirectory, $"steam-session-{Convert.ToHexString(_originHash)}.dat");

    internal static bool IsToken(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigitUpper);

    internal DesktopSession? Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return null;

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~PrivateFile) != 0)
            throw new IOException("The Steam session file has unexpected permissions.");
        var data = File.ReadAllBytes(path);
        if (OperatingSystem.IsWindows())
            data = ProtectedData.Unprotect(data, _originHash, DataProtectionScope.CurrentUser);
        var session = JsonSerializer.Deserialize<DesktopSession>(data);
        if (session is not null && IsToken(session.Token) && session.ExpiresAt > DateTimeOffset.UtcNow)
            return session;

        Clear();
        return null;
    }

    internal void Save(DesktopSession session)
    {
        Directory.CreateDirectory(AppPaths.UserDataDirectory);
        var data = JsonSerializer.SerializeToUtf8Bytes(session);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
            data = ProtectedData.Protect(data, _originHash, DataProtectionScope.CurrentUser);
        else
            options.UnixCreateMode = PrivateFile;

        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, options))
                stream.Write(data);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal void Clear() => File.Delete(FilePath);
}
