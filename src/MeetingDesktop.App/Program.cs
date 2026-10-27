using System.Runtime.CompilerServices;
using System.Windows;
using MeetingDesktop.Core;

namespace MeetingDesktop.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Helpers need no Application, theme resources, settings, tray or windows.
        if (Services.IconTransitionWorker.TryRun(args, out int visualCode)) return visualCode;
        if (ElevatedSession.TryRun(args, out int code)) return code;
        if (DesktopShellWorker.TryRun(args, out code)) return code;
        if (ElevatedOperations.TryRun(args, out code)) return code;
        return RunWindow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWindow()
    {
        var application = new App();
        // Equivalent to generated InitializeComponent, available in both WPF
        // compilation passes where App.g.cs may not have been generated yet.
        Application.LoadComponent(application, new Uri("/MeetingDesktop;component/App.xaml", UriKind.Relative));
        return application.Run();
    }
}
