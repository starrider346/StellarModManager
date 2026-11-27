using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using StellarModManager.Models;

namespace StellarModManager.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAppInstance(NamedPipeServerStream server) : IDisposable
{
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] Acknowledgement = [1];
    private const PipeOptions PipeFlags = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const int PipeBusy = unchecked((int)0x800700E7);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;

    internal static WindowsAppInstance? OwnOrForward(string? url, out bool forwarded)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var scope = $"{identity.User}:{identity.Owner}:{process.SessionId}:{directory.ToUpperInvariant()}";
        var pipeName = $"StellarModManager-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))}";

        using var timeout = new CancellationTokenSource(HandoffTimeout);
        forwarded = false;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeFlags | PipeOptions.FirstPipeInstance);
                return new WindowsAppInstance(pipe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException { HResult: PipeBusy }) {}

            if (ForwardAsync(pipeName, url, timeout.Token).GetAwaiter().GetResult())
            {
                forwarded = true;
                return null;
            }

            Thread.Sleep(50);
        }

        Trace.WriteLine("Timed out contacting the running Stellar Mod Manager window.");
        return null;
    }

    private static async Task<bool> ForwardAsync(string pipeName, string? url, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeFlags);
        try
        {
            await client.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
                AllowSetForegroundWindow(processId);

            var frame = new byte[1 + (url?.Length ?? 0)];
            frame[0] = (byte)(frame.Length - 1);
            if (url is not null)
                Encoding.ASCII.GetBytes(url, frame.AsSpan(1));
            await client.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            var count = await client.ReadAsync(frame.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            return count == 1 && frame[0] == Acknowledgement[0];
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    internal void Listen(Action<string?> activate) => _listener = ListenAsync(activate, _shutdown.Token);

    private async Task ListenAsync(Action<string?> activate, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                try
                {
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    request.CancelAfter(HandoffTimeout);
                    var header = new byte[1];
                    await server.ReadExactlyAsync(header, request.Token).ConfigureAwait(false);
                    if (header[0] > ReleaseInstallLink.UrlLength)
                        continue;

                    string? url = null;
                    if (header[0] != 0)
                    {
                        var payload = new byte[header[0]];
                        await server.ReadExactlyAsync(payload, request.Token).ConfigureAwait(false);
                        url = Encoding.ASCII.GetString(payload);
                        if (!ReleaseInstallLink.IsInstallScheme(url))
                            continue;
                    }

                    activate(url);
                    await server.WriteAsync(Acknowledgement, request.Token).ConfigureAwait(false);
                    
                    await server.ReadAsync(header, request.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) {}
                finally
                {
                    server.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException ex)
        {
            Trace.WriteLine($"Windows activation listener stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener?.GetAwaiter().GetResult();
        server.Dispose();
        _shutdown.Dispose();
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
