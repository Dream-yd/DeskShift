using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;
using System.IO;

namespace MeetingDesktop.App.Services;

internal sealed record StartupFailure(string Detail, LanguagePreference Language);

internal sealed record StartupData(AppPaths Paths, SettingsStore SettingsStore, AppSettings Settings,
    SessionStore Sessions, LayoutStore Layouts)
{
    public static StartupData? Open(string[] args, out StartupFailure? failure)
    {
        failure = null;
        AppSettings settings = new();
        try
        {
            AppPaths paths = AppPaths.Resolve(args);
            var settingsStore = new SettingsStore(paths.SettingsFilePath);
            settings = settingsStore.LoadForStartup();
            paths.EnsureCreated();
            return new(paths, settingsStore, settings, new SessionStore(paths.SessionFilePath),
                new LayoutStore(paths.LayoutsDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            failure = new(ex.Message, settings.Language);
            return null;
        }
    }
}
