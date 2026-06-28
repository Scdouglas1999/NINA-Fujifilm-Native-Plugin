using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Core.Utility;

namespace NINA.Plugins.Fujifilm.Settings;

[Export(typeof(IFujiSettingsProvider))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujiSettingsProvider : IFujiSettingsProvider
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA",
        "Plugins",
        "Fujifilm",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [ImportingConstructor]
    public FujiSettingsProvider()
    {
        Settings = LoadSettings();
    }

    public FujiSettings Settings { get; }

    public void Save()
    {
        try
        {
            Settings.Normalize();
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save Fujifilm plugin settings: {ex}");
        }
    }

    private static FujiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<FujiSettings>(json, JsonOptions);
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load Fujifilm plugin settings; defaults will be used: {ex}");
        }

        // Return default settings if loading failed or file doesn't exist
        return new FujiSettings();
    }
}
