
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OrbusRebornManager;

public partial class MainWindow
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int attributeSize);

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // Windows 10/11: give the native title bar the same dark theme as our WPF UI.
        // Older Windows builds simply fall back to their normal title bar.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            int size = sizeof(int);

            const int darkTitleBar = 20;
            const int olderDarkTitleBar = 19;

            if (DwmSetWindowAttribute(
                hwnd, darkTitleBar, ref enabled, size) != 0)
            {
                DwmSetWindowAttribute(
                    hwnd, olderDarkTitleBar, ref enabled, size);
            }

            // Windows 11 only: request native rounded outer window corners.
            // Windows 10 safely ignores this attribute, keeping resize behaviour.
            int roundedCorners = 2; // DWMWCP_ROUND
            const int cornerPreference = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
            DwmSetWindowAttribute(
                hwnd, cornerPreference, ref roundedCorners, size);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
