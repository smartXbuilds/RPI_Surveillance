namespace RpiCam.Services;

/// <summary>
/// The camera hardware serves exactly one client at a time. Manual/scheduled captures, motion
/// detection (both probing and its reactions) and the live view all go through this single gate
/// so they never collide on the device.
/// </summary>
public sealed class CameraGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for exclusive access. Dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    /// <summary>Takes the gate only if it is free right now; never waits. Used where a caller should
    /// report "camera busy" immediately instead of queueing (the live view).</summary>
    public bool TryAcquire(out IDisposable? handle)
    {
        if (_gate.Wait(0))
        {
            handle = new Releaser(_gate);
            return true;
        }
        handle = null;
        return false;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
