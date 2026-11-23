using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarModManager.Models;
using StellarModManager.Services;

namespace StellarModManager.ViewModels;

public partial class MainWindowViewModel
{
    private readonly DesktopAccountService _desktopAccount = new();
    private DesktopSession? _desktopSession;
    private CancellationTokenSource? _steamSignIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSteamUser), nameof(CanPostDiscussion), nameof(CanRateDiscussion), nameof(CanDeleteDiscussionComments))]
    public partial SteamAccount? SteamUser { get; private set; }

    [ObservableProperty]
    public partial bool IsSteamBusy { get; private set; }

    [ObservableProperty]
    public partial string? SteamStatus { get; private set; }

    public bool IsSteamSignInPending => _steamSignIn is not null;

    public bool HasSteamUser => SteamUser is not null;

    internal async Task RestoreSteamSessionAsync()
    {
        IsSteamBusy = true;
        try
        {
            var session = _desktopAccount.Store.Load();
            if (session is not null)
                await ActivateSteamSessionAsync(session, save: false, CancellationToken.None);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearSavedSession();
        }
        catch (Exception exception) when (IsAccountError(exception))
        {
            SteamStatus = LocalizationService.Instance["SteamRestoreFailed"];
        }
        finally
        {
            IsSteamBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInWithSteamAsync()
    {
        if (IsSteamBusy || HasSteamUser)
            return;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _steamSignIn = cancellation;
        OnPropertyChanged(nameof(IsSteamSignInPending));
        IsSteamBusy = true;
        SteamStatus = LocalizationService.Instance["SteamWaiting"];
        try
        {
            var session = await _desktopAccount.SignInAsync(cancellation.Token);
            if (session is not null)
                await ActivateSteamSessionAsync(session, save: true, cancellation.Token);
            else
                SteamStatus = LocalizationService.Instance["SteamSignInCanceled"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SteamStatus = LocalizationService.Instance["SteamSignInCanceled"];
        }
        catch (Exception exception) when (IsAccountError(exception))
        {
            SteamStatus = LocalizationService.Instance["SteamSignInFailed"];
        }
        finally
        {
            _steamSignIn = null;
            OnPropertyChanged(nameof(IsSteamSignInPending));
            IsSteamBusy = false;
        }
    }

    [RelayCommand]
    private void CancelSteamSignIn() => _steamSignIn?.Cancel();

    [RelayCommand]
    private async Task SignOutSteamAsync()
    {
        if (IsSteamBusy || _desktopSession is not { } session)
            return;

        IsSteamBusy = true;
        SteamStatus = null;
        _desktopSession = null;
        SteamUser = null;
        ClearSavedSession();
        _ = ReloadDiscussionAsync();
        try
        {
            await _desktopAccount.SignOutAsync(session, CancellationToken.None);
        }
        catch (Exception exception) when (IsAccountError(exception))
        {
            SteamStatus = LocalizationService.Instance["SteamSignOutUnconfirmed"];
        }
        finally
        {
            IsSteamBusy = false;
        }
    }

    private async Task ActivateSteamSessionAsync(DesktopSession session, bool save, CancellationToken cancellation)
    {
        var account = await _desktopAccount.CurrentUserAsync(session, cancellation);
        SteamStatus = null;
        if (save)
        {
            try { _desktopAccount.Store.Save(session); }
            catch (Exception exception) when (IsAccountError(exception))
            {
                SteamStatus = LocalizationService.Instance["SteamSaveFailed"];
            }
        }

        _desktopSession = session;
        SteamUser = account;
        if (DiscussionWriteStatus == LocalizationService.Instance["SteamSessionExpired"])
            DiscussionWriteStatus = null;
        _ = ReloadDiscussionAsync();
    }

    private void ExpireSteamSession(DesktopSession session)
    {
        if (!ReferenceEquals(_desktopSession, session))
            return;

        _desktopSession = null;
        SteamUser = null;
        SteamStatus = LocalizationService.Instance["SteamSessionExpired"];
        ClearSavedSession();
        _ = ReloadDiscussionAsync();
    }

    private void ClearSavedSession()
    {
        try { _desktopAccount.Store.Clear(); }
        catch (Exception exception) when (IsAccountError(exception))
        {
            SteamStatus = LocalizationService.Instance["SteamClearFailed"];
        }
    }

    private static bool IsAccountError(Exception exception) => exception is HttpRequestException or IOException or JsonException or
        CryptographicException or UnauthorizedAccessException or Win32Exception or SocketException or OperationCanceledException;
}
