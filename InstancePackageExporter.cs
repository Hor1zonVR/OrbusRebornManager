using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OrbusRebornManager;

/// <summary>
/// A shareable, versioned manifest, never a copied game or BepInEx archive.
/// Import/download support can be introduced against this schema later.
/// </summary>
public static class InstancePackageExporter
{
    public static void Export(string filename, string name, InstanceDocument document)
    {
        var manifest = new
        {
            format = "orbus-reborn-mod-list",
            schemaVersion = 1,
            game = "OrbusVR Reborn Community Edition",
            name,
            exportedAtUtc = DateTimeOffset.UtcNow,
            mods = document.Mods
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    id = x.Id,
                    name = x.Name,
                    repository = x.Local ? "" : x.Repository,
                    version = x.Version,
                    enabled = x.Enabled,
                    source = x.Local ? "local" : "catalogue",
                    // Local DLLs are intentionally not included in the export.
                    requiresManualFile = x.Local
                })
                .ToArray()
        };

        // Only public mod metadata goes into the manifest. Never serialize
        // the instance path, local icon path, user settings or DLL contents.
        var json = JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filename, json);
    }
}
