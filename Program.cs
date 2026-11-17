using Avalonia;
using System;

using System.Runtime.Versioning;
using System_Resource_Manager.Services;

[assembly: SupportedOSPlatform("linux")]

namespace System_Resource_Manager;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--worker", var socketPath]) return WorkerHost.Run(socketPath);
        if (args is ["--demo"]) return LabDemo.RunAsync().GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
