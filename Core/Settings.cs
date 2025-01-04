using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PACT.Core;

public class SettingsManager
{
    private readonly Dictionary<string, object> _yamlCache = new();
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly string _settingsPath;
    private readonly string _dataPath;

    public SettingsManager(string settingsPath = "PACT Settings.yaml", string dataPath = "PACT Data/PACT Main.yaml")
    {
        // Convert relative paths to absolute paths based on executable location
        var baseDir = AppContext.BaseDirectory;
        _settingsPath = Path.Combine(baseDir, settingsPath);
        _dataPath = Path.Combine(baseDir, dataPath);

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        InitializeSettingsFile();
    }

    private void InitializeSettingsFile()
    {
        if (!File.Exists(_settingsPath))
        {
            try
            {
                // Try to get defaults from data file first
                var defaultSettings = GetYamlValue(_dataPath, "PACT_Data.default_settings");
                if (defaultSettings != null)
                {
                    File.WriteAllText(_settingsPath, defaultSettings.ToString());
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load default settings from data file: {ex.Message}");
                Console.WriteLine("Falling back to basic default settings.");
            }

            // Create basic default settings if data file loading failed
            var basicDefaults = new Dictionary<string, object>
            {
                ["PACT_Settings"] = new Dictionary<string, object>
                {
                    ["Cleaning Timeout"] = 300,
                    ["Journal Expiration"] = 7,
                    ["LoadOrder TXT"] = "",
                    ["XEDIT EXE"] = "",
                    ["Update Check"] = true
                }
            };

            var yaml = _serializer.Serialize(basicDefaults);
            File.WriteAllText(_settingsPath, yaml);
        }
    }

    public T? GetSetting<T>(string key)
    {
        try
        {
            var setting = GetYamlValue(_settingsPath, $"PACT_Settings.{key}");
            if (setting == null && !key.Contains("Path", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"ERROR (pact_settings)! Trying to grab a null value for: '{key}'");
            }
            return setting is T value ? value : default;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving setting {key}: {ex.Message}");
            return default;
        }
    }

    public async Task UpdateSettingAsync<T>(string key, T value)
    {
        try
        {
            var yaml = await File.ReadAllTextAsync(_settingsPath);
            var settings = _deserializer.Deserialize<Dictionary<string, object>>(yaml);

            // Navigate to the correct nested location
            var current = settings;
            var parts = key.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!current.ContainsKey(parts[i]))
                {
                    current[parts[i]] = new Dictionary<string, object>();
                }
                current = (Dictionary<string, object>)current[parts[i]];
            }

            current[parts[^1]] = value!;

            // Serialize back to YAML
            var updatedYaml = _serializer.Serialize(settings);
            await File.WriteAllTextAsync(_settingsPath, updatedYaml);

            // Update cache
            _yamlCache[_settingsPath] = settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating setting {key}: {ex.Message}");
            throw;
        }
    }

    private object? GetYamlValue(string path, string keyPath)
    {
        if (!_yamlCache.ContainsKey(path))
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"YAML file not found: {path}");
            }

            var yaml = File.ReadAllText(path);
            _yamlCache[path] = _deserializer.Deserialize<Dictionary<string, object>>(yaml);
        }

        var data = _yamlCache[path];
        var keys = keyPath.Split('.');
        var value = data;

        foreach (var key in keys)
        {
            if (value is Dictionary<string, object> dict && dict.TryGetValue(key, out var value1))
            {
                value = value1;
            }
            else
            {
                return null;
            }
        }

        return value;
    }

    public async Task InitializeInfoAsync(PactInfo info)
    {
        // Load and set all settings asynchronously
        var tasks = new[]
        {
            Task.Run(() => (object?)GetSetting<string>("LoadOrder TXT")),
            Task.Run(() => (object?)GetSetting<string>("XEDIT EXE")),
            Task.Run(() => (object?)GetSetting<int>("Cleaning Timeout")),
            Task.Run(() => (object?)GetSetting<int>("Journal Expiration"))
        };

        // Wait for all settings to be loaded
        await Task.WhenAll(tasks);

        var loadOrderPath = await tasks[0] as string;
        var xEditPath = await tasks[1] as string;
        var cleaningTimeout = await tasks[2] is int timeout ? timeout : 0;
        var journalExpiration = await tasks[3] is int expiration ? expiration : 0;

        if (!string.IsNullOrEmpty(loadOrderPath))
        {
            info.UpdateLoadOrderPath(loadOrderPath);
        }

        if (!string.IsNullOrEmpty(xEditPath))
        {
            info.UpdateXEditPaths(xEditPath);
        }

        if (cleaningTimeout > 0)
        {
            info.CleaningTimeout = cleaningTimeout;
        }

        if (journalExpiration > 0)
        {
            info.JournalExpiration = journalExpiration;
        }

        await ValidateSettingsAsync(info);
    }

    private async Task ValidateSettingsAsync(PactInfo info)
    {
        var validationTasks = new List<Task>();

        if (info.CleaningTimeout <= 0)
        {
            validationTasks.Add(Task.FromException(new InvalidOperationException(
                "ERROR: CLEANING TIMEOUT VALUE IN PACT SETTINGS IS NOT VALID.\n" +
                "Please change Cleaning Timeout to a valid positive number.")));
        }

        if (info.CleaningTimeout < 30)
        {
            validationTasks.Add(Task.FromException(new InvalidOperationException(
                "ERROR: CLEANING TIMEOUT VALUE IN PACT SETTINGS IS TOO SMALL.\n" +
                "Cleaning Timeout must be set to at least 30 seconds or more.")));
        }

        if (info.JournalExpiration <= 0)
        {
            validationTasks.Add(Task.FromException(new InvalidOperationException(
                "ERROR: JOURNAL EXPIRATION VALUE IN PACT SETTINGS IS NOT VALID.\n" +
                "Please change Journal Expiration to a valid positive number.")));
        }

        if (info.JournalExpiration < 1)
        {
            validationTasks.Add(Task.FromException(new InvalidOperationException(
                "ERROR: JOURNAL EXPIRATION VALUE IN PACT SETTINGS IS TOO SMALL.\n" +
                "Journal Expiration must be set to at least 1 day or more.")));
        }

        await Task.WhenAll(validationTasks);
    }
}