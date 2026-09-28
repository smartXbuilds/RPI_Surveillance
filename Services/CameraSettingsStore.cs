using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

/// <summary>Auto white balance mode, passed to rpicam-still as --awb.</summary>
public enum AwbMode
{
    Auto,
    Incandescent,
    Tungsten,
    Fluorescent,
    Indoor,
    Daylight,
    Cloudy,
}

public sealed record CameraSettings(
    int IntervalSeconds = 0,
    int Width = 1920,
    int Height = 1080,
    int Quality = 85,
    double Brightness = 0,
    double Contrast = 1,
    double Saturation = 1,
    double Sharpness = 1,
    double Ev = 0,
    AwbMode Awb = AwbMode.Auto,
    bool HFlip = false,
    bool VFlip = false)
{
    public const int MinSide = 320;
    public const int MaxSide = 4608;
    public const int MinIntervalSeconds = 5;

    /// <summary>Returns a copy with all values forced into the range the camera tools accept.</summary>
    public CameraSettings Normalized() => this with
    {
        IntervalSeconds = IntervalSeconds <= 0 ? 0 : Math.Max(IntervalSeconds, MinIntervalSeconds),
        Width = Math.Clamp(Width, MinSide, MaxSide),
        Height = Math.Clamp(Height, MinSide, MaxSide),
        Quality = Math.Clamp(Quality, 10, 100),
        Brightness = Math.Clamp(Brightness, -1, 1),
        Contrast = Math.Clamp(Contrast, 0, 2),
        Saturation = Math.Clamp(Saturation, 0, 2),
        Sharpness = Math.Clamp(Sharpness, 0, 2),
        Ev = Math.Clamp(Ev, -2, 2),
    };
}

/// <summary>Capture settings, persisted as settings.json in the data directory.</summary>
public sealed class CameraSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<CameraSettingsStore> _log;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private volatile CameraSettings _current;

    public CameraSettingsStore(IOptions<RpiCamOptions> options, ILogger<CameraSettingsStore> log)
    {
        _log = log;
        Directory.CreateDirectory(options.Value.DataDir);
        _path = Path.Combine(options.Value.DataDir, "settings.json");
        _current = Load();
    }

    public CameraSettings Current => _current;

    public event Action? Changed;

    public async Task UpdateAsync(CameraSettings settings)
    {
        var normalized = settings.Normalized();
        await _saveLock.WaitAsync();
        try
        {
            // Write to a temp file first so a power cut cannot leave a half-written settings.json.
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            _current = normalized;
        }
        finally
        {
            _saveLock.Release();
        }
        Changed?.Invoke();
    }

    private CameraSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<CameraSettings>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    return loaded.Normalized();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.LogWarning(ex, "Could not read {Path}, using defaults", _path);
        }
        return new CameraSettings().Normalized();
    }
}
