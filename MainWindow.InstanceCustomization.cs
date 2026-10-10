using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private void SelectedInstanceOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: not null } button)
            return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void InstanceCustomize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstanceCard card })
            CustomizeInstance(card.Instance.Path);
    }

    private void EditSelectedInstance_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.GamePath))
            return;
        CustomizeInstance(_settings.GamePath);
    }

    private void CustomizeInstance(string gamePath)
    {
        try
        {
            var instances = _instanceStore.Load();
            var instance = instances.FirstOrDefault(x =>
                string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(gamePath),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("This instance is not registered.");

            var editor = new EditInstanceWindow(instance) { Owner = this };
            if (editor.ShowDialog() != true)
                return;

            if (instances.Any(x =>
                !string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(gamePath),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Name, editor.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this,
                    "Another instance already uses that display name. Choose a different one.",
                    "Duplicate instance name", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Keep the game path untouched. Store a re-encoded copy of
            // the chosen artwork under the manager's own AppData.
            string iconPath = instance.CustomIconPath;
            if (editor.ResetImage)
                iconPath = "";
            if (!string.IsNullOrWhiteSpace(editor.NewImagePath))
                iconPath = InstanceArtwork.Save(editor.NewImagePath, instance.Path);

            instance.Name = editor.DisplayName;
            instance.CustomIconPath = iconPath;
            instance.IconFit = editor.IconFit;
            instance.IconZoom = editor.IconZoom;
            instance.IconOffsetX = editor.IconOffsetX;
            instance.IconOffsetY = editor.IconOffsetY;
            instance.IconHasCrop = editor.IconHasCrop;
            instance.IconCropX = editor.IconCrop.X;
            instance.IconCropY = editor.IconCrop.Y;
            instance.IconCropWidth = editor.IconCrop.Width;
            instance.IconCropHeight = editor.IconCrop.Height;
            _instanceStore.Save(instances);

            RefreshInstanceCards();
            if (InstalledPanel.Visibility == Visibility.Visible)
                UpdateInstalledRows();
            Log("Updated " + instance.Name + ". Game files were not moved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not customize instance",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstanceExport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstanceCard card })
            ExportInstanceMods(card.Instance.Path);
    }

    private void ExportSelectedInstance_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.GamePath))
            return;
        ExportInstanceMods(_settings.GamePath);
    }

    private void ExportInstanceMods(string gamePath)
    {
        try
        {
            var instance = _instanceStore.Load().FirstOrDefault(x =>
                string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(gamePath),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The instance is not registered.");

            string valid = _service.NormalizeGamePath(instance.Path);
            InstanceDocument installed = _service.LoadInstalled(valid);
            string safeFilename = Regex.Replace(instance.Name, @"[^a-zA-Z0-9 _-]", "")
                .Trim();
            if (safeFilename.Length == 0)
                safeFilename = "Orbus Instance";

            var picker = new SaveFileDialog
            {
                Title = "Export an Orbus mod list",
                FileName = safeFilename + ".orbuspack",
                DefaultExt = ".orbuspack",
                AddExtension = true,
                Filter = "Orbus mod list (*.orbuspack)|*.orbuspack|JSON file (*.json)|*.json",
                OverwritePrompt = true
            };
            if (picker.ShowDialog(this) != true)
                return;

            InstancePackageExporter.Export(picker.FileName, instance.Name, installed);
            Log("Exported mod list for " + instance.Name + ".");
            MessageBox.Show(this,
                "Exported a shareable mod list containing " + installed.Mods.Count +
                " managed mod(s).\n\n" +
                "The file does not contain game files, DLLs, local paths or your settings. " +
                "Local test mods are listed as manual requirements.\n\n" +
                "Use Library > Import .orbuspack to create a separate game copy with these approved mods. " +
                "Local DLLs and unavailable pinned releases must be added manually.",
                "Mod list exported", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not export mod list",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
