using StellarModManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using System.Linq; // thumbnails
using Avalonia.Platform;

namespace StellarModManager.Services;

public class ModRepositoryService
{
    private const int PageSize = 50;
    private readonly HttpClient _httpClient;
    private readonly Uri _origin;
    private readonly SemaphoreSlim _hashLookups = new(4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashMatch> _hashMatches = new();

    public ModRepositoryService() : this(new HttpClient(), ConfiguredOrigin()) { }

    internal ModRepositoryService(HttpClient httpClient, Uri origin)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StellarModManager");
        _origin = origin;
    }

    internal static Uri ConfiguredOrigin()
    {
        var origin = typeof(ModRepositoryService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "StellarModsApiOrigin")?.Value;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var configured) || configured.Scheme != Uri.UriSchemeHttps ||
            configured.UserInfo.Length > 0 || configured.AbsolutePath != "/" || configured.Query.Length > 0 ||
            configured.Fragment.Length > 0)
            throw new InvalidOperationException("Configure StellarModsApiOrigin as an HTTPS origin.");
        return configured;
    }

    private static readonly string[] DefaultThumbnailAssets =
    {
        "avares://StellarModManager/Assets/Thumbnails/smileyAngry.png",
        "avares://StellarModManager/Assets/Thumbnails/smileyCry.png",
        "avares://StellarModManager/Assets/Thumbnails/smileyFrown.png",
        "avares://StellarModManager/Assets/Thumbnails/smileyGrumpy.png",
        "avares://StellarModManager/Assets/Thumbnails/smileyTounge.png",
        "avares://StellarModManager/Assets/Thumbnails/smileyWhat.png"
    };

    private static Bitmap LoadDefaultThumbnail()
    {
        int index = Random.Shared.Next(DefaultThumbnailAssets.Length);

        using var stream = AssetLoader.Open(new Uri(DefaultThumbnailAssets[index]));

        return Bitmap.DecodeToWidth(stream, 192);
    }

    internal async Task<CatalogPage> GetPageAsync(int page, int pageSize, string keyword, int sortIndex,
        SubmissionType type,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var sort = sortIndex switch { 1 => "Name", 2 => "Downloads", _ => "ReleaseTime" };
        var order = sortIndex == 1 ? "ascending" : "descending";
        var submissionType = type == SubmissionType.Plugin ? "plugin" : "mod";
        var query = $"keyword={Uri.EscapeDataString(keyword.Trim())}&sort={sort}&sort_order={order}&type={submissionType}";
        var apiPageSize = Math.Min(pageSize, PageSize);
        var offset = (page - 1L) * pageSize;
        var cursor = offset;
        var mods = new Dictionary<Guid, OnlineModInfo>();
        int totalCount;
        do
        {
            var apiPage = cursor / apiPageSize + 1;
            var result = await ReadAsync<SubmissionPage>(
                $"/api/submissions?page={apiPage}&page_size={apiPageSize}&{query}", cancellationToken);
            totalCount = result.TotalCount;
            var skip = (int)(cursor % apiPageSize);
            var take = (int)Math.Min(pageSize - (cursor - offset), Math.Max(0, result.Items.Count - skip));
            foreach (var item in result.Items.Skip(skip).Take(take))
                if (item.Type is "mod" or "plugin")
                    mods.TryAdd(item.Uuid, item.ToOnline());
            if (take == 0)
                break;
            cursor += take;
        }
        while (cursor < offset + pageSize && cursor < totalCount);
        return new CatalogPage(mods.Values.ToList(), page, pageSize, totalCount);
    }

    internal async Task<OnlineModInfo?> GetSubmissionAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(new Uri(_origin, $"/api/submissions/{uuid}"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var submission = await response.Content.ReadFromJsonAsync<Submission>(cancellationToken);
        return submission is { Type: "mod" or "plugin" } ? submission.ToOnline() : null;
    }

    internal async Task<(List<OnlineModInfo> ToInstall, List<string> Missing)> ResolveInstallAsync(
        OnlineModInfo root, IEnumerable<InstalledModInfo> installed)
    {
        var installedById = installed.ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
        var available = new Dictionary<string, OnlineModInfo>(StringComparer.OrdinalIgnoreCase) { [root.Id] = root };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
        var pending = new Stack<ModInfo>();
        pending.Push(root);
        while (pending.TryPop(out var mod))
        {
            foreach (var id in mod.Needs ?? [])
            {
                if (!visited.Add(id))
                    continue;
                if (installedById.TryGetValue(id, out var existing))
                {
                    pending.Push(existing);
                    continue;
                }
                if (!Guid.TryParse(id, out var uuid))
                    continue;
                var dependency = await GetSubmissionAsync(uuid);
                if (dependency is not { CanInstall: true })
                    continue;
                available[id] = dependency;
                pending.Push(dependency);
            }
        }
        var result = DependencyResolver.ResolveInstall(root, installedById.Values, id => available.GetValueOrDefault(id));
        return (result.ToInstall.Cast<OnlineModInfo>().ToList(), result.Missing);
    }

    internal void ClearHashMatches() => _hashMatches.Clear();

    internal async Task<(Submission Submission, CatalogRelease Release)?> IdentifyAsync(string sha256, CancellationToken cancellationToken)
    {
        if (!_hashMatches.TryGetValue(sha256, out var match))
        {
            await _hashLookups.WaitAsync(cancellationToken);
            try
            {
                if (!_hashMatches.TryGetValue(sha256, out match))
                {
                    var result = await ReadAsync<SubmissionPage>($"/api/submissions?sha256={sha256}&page_size=2", cancellationToken);
                    match = new HashMatch(result.TotalCount == 1 ? result.Items.SingleOrDefault() : null);
                    _hashMatches[sha256] = match;
                }
            }
            finally
            {
                _hashLookups.Release();
            }
        }
        return match.Submission?.MatchedRelease is { } release ? (match.Submission, release) : null;
    }

    internal async Task LogDownloadAsync(Guid submissionUuid, Guid releaseUuid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _httpClient.PostAsync(
                new Uri(_origin, $"/api/submissions/{submissionUuid}/releases/{releaseUuid}/downloads"),
                null, timeout.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"Download reporting failed for {releaseUuid}: {exception.Message}");
        }
    }

    internal async Task<string?> GetCurrentGameVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<GameVersionResponse>("/api/game-version", cancellationToken);
        return string.IsNullOrWhiteSpace(result.CurrentGameVersion) ? null : result.CurrentGameVersion.Trim();
    }

    private sealed record GameVersionResponse(string? CurrentGameVersion);

    private sealed record HashMatch(Submission? Submission);

    private async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default) where T : class =>
        await _httpClient.GetFromJsonAsync<T>(new Uri(_origin, path), cancellationToken)
        ?? throw new InvalidDataException("The catalog returned an empty response.");

    public async Task LoadChangelogAsync(OnlineModInfo mod)
    {
        var task = mod.ChangelogTask ??= FetchChangelogAsync(mod, mod.ChangelogRevision);
        await task;
        if (ReferenceEquals(mod.ChangelogTask, task) && mod.ChangelogUnavailable)
            mod.ChangelogTask = null;
    }

    private async Task FetchChangelogAsync(OnlineModInfo mod, (Guid?, string?, string?, string?) revision)
    {
        var generation = mod.ChangelogGeneration;
        mod.IsChangelogLoading = true;
        mod.ChangelogUnavailable = false;
        try
        {
            var entries = new List<ChangelogEntry>();
            for (var page = 1; ; page++)
            {
                var result = await ReadAsync<ReleasePage>($"/api/submissions/{mod.Id}/releases?page={page}&page_size={PageSize}");
                entries.AddRange(result.Items.Select((release, index) =>
                {
                    var installation = new OnlineModInfo
                    {
                        Id = mod.Id, Type = mod.Type, Name = mod.Name, Author = mod.Author,
                        Description = mod.Description, CurrentGameVersion = mod.CurrentGameVersion
                    };
                    installation.SetRelease(release);
                    return new ChangelogEntry(release.Uuid, release.Version, release.Changelog)
                    {
                        IsNewest = page == 1 && index == 0,
                        Installation = installation,
                        Dependencies = release.Dependencies.Hard
                            .Select(dependency => new ReleaseDependencyInfo(dependency.Uuid, dependency.Name, true))
                            .Concat(release.Dependencies.Soft.Select(dependency =>
                                new ReleaseDependencyInfo(dependency.Uuid, dependency.Name, false))).ToList()
                    };
                }));
                if (result.Items.Count == 0 || page * (long)result.PageSize >= result.TotalCount)
                    break;
            }
            if (revision == mod.ChangelogRevision && generation == mod.ChangelogGeneration)
            {
                mod.Changelog.Clear();
                foreach (var entry in entries)
                {
                    if (entry.Installation is { } installation)
                        installation.CurrentGameVersion = mod.CurrentGameVersion;
                    mod.Changelog.Add(entry);
                }
                // Release history is freshly loaded when details reopen. Keep the
                // submission warning in sync with author edits to its latest release.
                mod.SetRelease(entries.FirstOrDefault()?.Installation?.Release);
                mod.IsChangelogLoading = false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            if (revision == mod.ChangelogRevision && generation == mod.ChangelogGeneration)
                mod.ChangelogUnavailable = true;
        }
        finally
        {
            if (revision == mod.ChangelogRevision && generation == mod.ChangelogGeneration)
                mod.IsChangelogLoading = false;
        }
    }

    public async Task DownloadModAsync(string url, string destination, IProgress<double>? progress = null)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(destination);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalRead += bytesRead;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                progress?.Report((double)totalRead / totalBytes.Value * 100);
            }
        }
    }

    public async Task LoadThumbnailAsync(OnlineModInfo mod)
    {
        if (mod.ThumbnailImage != null)
            return;

        if (string.IsNullOrWhiteSpace(mod.ThumbnailUrl))
        {
            mod.ThumbnailImage = LoadDefaultThumbnail();
            return;
        }

        try
        {
            byte[] bytes = await _httpClient.GetByteArrayAsync(mod.ThumbnailUrl);

            using var contentStream = new MemoryStream(bytes);
            mod.ThumbnailImage = Bitmap.DecodeToWidth(contentStream, 192);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Thumbnail loading failed for {mod.Name}: {ex.Message}");

            mod.ThumbnailImage = LoadDefaultThumbnail();
        }
    }

    public async Task LoadGalleryAsync(OnlineModInfo mod)
    {
        var urls = new[] { mod.ThumbnailUrl }
        .Concat(mod.ImageUrls)
        .Where(url => !string.IsNullOrEmpty(url))
        .Distinct()
        .ToList();


        if (mod.GalleryLoaded || urls.Count == 0)
            return;
        mod.GalleryLoaded = true;

        foreach (string url in urls)
        {
            try
            {
                byte[] bytes = await _httpClient.GetByteArrayAsync(url);
                using var contentStream = new MemoryStream(bytes);
                mod.GalleryImages.Add(Bitmap.DecodeToWidth(contentStream, 1280));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gallery image failed for {mod.Name}: {ex.Message}");
            }
        }
    }

}
