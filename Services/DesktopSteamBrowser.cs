using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StellarModManager.Services;

internal static class DesktopSteamBrowser
{
    internal static async Task<(string? Code, string Verifier)> SignInAsync(Uri origin, CancellationToken cancellation)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var challenge = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var signIn = new Uri(origin, FormattableString.Invariant($"/api/auth/steam/desktop?port={port}&state={state}&challenge={challenge}"));
            using var browser = Process.Start(new ProcessStartInfo(signIn.AbsoluteUri) { UseShellExecute = true });

            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync(cancellation);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var stream = connection.GetStream();
                    var path = await ReadPathAsync(stream, port, requestTimeout.Token);
                    var query = path is null ? null : System.Web.HttpUtility.ParseQueryString(path.Query);
                    var code = query?["code"];
                    var canceled = query?["error"] == "1";
                    var valid = string.Equals(query?["state"], state, StringComparison.Ordinal) &&
                        (canceled || DesktopSessionStore.IsToken(code));
                    var message = valid ? "Return to Stellar Mod Manager to continue." : "Invalid sign-in callback.";
                    var status = valid ? "200 OK" : "400 Bad Request";
                    var response = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {message.Length}\r\n" +
                        "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n" + message;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), requestTimeout.Token);
                    if (valid)
                        return (canceled ? null : code, verifier);
                }
                catch (IOException) when (!cancellation.IsCancellationRequested) { }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<Uri?> ReadPathAsync(NetworkStream stream, int port, CancellationToken cancellation)
    {
        var bytes = new byte[8192];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellation);
            if (read == 0)
                return null;
            length += read;

            var headers = Encoding.ASCII.GetString(bytes, 0, length);
            var end = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;

            var lines = headers[..end].Split("\r\n");
            var host = FormattableString.Invariant($"127.0.0.1:{port}");
            var hostHeaders = Array.FindAll(lines, line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
            if (hostHeaders.Length != 1 || hostHeaders[0][5..].Trim() != host)
                return null;

            var request = lines[0].Split(' ');
            if (request.Length == 3 && request[0] == "GET" && request[1].StartsWith("/steam/callback?", StringComparison.Ordinal) &&
                Uri.TryCreate($"http://{host}{request[1]}", UriKind.Absolute, out var path) && path.AbsolutePath == "/steam/callback")
                return path;

            return null;
        }

        return null;
    }
}
