using System;

namespace StellarModManager.Models;

internal readonly record struct ReleaseInstallLink(Guid SubmissionUuid, Guid ReleaseUuid)
{
    private const string SchemePrefix = "stellarmodmanager:";
    private const string InstallHost = SchemePrefix + "//install";
    private const string ReleasePath = "/release/";
    private const int UuidLength = 36;
    internal static readonly int UrlLength = InstallHost.Length + ReleasePath.Length + UuidLength * 2 + 1;

    internal string Code => $"{SubmissionUuid:D}:{ReleaseUuid:D}";

    internal static bool IsInstallScheme(string value) =>
        value.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase);

    internal static string? FromArguments(string[]? args)
    {
        if (args == null || !Array.Exists(args, IsInstallScheme))
            return null;

        return args.Length == 1 && TryParse(args[0], out _) ? args[0] : SchemePrefix;
    }

    internal static bool TryParse(string url, out ReleaseInstallLink link)
    {
        link = default;
        var prefixLength = InstallHost.Length + ReleasePath.Length;
        if (url.Length != UrlLength || !url.StartsWith(InstallHost, StringComparison.OrdinalIgnoreCase) ||
            !url.AsSpan(InstallHost.Length, ReleasePath.Length).SequenceEqual(ReleasePath) ||
            url[prefixLength + UuidLength] != '/')
            return false;

        return TryParseIds(url.AsSpan(prefixLength, UuidLength), url.AsSpan(prefixLength + UuidLength + 1), out link);
    }

    internal static bool TryParseCode(string code, out ReleaseInstallLink link)
    {
        link = default;
        var separator = code.IndexOf(':');
        return separator >= 0 &&
            TryParseIds(code.AsSpan(0, separator).Trim(), code.AsSpan(separator + 1).Trim(), out link);
    }

    private static bool TryParseIds(ReadOnlySpan<char> submission, ReadOnlySpan<char> release, out ReleaseInstallLink link)
    {
        link = default;
        if (submission.Length != UuidLength || release.Length != UuidLength ||
            !Guid.TryParseExact(submission, "D", out var submissionUuid) || submissionUuid == Guid.Empty ||
            !Guid.TryParseExact(release, "D", out var releaseUuid) || releaseUuid == Guid.Empty)
            return false;

        link = new ReleaseInstallLink(submissionUuid, releaseUuid);
        return true;
    }
}
