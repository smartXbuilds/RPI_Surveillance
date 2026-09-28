using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public interface ISystemTimeService
{
    Task<bool> SetAsync(DateTimeOffset time);
}

/// <summary>
/// A Pi has no battery-backed clock, and the hotspot has no internet for NTP, so image
/// timestamps would drift. This lets the user copy the time from their browser.
/// </summary>
public sealed class SystemTimeService(IProcessRunner runner, IOptions<RpiCamOptions> options, ILogger<SystemTimeService> log) : ISystemTimeService
{
    private static readonly DateTimeOffset Earliest = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<bool> SetAsync(DateTimeOffset time)
    {
        // Guards against a browser with a broken clock making every image timestamp nonsense.
        if (time < Earliest || time > Latest)
        {
            return false;
        }
        if (options.Value.Simulate)
        {
            log.LogInformation("Simulation: would set system time to {Time:u}", time);
            return true;
        }

        var result = await runner.RunAsync("date", ["-u", "-s", $"@{time.ToUnixTimeSeconds()}"], TimeSpan.FromSeconds(10));
        if (!result.Success)
        {
            log.LogWarning("Setting the system time failed: {Error}", result.ErrorText);
            return false;
        }
        // DietPi restores the last saved time at boot; save the new one now (best effort).
        await runner.RunAsync("fake-hwclock", ["save"], TimeSpan.FromSeconds(10));
        return true;
    }
}
