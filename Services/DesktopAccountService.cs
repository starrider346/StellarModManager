using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using StellarModManager.Models;

namespace StellarModManager.Services;

internal sealed class DesktopAccountService
{
    private readonly Uri _origin = ModRepositoryService.ConfiguredOrigin();
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    internal DesktopSessionStore Store { get; }

    internal DesktopAccountService()
    {
        Store = new DesktopSessionStore(_origin);
    }

    internal async Task<DesktopSession?> SignInAsync(CancellationToken cancellation)
    {
        var (code, verifier) = await DesktopSteamBrowser.SignInAsync(_origin, cancellation);
        if (code is null)
            return null;

        using var response = await SendAsync(HttpMethod.Post, "/api/auth/steam/desktop/exchange", null,
            JsonContent.Create(new { code, verifier }), cancellation);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<DesktopSession>(cancellation);
        if (session is null || !DesktopSessionStore.IsToken(session.Token) || session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new System.IO.InvalidDataException("The sign-in response did not contain a valid desktop session.");
        return session;
    }

    internal Task<SteamAccount> CurrentUserAsync(DesktopSession session, CancellationToken cancellation) =>
        ReadAsync<SteamAccount>("/api/auth/me", session, cancellation);

    internal async Task SignOutAsync(DesktopSession session, CancellationToken cancellation)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/auth/logout", session, cancellation: cancellation);
        response.EnsureSuccessStatusCode();
    }

    internal Task<SubmissionDiscussion> DiscussionAsync(Guid submission, int page, DesktopSession? session, CancellationToken cancellation) =>
        ReadAsync<SubmissionDiscussion>(FormattableString.Invariant($"/api/submissions/{submission:D}/comments?page={page}"), session, cancellation);

    internal Task<SubmissionReplies> RepliesAsync(Guid submission, Guid parent, int page, CancellationToken cancellation) =>
        ReadAsync<SubmissionReplies>(FormattableString.Invariant($"/api/submissions/{submission:D}/comments/{parent:D}/replies?page={page}"), null, cancellation);

    internal Task<HttpResponseMessage> WriteCommentAsync(Guid submission, Guid? edit, Guid? parent, string comment, int? rating,
        DesktopSession session, CancellationToken cancellation)
    {
        var path = $"/api/submissions/{submission:D}/comments";
        if (edit is { } commentUuid)
            path += $"/{commentUuid:D}";
        if (parent is { } parentUuid)
            path += $"/{parentUuid:D}/replies";
        return SendAsync(edit.HasValue ? HttpMethod.Put : HttpMethod.Post, path, session,
            JsonContent.Create(new { comment, rating }), cancellation);
    }

    internal Task<HttpResponseMessage> DeleteCommentAsync(Guid submission, Guid comment, DesktopSession session,
        CancellationToken cancellation) =>
        SendAsync(HttpMethod.Delete, $"/api/submissions/{submission:D}/comments/{comment:D}", session, cancellation: cancellation);

    private async Task<T> ReadAsync<T>(string path, DesktopSession? session, CancellationToken cancellation)
    {
        using var response = await SendAsync(HttpMethod.Get, path, session, cancellation: cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellation) ??
            throw new System.IO.InvalidDataException("The API returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, DesktopSession? session,
        HttpContent? content = null, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(_origin, path)) { Content = content };
        if (session is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        return await _client.SendAsync(request, cancellation);
    }
}
