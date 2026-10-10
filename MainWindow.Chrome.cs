using System;
using System.Windows;
using System.Windows.Input;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private void ChromeTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount == 2)
        {
            ToggleChromeMaximize();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Mouse released while Windows was beginning a window drag.
        }
    }

    private void Chrome_Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Chrome_Maximize_Click(object sender, RoutedEventArgs e) =>
        ToggleChromeMaximize();

    private void ToggleChromeMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized
            ? "\uE923" : "\uE922";
    }

    private void Chrome_Close_Click(object sender, RoutedEventArgs e) => Close();
}
