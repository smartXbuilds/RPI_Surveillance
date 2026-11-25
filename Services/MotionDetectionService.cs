
namespace RpiCam.Services;

public enum MotionStatus
{
    Disabled,
    Watching,
    Reacting,
    Cooldown,
}

/// <summary>
/// Polls a small grayscale probe picture from the camera every couple of seconds and compares it
/// against the previous one. This runs entirely in the app rather than through rpicam-apps' own
/// "motion_detect" post-processing stage: that stage's result is not exposed anywhere a CLI-driven
/// app can read it (no log line, not in the --metadata dump), only the stage name itself is even
/// present in the installed rpicam-apps-lite build - verified on the device. Frame differencing
/// here is slower to react but fully under our control and easy to reason about.
/// </summary>
public sealed class MotionDetectionService(
    ICameraService camera,
    IVideoPipeline video,
    MotionSettingsStore settings,
    MotionEventLog events,
    ClipStore clips,
    ILogger<MotionDetectionService> log) : BackgroundService
{
    // Out of 255: how much a pixel's brightness must change to count as "changed" at all. Fixed
    // rather than user-configurable, the Sensitivity setting already controls how much of the
    // image has to change; this second knob would mostly just confuse without adding much.
    private const int PixelDiffThreshold = 28;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeFailureBackoff = TimeSpan.FromSeconds(30);

    private byte[]? _baseline;

    public MotionStatus Status { get; private set; } = MotionStatus.Disabled;

    public event Action? StatusChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!settings.Current.Enabled)
            {
                SetStatus(MotionStatus.Disabled);
                _baseline = null;
                await Task.Delay(DisabledPollInterval, stoppingToken);
                continue;
            }

            SetStatus(MotionStatus.Watching);
            await Task.Delay(SampleInterval, stoppingToken);
            if (!settings.Current.Enabled)
            {
                continue;
            }

            byte[] gray;
            try
            {
                gray = await camera.CaptureProbeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Back off: an unplugged webcam or unreachable network camera would otherwise be retried
                // every few seconds, flooding the log and keeping the camera gate (live view) busy.
                log.LogWarning("Motion probe failed, retrying in {Seconds}s: {Message}", ProbeFailureBackoff.TotalSeconds, ex.Message);
                _baseline = null;
                await Task.Delay(ProbeFailureBackoff, stoppingToken);
                continue;
            }

            if (_baseline is not null && HasMotion(_baseline, gray, settings.Current.ChangedFractionThreshold))
            {
                await ReactAsync(stoppingToken);
                // Re-establish a fresh baseline after reacting: the scene, lighting or camera
                // settings may have moved on, comparing against a stale frame would misfire.
                _baseline = null;
                continue;
            }
            _baseline = gray;
        }
    }

    private async Task ReactAsync(CancellationToken ct)
    {
        SetStatus(MotionStatus.Reacting);
        var current = settings.Current;
        try
        {
            if (current.Mode == MotionReactionMode.Stream)
            {
                var path = clips.NewClipPath();
                await video.RecordClipAsync(path, current.StreamSeconds, ct);
                clips.NotifyAdded();
                await events.AddAsync(new MotionEvent(DateTime.Now, MotionEventKind.Stream, Path.GetFileName(path), null));
            }
            else
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(current.IntervalDurationSeconds);
                var count = 0;
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        await camera.CaptureAsync(ct);
                        count++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("Motion interval capture failed: {Message}", ex.Message);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(current.IntervalCaptureSeconds), ct);
                }
                await events.AddAsync(new MotionEvent(DateTime.Now, MotionEventKind.Interval, null, count));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Motion reaction failed");
        }

        SetStatus(MotionStatus.Cooldown);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(current.CooldownSeconds), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private void SetStatus(MotionStatus status)
    {
        if (status == Status)
        {
            return;
        }
        Status = status;
        StatusChanged?.Invoke();
    }

    private static bool HasMotion(byte[] baseline, byte[] current, double fractionThreshold)
    {
        var changed = 0;
        for (var i = 0; i < baseline.Length; i++)
        {
            if (Math.Abs(baseline[i] - current[i]) > PixelDiffThreshold)
            {
                changed++;
            }
        }
        return (double)changed / baseline.Length > fractionThreshold;
    }
}
