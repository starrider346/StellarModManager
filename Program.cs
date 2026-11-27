using System;
using Avalonia;
using StellarModManager.Models;
using StellarModManager.Services;

namespace StellarModManager;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        using var instance = WindowsAppInstance.OwnOrForward(ReleaseInstallLink.FromArguments(args), out var forwarded);
        if (instance == null)
            return forwarded ? 0 : 1;

        return BuildAvaloniaApp(instance).StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(WindowsAppInstance? instance)
        => AppBuilder.Configure(() => new App { WindowsInstance = instance })
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
