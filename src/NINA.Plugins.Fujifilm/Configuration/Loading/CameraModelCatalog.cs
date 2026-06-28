using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.Json;
using NINA.Core.Utility;

namespace NINA.Plugins.Fujifilm.Configuration.Loading;

[Export(typeof(ICameraModelCatalog))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class CameraModelCatalog : ICameraModelCatalog
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private IReadOnlyList<CameraConfig> _configs = Array.Empty<CameraConfig>();
    private Dictionary<string, CameraConfig> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _configDirectory;

    public CameraModelCatalog() : this(ResolveConfigDirectory())
    {
    }

    internal CameraModelCatalog(string configDirectory)
    {
        _configDirectory = configDirectory;
        Reload();
    }

    public CameraConfig? TryGetByProductName(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        lock (_sync)
        {
            _lookup.TryGetValue(productName.Trim(), out var config);
            if (config != null)
            {
                return config;
            }

            var sanitized = SanitizeModelName(productName);
            if (_lookup.TryGetValue(sanitized, out config))
            {
                return config;
            }

            // SDK product strings commonly include a FUJIFILM vendor prefix. Match the
            // longest model suffix so names such as X-H2S cannot be mistaken for X-H2.
            return CameraModelRules.FindBestMatch(_configs, productName);
        }
    }

    public IReadOnlyList<CameraConfig> GetAll()
    {
        lock (_sync)
        {
            return _configs;
        }
    }

    public void Reload()
    {
        lock (_sync)
        {
            var directory = _configDirectory;
            if (!Directory.Exists(directory))
            {
                Logger.Error($"Fujifilm camera configuration directory is missing: {directory}");
                _configs = Array.Empty<CameraConfig>();
                _lookup = new Dictionary<string, CameraConfig>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            var configs = new List<CameraConfig>(files.Length);
            var lookup = new Dictionary<string, CameraConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var config = JsonSerializer.Deserialize<CameraConfig>(stream, _serializerOptions);
                    if (config == null || string.IsNullOrWhiteSpace(config.ModelName))
                    {
                        Logger.Error($"Ignoring Fujifilm camera configuration without a model name: {file}");
                        continue;
                    }

                    if (!CameraModelRules.IsValid(config))
                    {
                        Logger.Error($"Ignoring invalid Fujifilm camera configuration '{file}': sensor and exposure values must be positive and ordered.");
                        continue;
                    }

                    configs.Add(config);
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        config.ModelName,
                        SanitizeModelName(config.ModelName)
                    };

                    foreach (var key in keys)
                    {
                        lookup[key] = config;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load Fujifilm camera configuration '{file}': {ex}");
                }
            }

            _configs = configs;
            _lookup = lookup;
        }
    }

    private static string ResolveConfigDirectory()
    {
        // AppContext.BaseDirectory returns NINA's exe directory, not the plugin directory.
        // We need to use the plugin assembly's location instead.
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDir, "Configuration", "Assets", "CameraConfigs");
    }

    private static string SanitizeModelName(string name)
    {
        return CameraModelRules.NormalizeName(name);
    }
}
