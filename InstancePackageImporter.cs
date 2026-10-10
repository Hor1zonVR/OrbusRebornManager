using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrbusRebornManager;

/// <summary>
/// Reads a shareable .orbuspack manifest. This format is only metadata:
/// never execute anything or extract arbitrary user-supplied files.
/// </summary>
public static class InstancePackageImporter
{
    private const string ExpectedFormat = "orbus-reborn-mod-list";
    private const string ExpectedGame = "OrbusVR Reborn Community Edition";
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxMods = 128;

    public static ImportedInstancePackage Read(string filename)
    {
        var file = new FileInfo(filename);
        if (!file.Exists || file.Length > MaxFileBytes)
            throw new InvalidOperationException(
                "Choose an existing .orbuspack file smaller than 1 MB.");

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(filename),
            new JsonDocumentOptions { MaxDepth = 12 });

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            ReadString(root, "format") != ExpectedFormat ||
            ReadString(root, "game") != ExpectedGame ||
            !root.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out int version) || version != 1)
            throw new InvalidOperationException(
                "This isn't a supported OrbusVR Reborn .orbuspack mod list.");

        if (!root.TryGetProperty("mods", out JsonElement mods) ||
            mods.ValueKind != JsonValueKind.Array ||
            mods.GetArrayLength() > MaxMods)
            throw new InvalidOperationException(
                "The mod list is missing or contains too many entries.");

        string name = ReadString(root, "name").Trim();
        if (name.Length > 90)
            throw new InvalidOperationException("The exported instance name is too long.");

        var package = new ImportedInstancePackage
        {
            Name = name.Length == 0 ? "Imported Orbus" : name,
            Filename = Path.GetFileName(filename)
        };
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in mods.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The mod list contains an invalid entry.");

            string id = ReadString(item, "id");
            string modName = ReadString(item, "name");
            string tag = ReadString(item, "version");
            string repository = ReadString(item, "repository");
            string source = ReadString(item, "source");

            if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,60}$") ||
                !unique.Add(id))
                throw new InvalidOperationException(
                    "The mod list contains an invalid or duplicate mod ID.");

            if (tag.Length > 90 || modName.Length > 120 ||
                repository.Length > 400 || source.Length > 40)
                throw new InvalidOperationException(
                    "A mod entry contains oversized metadata.");

            bool enabled = item.TryGetProperty("enabled", out var flag)
                ? flag.ValueKind == JsonValueKind.True
                    ? true : flag.ValueKind == JsonValueKind.False
                        ? false : throw new InvalidOperationException(
                            "Invalid enabled flag in mod list.")
                : true;

            bool manual = item.TryGetProperty("requiresManualFile", out var manualFlag) &&
                manualFlag.ValueKind == JsonValueKind.True;

            package.Mods.Add(new ImportedPackageMod
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(modName) ? id : modName,
                Repository = repository,
                Version = tag,
                Enabled = enabled,
                Manual = manual || source.Equals("local", StringComparison.OrdinalIgnoreCase)
            });
        }

        return package;
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return "";
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Invalid field in .orbuspack: " + name);
        return element.GetString() ?? "";
    }

    public static ModDefinition? ApprovedMod(
        ImportedPackageMod item, CatalogDocument catalog)
    {
        if (item.Manual || item.Version.Length == 0 ||
            !Regex.IsMatch(item.Version, @"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,79}$"))
            return null;

        ModDefinition? approved = catalog.Mods.FirstOrDefault(x =>
            x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
            !x.Target.Equals("server", StringComparison.OrdinalIgnoreCase));

        if (approved == null)
            return null;

        // The file cannot redirect an approved ID to an unrelated repo.
        try
        {
            var left = ManagerService.GetRepoParts(item.Repository);
            var right = ManagerService.GetRepoParts(approved.Repository);
            if (!left.Owner.Equals(right.Owner, StringComparison.OrdinalIgnoreCase) ||
                !left.Repository.Equals(right.Repository, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch { return null; }

        return approved;
    }
}

public sealed class ImportedInstancePackage
{
    public string Name { get; set; } = "";
    public string Filename { get; set; } = "";
    public List<ImportedPackageMod> Mods { get; } = new();
}

public sealed class ImportedPackageMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Manual { get; set; }
}
