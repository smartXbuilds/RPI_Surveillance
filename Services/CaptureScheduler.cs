namespace RpiCam.Services;

/// <summary>Takes pictures at the interval configured in the settings (0 = only manual captures).</summary>
public sealed class CaptureScheduler(
    ICameraService camera,
    CameraSettingsStore settings,
    ILogger<CaptureScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCapture = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Poll once a second so a changed interval takes effect immediately.
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            var interval = settings.Current.IntervalSeconds;
            if (interval <= 0 || DateTime.UtcNow - lastCapture < TimeSpan.FromSeconds(interval))
            {
                continue;
            }

            // Set before capturing: a failing camera is retried once per interval, not in a tight loop.
            lastCapture = DateTime.UtcNow;
            try
            {
                await camera.CaptureAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Scheduled capture failed: {Message}", ex.Message);
            }
        }
    }
}
