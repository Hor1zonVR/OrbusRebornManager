using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OrbusRebornManager;

public sealed class PackagePreviewRow
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Status { get; set; } = "";
    public Brush StatusBrush { get; set; } = Brushes.LightGray;
}

public partial class ImportPackagePreviewWindow : Window
{
    public ObservableCollection<PackagePreviewRow> PackageRows { get; } = new();

    public ImportPackagePreviewWindow(
        ImportedInstancePackage package, CatalogDocument catalog)
    {
        InitializeComponent();
        DataContext = this;

        InstanceNameLabel.Text = package.Name;
        PackageLabel.Text = package.Filename;

        int approvedCount = 0;
        foreach (var mod in package.Mods)
        {
            bool local = mod.Manual;
            bool approved = InstancePackageImporter.ApprovedMod(mod, catalog) != null;
            if (approved) approvedCount++;

            PackageRows.Add(new PackagePreviewRow
            {
                Name = mod.Name,
                Detail = mod.Version + (mod.Enabled ? "  ·  Enabled" : "  ·  Disabled"),
                Status = local ? "Manual DLL" : approved ? "Approved" : "Unavailable",
                StatusBrush = approved
                    ? new SolidColorBrush(Color.FromRgb(111, 219, 171))
                    : new SolidColorBrush(Color.FromRgb(241, 179, 111))
            });
        }

        SummaryLabel.Text = package.Mods.Count + " mod(s) in package  ·  " +
            approvedCount + " approved for download  ·  " +
            (package.Mods.Count - approvedCount) + " manual/unavailable";
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
