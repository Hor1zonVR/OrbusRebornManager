namespace OrbusRebornManager;

/// <summary>
/// Friendly names for RM presentation only. Plugin GUIDs, repository names,
/// release asset patterns and installed registry records are never rewritten.
/// </summary>
public static class ModPresentation
{
    public static string Name(string? modId, string? publishedName)
    {
        return modId?.Trim().ToLowerInvariant() switch
        {
            "orbusreborncamera" => "RebornCam",
            "orbusquesthelper" => "QuestHelper",
            "orbusgoblintimer" => "GoblinTimer",
            "orbusrebornmenu" => "RebornMenu",
            "orbusrebornautomount" => "AutoMount",
            "castmetrics" => "CastMetrics",
            "orbusabilitylabels" => "AbilityLabels",
            _ => string.IsNullOrWhiteSpace(publishedName) ? "Unnamed Mod" : publishedName.Trim()
        };
    }
}
