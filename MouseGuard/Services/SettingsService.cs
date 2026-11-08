using System.Text.Json;
using System.Text.Json.Serialization;
using MouseGuard.Models;

namespace MouseGuard.Services;

public sealed class SettingsService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    private readonly object _saveLock = new();
    private bool _loaded;

    public SettingsService()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("MOUSEGUARD_DATA_DIR");
        DataDirectory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseGuard");
        Directory.CreateDirectory(DataDirectory);
        SettingsFile = Path.Combine(DataDirectory, "settings.json");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }
    public string SettingsFile { get; }
    public string LogDirectory { get; }
    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                Settings = new AppSettings();
                Settings.EnsureDefaults();
                return;
            }

            var json = File.ReadAllText(SettingsFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            Settings.EnsureDefaults();
        }
        catch (Exception ex)
        {
            LogService.Error("读取设置失败，已使用默认设置", ex);
            TryBackupBrokenFile();
            Settings = new AppSettings();
            Settings.EnsureDefaults();
        }
        finally
        {
            _loaded = true;
        }
    }

    public void Save()
    {
        if (!_loaded)
            return;

        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                var temp = SettingsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, SettingsFile, true);
            }
            catch (Exception ex)
            {
                LogService.Error("保存设置失败", ex);
            }
        }
    }

    public void Reset()
    {
        var old = SettingsFile;
        try
        {
            if (File.Exists(old))
                File.Move(old, old + ".bak", true);
        }
        catch
        {
        }

        Settings = new AppSettings();
        Settings.EnsureDefaults();
        Save();
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(SettingsFile))
                File.Copy(SettingsFile, SettingsFile + ".broken", true);
        }
        catch
        {
        }
    }
}
