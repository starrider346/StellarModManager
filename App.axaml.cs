using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StellarModManager.Models;
using StellarModManager.Services;
using StellarModManager.ViewModels;
using StellarModManager.Views;

namespace StellarModManager;

public partial class App : Application
{
    internal WindowsAppInstance? WindowsInstance { get; init; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            var startupUrl = ReleaseInstallLink.FromArguments(desktop.Args);
            var opened = false;
            window.Opened += (_, _) =>
            {
                opened = true;
                if (startupUrl is { } url)
                {
                    startupUrl = null;
                    Activate(url);
                }
            };

            if (OperatingSystem.IsMacOS() && this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, args) =>
                {
                    if (args is ProtocolActivatedEventArgs { Uri: { } uri } && ReleaseInstallLink.IsInstallScheme(uri.OriginalString))
                        Dispatcher.UIThread.Post(() => Activate(uri.OriginalString));
                };
            }

            if (OperatingSystem.IsWindows())
                WindowsInstance?.Listen(url => Dispatcher.UIThread.Post(() => Activate(url)));

            void Activate(string? url)
            {
                if (!opened)
                {
                    startupUrl = url ?? startupUrl;
                    return;
                }

                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                if (url is not null)
                    viewModel.ReceiveInstallLink(url);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
