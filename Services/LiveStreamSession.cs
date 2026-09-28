namespace RpiCam.Services;

/// <summary>
/// Lets the Live page stop the stream explicitly instead of relying only on the browser aborting
/// the HTTP connection when the &lt;video&gt; element is removed. That detection turned out to be
/// unreliable in practice (verified on the device: rpicam-vid/ffmpeg kept running well after the
/// element was gone) - Blazor Server runs the page's own code on the server already, so it can
/// cancel the same token the streaming endpoint is using, no HTTP round trip needed.
/// </summary>
public sealed class LiveStreamSession
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _current;

    /// <summary>Called by the streaming endpoint when a viewer connects. Cancels any previous session first.</summary>
    public CancellationToken Begin()
    {
        lock (_lock)
        {
            _current?.Cancel();
            _current = new CancellationTokenSource();
            return _current.Token;
        }
    }

    /// <summary>Called by the Live page's Stop button.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _current?.Cancel();
        }
    }

    /// <summary>Called by the streaming endpoint when it is done, so a later Stop() does not affect the next viewer.</summary>
    public void End(CancellationToken token)
    {
        lock (_lock)
        {
            if (_current is { } cts && cts.Token == token)
            {
                _current = null;
            }
        }
    }
}
