
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace OrbusRebornManager;

public sealed class ManagerSettings
{
    public string GamePath { get; set; } = "";

    public string CatalogUrl { get; set; } =
        "https://raw.githubusercontent.com/Hor1zonVR/OrbusRebornManager/main/catalog/mods.json";

    public bool AutoUpdate { get; set; } = true;
}

public sealed class CatalogDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ModDefinition> Mods { get; set; } = new();
}

public sealed class ModDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Repository { get; set; } = "";
    public string AssetPattern { get; set; } = "*.dll";
    public string Target { get; set; } = "client";
    public string Category { get; set; } = "Other";
}

public sealed class InstalledMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Version { get; set; } = "";
    public string AssetName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Local { get; set; }
}

public sealed class InstanceDocument
{
    public List<InstalledMod> Mods { get; set; } = new();
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

public sealed class ModRow : INotifyPropertyChanged
{
    public ModDefinition Definition { get; }

    private string _status = "Checking releases...";
    private string _action = "Install";
    private string _version = "";
    private bool _working;

    public string Name => Definition.Name;
    public string Author => Definition.Author;
    public string Description => Definition.Description;
    public string Repository => Definition.Repository;

    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    public string Action
    {
        get => _action;
        set { _action = value; OnChanged(); }
    }

    public string Version
    {
        get => _version;
        set { _version = value; OnChanged(); }
    }

    public bool Working
    {
        get => _working;
        set
        {
            _working = value;
            OnChanged();
            OnChanged(nameof(CanClick));
        }
    }

    public bool CanClick => !_working;

    public GitHubRelease? Release { get; set; }
    public GitHubAsset? Asset { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(
        [CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(property));
    }

    public ModRow(ModDefinition definition)
    {
        Definition = definition;
    }
}
