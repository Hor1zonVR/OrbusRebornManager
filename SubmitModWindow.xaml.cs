using System;
using System.Windows;
using System.Windows.Input;

namespace OrbusRebornManager;

/// <summary>
/// Visible, copy-friendly mod submission contact. No Discord token,
/// app connection or external automation is required.
/// </summary>
public partial class SubmitModWindow : Window
{
    private const string MaintainerDiscord = "horizonvr";

    public SubmitModWindow()
    {
        InitializeComponent();
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void CopyUsername_Click(object sender, RoutedEventArgs e)
    {
        Copy(MaintainerDiscord, "Discord username copied.");
    }

    private void CopyTemplate_Click(object sender, RoutedEventArgs e)
    {
        Copy(
            "Hey! I'd like to submit a mod for RebornManager.\r\n" +
            "Mod name: \r\n" +
            "GitHub repository: \r\n" +
            "Latest stable release: \r\n" +
            "Client or server: \r\n" +
            "What it does: ",
            "Submission message copied.");
    }

    private void Copy(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            CopyStatusText.Text = message;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Could not access the Windows clipboard: " + ex.Message,
                "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
