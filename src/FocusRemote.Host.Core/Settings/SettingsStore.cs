using System.Text.Json;
using FocusRemote.Host.Core.Settings.Models;

namespace FocusRemote.Host.Core.Settings;

/// <summary>Loads/saves <see cref="HostSettings"/> as JSON at a fixed path (atomic replace on save).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _filePath;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    /// <summary>Returns defaults when the file is missing or unreadable.</summary>
    public HostSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new HostSettings();
            return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(_filePath), _jsonOptions) ?? new HostSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new HostSettings();
        }
    }

    public void Save(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }
}
