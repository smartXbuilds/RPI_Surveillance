using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public enum MotionReactionMode
{
    /// <summary>Take still pictures at an interval, for a fixed total duration. Stored in the gallery.</summary>
    Interval,

    /// <summary>Record a single video clip of a fixed length. Stored under the clips list.</summary>
    Stream,
}

public sealed record MotionSettings(
    bool Enabled = false,
    MotionReactionMode Mode = MotionReactionMode.Interval,
    int Sensitivity = 5,
    int StreamSeconds = 10,
    int IntervalCaptureSeconds = 5,
    int IntervalDurationSeconds = 120,
    int CooldownSeconds = 30)
{
    public const int MinSensitivity = 1;
    public const int MaxSensitivity = 10;
    public const int MinSeconds = 1;
    public const int MaxStreamSeconds = 300;
    public const int MaxIntervalCaptureSeconds = 600;
    public const int MaxIntervalDurationSeconds = 3600;
    public const int MaxCooldownSeconds = 3600;

    /// <summary>
    /// Fraction of the probe image that must change before a frame counts as motion. Sensitivity 1
    /// (least sensitive) needs 15% of the image to change, 10 (most sensitive) needs about 1.5%.
    /// </summary>
    public double ChangedFractionThreshold => 0.15 / Math.Clamp(Sensitivity, MinSensitivity, MaxSensitivity);

    public MotionSettings Normalized() => this with
    {
        Sensitivity = Math.Clamp(Sensitivity, MinSensitivity, MaxSensitivity),
        StreamSeconds = Math.Clamp(StreamSeconds, MinSeconds, MaxStreamSeconds),
        IntervalCaptureSeconds = Math.Clamp(IntervalCaptureSeconds, MinSeconds, MaxIntervalCaptureSeconds),
        IntervalDurationSeconds = Math.Clamp(IntervalDurationSeconds, MinSeconds, MaxIntervalDurationSeconds),
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, MaxCooldownSeconds),
    };
}

/// <summary>Motion detection settings, persisted as motion.json in the data directory.</summary>
public sealed class MotionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<MotionSettingsStore> _log;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private volatile MotionSettings _current;

    public MotionSettingsStore(IOptions<RpiCamOptions> options, ILogger<MotionSettingsStore> log)
    {
        _log = log;
        Directory.CreateDirectory(options.Value.DataDir);
        _path = Path.Combine(options.Value.DataDir, "motion.json");
        _current = Load();
    }

    public MotionSettings Current => _current;

    public event Action? Changed;

    public async Task UpdateAsync(MotionSettings settings)
    {
        var normalized = settings.Normalized();
        await _saveLock.WaitAsync();
        try
        {
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

    private MotionSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<MotionSettings>(File.ReadAllText(_path));
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
        return new MotionSettings().Normalized();
    }
}
