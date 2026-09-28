using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RpiCam.Services;

public sealed class CameraException(string message) : Exception(message);

public interface ICameraService
{
    /// <summary>Takes one picture with the current settings and adds it to the image store.</summary>
    Task<ImageInfo> CaptureAsync(CancellationToken ct = default);
}

public abstract class CameraServiceBase(ImageStore store, CameraSettingsStore settings, IOptions<RpiCamOptions> options) : ICameraService
{
    // The camera can only serve one capture at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected RpiCamOptions Options => options.Value;

    public async Task<ImageInfo> CaptureAsync(CancellationToken ct = default)
    {
        var minFree = Options.MinFreeMegabytes;
        if (store.FreeMegabytes() < minFree)
        {
            throw new CameraException($"Less than {minFree} MB free storage left.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            var path = store.NewImagePath();
            try
            {
                await CaptureToFileAsync(path, settings.Current, ct);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    throw new CameraException("The camera produced no image.");
                }
            }
            catch
            {
                // Never leave a truncated file behind, it would show up as a broken image.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                throw;
            }
            await store.RegisterAsync(path);
            var info = new FileInfo(path);
            return new ImageInfo(info.Name, DateTime.Now, info.Length);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected abstract Task CaptureToFileAsync(string path, CameraSettings settings, CancellationToken ct);
}

/// <summary>Real camera through rpicam-still (rpicam-apps).</summary>
public sealed class RpiCameraService(
    ImageStore store,
    CameraSettingsStore settings,
    IOptions<RpiCamOptions> options,
    IProcessRunner runner) : CameraServiceBase(store, settings, options)
{
    protected override async Task CaptureToFileAsync(string path, CameraSettings s, CancellationToken ct)
    {
        // -n: no preview window (headless), -t: time the auto exposure gets to settle.
        string[] args =
        [
            "-n", "-t", "1000",
            "--width", s.Width.ToString(),
            "--height", s.Height.ToString(),
            "-q", s.Quality.ToString(),
            "-o", path,
        ];
        var result = await runner.RunAsync(Options.CameraCommand, args, TimeSpan.FromSeconds(40), ct);
        if (!result.Success)
        {
            throw new CameraException(Truncate(result.ErrorText));
        }
    }

    private static string Truncate(string text) =>
        string.IsNullOrWhiteSpace(text) ? "rpicam-still failed." : text.Length <= 300 ? text : text[..300];
}

/// <summary>Stand-in camera for development: writes a colour gradient that changes with every shot.</summary>
public sealed class SimulatedCameraService(
    ImageStore store,
    CameraSettingsStore settings,
    IOptions<RpiCamOptions> options) : CameraServiceBase(store, settings, options)
{
    private int _shot;

    protected override async Task CaptureToFileAsync(string path, CameraSettings s, CancellationToken ct)
    {
        var shot = Interlocked.Increment(ref _shot);
        const int width = 800, height = 600;
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgb24(
                        (byte)((x * 255 / width + shot * 37) % 256),
                        (byte)(y * 255 / height),
                        (byte)((255 - x * 255 / width + shot * 71) % 256));
                }
            }
        });
        await image.SaveAsJpegAsync(path, ct);
    }
}
