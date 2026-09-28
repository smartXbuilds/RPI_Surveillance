using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public enum MotionEventKind
{
    Interval,
    Stream,
}

/// <summary>One completed motion reaction. For <see cref="MotionEventKind.Stream"/>, <see cref="ClipName"/>
/// is the file in <see cref="ClipStore"/>; for <see cref="MotionEventKind.Interval"/>, <see cref="PictureCount"/>
/// is how many pictures landed in the gallery.</summary>
public sealed record MotionEvent(DateTime Time, MotionEventKind Kind, string? ClipName, int? PictureCount);

/// <summary>The most recent motion events, persisted as motion-events.json in the data directory.</summary>
public sealed class MotionEventLog
{
    private const int MaxEvents = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<MotionEventLog> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<MotionEvent> _events;

    public MotionEventLog(IOptions<RpiCamOptions> options, ILogger<MotionEventLog> log)
    {
        _log = log;
        Directory.CreateDirectory(options.Value.DataDir);
        _path = Path.Combine(options.Value.DataDir, "motion-events.json");
        _events = Load();
    }

    public event Action? Changed;

    public IReadOnlyList<MotionEvent> List() => _events;

    public async Task AddAsync(MotionEvent motionEvent)
    {
        await _lock.WaitAsync();
        try
        {
            _events.Insert(0, motionEvent);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);
            }
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_events, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke();
    }

    private List<MotionEvent> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<MotionEvent>>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.LogWarning(ex, "Could not read {Path}, starting with an empty event log", _path);
        }
        return [];
    }
}
