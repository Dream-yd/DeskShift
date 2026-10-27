using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MeetingDesktop.App.Services;

/// <summary>Supported DWM Desktop Acrylic with an opaque WPF fallback.</summary>
internal static class NativeBackdrop
{
    public static void Attach(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source?.CompositionTarget is null) return;

        void Refresh()
        {
            ApplyContrast(window);
            int dark = ThemeManager.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int));
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                window.SetResourceReference(Window.BackgroundProperty, "Brush.Window");
                return;
            }
            // Keep ordinary WPF window chrome and hit testing. No layered window,
            // undocumented composition APIs, or image blur is involved.
            bool enabled = !SystemParameters.HighContrast && TransparencyEnabled();
            int material = enabled ? 3 : 1; // DWMSBT_TRANSIENTWINDOW / DWMSBT_NONE
            int result = DwmSetWindowAttribute(source.Handle, 38, ref material, sizeof(int));
            var margins = new Margins { Left = enabled && result >= 0 ? -1 : 0 };
            int frameResult = DwmExtendFrameIntoClientArea(source.Handle, ref margins);
            if (enabled && result >= 0 && frameResult >= 0)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                window.Background = Brushes.Transparent;
            }
            else
            {
                material = 1;
                _ = DwmSetWindowAttribute(source.Handle, 38, ref material, sizeof(int));
                margins = new Margins();
                _ = DwmExtendFrameIntoClientArea(source.Handle, ref margins);
                source.CompositionTarget.BackgroundColor = ((SolidColorBrush)window.FindResource("Brush.Window")).Color;
                window.SetResourceReference(Window.BackgroundProperty, "Brush.Window");
            }
        }

        nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            // Reapply after system appearance, accessibility, or composition changes.
            if (message is 0x001A or 0x031A or 0x031E)
            {
                ThemeManager.RefreshSystem();
                Refresh();
            }
            return 0;
        }

        source.AddHook(OnMessage);
        void OnThemeChanged(object? sender, EventArgs e) => Refresh();
        ThemeManager.Changed += OnThemeChanged;
        window.Closed += (_, _) =>
        {
            source.RemoveHook(OnMessage);
            ThemeManager.Changed -= OnThemeChanged;
        };
        Refresh();
    }

    private static bool TransparencyEnabled()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return settings?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.IO.IOException) { return false; }
    }

    private static void ApplyContrast(Window window)
    {
        string[] surfaces = ["CanvasTint", "Window", "Panel", "Card", "Sunken", "Input", "DangerSurface"];
        string[] text = ["Text.Primary", "Text.Strong", "Text.Secondary", "Text.Muted", "SelectionText", "Danger", "Success", "Warning"];
        foreach (string role in surfaces.Concat(text).Concat(
            ["Hairline", "Primary", "PrimaryHover", "PrimaryPressed", "Accent", "CardSelected", "CardHover", "OnPrimary"]))
        {
            string key = "Brush." + role;
            if (!SystemParameters.HighContrast) { window.Resources.Remove(key); continue; }
            window.Resources[key] = role switch
            {
                "Primary" or "PrimaryHover" or "PrimaryPressed" or "Accent" => SystemColors.WindowTextBrush,
                "OnPrimary" => SystemColors.WindowBrush,
                "CardSelected" or "CardHover" => SystemColors.ControlBrush,
                "Hairline" => SystemColors.WindowTextBrush,
                _ => surfaces.Contains(role) ? SystemColors.WindowBrush : SystemColors.WindowTextBrush,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
