using System.Text.Json;
using Dalamud.Plugin;

namespace GlamourTracker.Services;

/// <summary>
/// Detects HaselTweaks' Glamour Dresser Alert so we can stand down our armoire panel.
/// </summary>
internal static class HaselTweaksGate
{
    public const string PluginInternalName = "HaselTweaks";
    public const string GlamourDresserAlertTweak = "GlamourDresserAlert";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private static bool cachedAlertEnabled;
    private static DateTime cacheUtc = DateTime.MinValue;

    /// <summary>
    /// True when HaselTweaks is loaded and Glamour Dresser Alert is in EnabledTweaks.
    /// </summary>
    public static bool IsGlamourDresserAlertEnabled(IDalamudPluginInterface pluginInterface)
    {
        var now = DateTime.UtcNow;
        if ((now - cacheUtc) < CacheTtl)
            return cachedAlertEnabled;

        cachedAlertEnabled = false;
        if (!IsPluginLoaded(pluginInterface))
        {
            cacheUtc = now;
            return false;
        }

        try
        {
            var configPath = Path.Combine(
                pluginInterface.ConfigFile.DirectoryName ?? string.Empty,
                "HaselTweaks.json");
            if (!File.Exists(configPath))
            {
                cacheUtc = now;
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("EnabledTweaks", out var tweaks)
                || tweaks.ValueKind != JsonValueKind.Array)
            {
                cacheUtc = now;
                return false;
            }

            foreach (var entry in tweaks.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                if (entry.GetString()?.Equals(GlamourDresserAlertTweak, StringComparison.OrdinalIgnoreCase) == true)
                {
                    cachedAlertEnabled = true;
                    break;
                }
            }
        }
        catch (Exception)
        {
            cachedAlertEnabled = false;
        }

        cacheUtc = now;
        return cachedAlertEnabled;
    }

    private static bool IsPluginLoaded(IDalamudPluginInterface pluginInterface)
    {
        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (!plugin.InternalName.Equals(PluginInternalName, StringComparison.OrdinalIgnoreCase))
                continue;
            return plugin.IsLoaded;
        }

        return false;
    }
}
