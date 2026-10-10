using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private async void ImportInstance_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Import a shared OrbusVR instance mod list",
            Filter = "Orbus mod list (*.orbuspack)|*.orbuspack",
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
            return;

        try
        {
            var package = InstancePackageImporter.Read(picker.FileName);

            // The importer must not resolve or download mods from arbitrary
            // GitHub repositories listed in an untrusted .orbuspack file.
            if (_catalog.Mods.Count == 0)
            {
                var refreshed = await _service.FetchCatalogAsync(
                    _settings.CatalogUrl);
                _catalog = refreshed.Catalog;
            }

            var preview = new ImportPackagePreviewWindow(package, _catalog)
            {
                Owner = this
            };
            if (preview.ShowDialog() != true)
                return;

            if (_service.IsGameRunning())
            {
                MessageBox.Show(this,
                    "Close OrbusVR before importing an instance.",
                    "Close the game", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var existing = _instanceStore.Load();
            string source = existing.FirstOrDefault(i => !i.CreatedByManager)?.Path
                ?? _settings.GamePath;

            // Reuse the same safe copy, source selection and storage UI used
            // for ordinary instance creation. Loader is mandatory here.
            var create = new CreateInstanceWindow(
                _service, source, _settings.DefaultInstanceDirectory,
                suggestedName: package.Name,
                importModList: true)
            {
                Owner = this
            };

            if (create.ShowDialog() != true || create.CreatedInstance == null)
                return;

            GameInstance instance = create.CreatedInstance;

            // The create wizard registers the game copy before returning.
            // Keep it even if network or mod installation fails.
            if (create.RememberLocation)
            {
                _settings.DefaultInstanceDirectory = create.SelectedStoragePath;
                DefaultInstanceFolderText.Text = _settings.DefaultInstanceDirectory;
            }

            _settings.GamePath = instance.Path;
            _service.SaveSettings(_settings);
            RefreshInstanceCards();
            UpdateDashboard();

            await RunAsync(async () =>
            {
                await RestoreImportedModsAsync(instance, package);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not import instance",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RestoreImportedModsAsync(
        GameInstance instance, ImportedInstancePackage package)
    {
        int installed = 0;
        var skipped = new List<string>();

        try
        {
            Log("Preparing BepInEx in " + instance.Name + "...");
            if (!_service.IsBepInExInstalled(instance.Path))
            {
                await _service.InstallLoaderAsync(instance.Path,
                    message => Dispatcher.Invoke(() => Log(message)));
            }
        }
        catch (Exception ex)
        {
            RefreshInstanceCards();
            UpdateDashboard();

            MessageBox.Show(this,
                "The new instance was created, but BepInEx could not be installed:\n\n" +
                ex.Message + "\n\nYour new game copy is still saved in the Library. " +
                "Nothing was changed in any existing instance.",
                "Import setup incomplete", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        foreach (ImportedPackageMod item in package.Mods)
        {
            ModDefinition? mod = InstancePackageImporter.ApprovedMod(item, _catalog);

            if (mod == null)
            {
                string reason = item.Manual ? "local DLL (manual install required)" :
                    "not approved, repository mismatch, or unsupported version";
                skipped.Add(item.Name + ": " + reason);
                continue;
            }

            try
            {
                Log("Restoring " + item.Name + " " + item.Version + "...");
                var (release, asset) =
                    await _service.GetPinnedReleaseAsync(mod, item.Version);

                await _service.InstallReleaseAsync(
                    instance.Path, mod, release, asset,
                    message => Dispatcher.Invoke(() => Log(message)));

                if (!item.Enabled)
                    _service.SetModEnabled(instance.Path, mod.Id, false);

                installed++;
            }
            catch (Exception ex)
            {
                skipped.Add(item.Name + " " + item.Version + ": " + ex.Message);
                Log("Skipped " + item.Name + ": " + ex.Message);
            }
        }

        RefreshInstanceCards();
        UpdateDashboard();
        UpdateReleaseStatuses();
        UpdateInstalledRows();

        var registered = _instanceStore.Load().FirstOrDefault(x =>
            string.Equals(
                Path.GetFullPath(x.Path),
                Path.GetFullPath(instance.Path),
                StringComparison.OrdinalIgnoreCase));
        if (registered != null)
            OpenInstance(new InstanceCard(registered, "", "", true));

        Log("Imported " + instance.Name + ": " + installed +
            " mod(s) installed, " + skipped.Count + " skipped.");

        string warning = skipped.Count == 0
            ? ""
            : "\n\nCould not restore automatically:\n" +
              string.Join("\n", skipped.Take(9).Select(x => "• " + x)) +
              (skipped.Count > 9
                  ? "\n…plus " + (skipped.Count - 9) + " more."
                  : "");

        MessageBox.Show(this,
            "Created a brand-new instance: " + instance.Name +
            "\n\nInstalled " + installed + " of " + package.Mods.Count +
            " mod(s)." +
            (skipped.Count == 0
                ? "\nAll exported mods restored successfully."
                : "\nSkipped " + skipped.Count + " mod(s).") +
            warning +
            "\n\nLaunch this copy of OrbusVR once so BepInEx can generate its interop files. " +
            "The original game and other instances were not modified." +
            "\n\nYour regular automatic-mod-update setting still applies on future launches.",
            skipped.Count == 0 ? "Instance imported" : "Import completed with skips",
            MessageBoxButton.OK,
            skipped.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
