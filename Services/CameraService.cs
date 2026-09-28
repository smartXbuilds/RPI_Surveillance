using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RpiCam.Services;

public sealed class CameraException(string message) : Exception(message);

public interface ICameraService
{
    /// <summary>Takes one picture with the current settings and adds it to the image store.</summary>
    Task<ImageInfo> CaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes a small, fast picture for internal use (motion detection probing). Never written to
    /// the gallery. Still goes through the same camera gate as every other capture.
    /// </summary>
    Task<byte[]> CaptureProbeAsync(CancellationToken ct = default);
}

public abstract class CameraServiceBase(
    ImageStore store,
    CameraSettingsStore settings,
    IOptions<RpiCamOptions> options,
    CameraGate gate) : ICameraService
{
    protected RpiCamOptions Options => options.Value;

    public async Task<ImageInfo> CaptureAsync(CancellationToken ct = default)
    {
        var minFree = Options.MinFreeMegabytes;
        if (store.FreeMegabytes() < minFree)
        {
            throw new CameraException($"Less than {minFree} MB free storage left.");
        }

        using var _ = await gate.AcquireAsync(ct);
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

    public async Task<byte[]> CaptureProbeAsync(CancellationToken ct = default)
    {
        var tmpDir = Path.Combine(Path.GetFullPath(Options.DataDir), "tmp");
        Directory.CreateDirectory(tmpDir);
        var path = Path.Combine(tmpDir, $"probe-{Guid.NewGuid():N}.jpg");

        using var _ = await gate.AcquireAsync(ct);
        try
        {
            await CaptureProbeToFileAsync(path, ct);
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    protected abstract Task CaptureToFileAsync(string path, CameraSettings settings, CancellationToken ct);
    protected abstract Task CaptureProbeToFileAsync(string path, CancellationToken ct);
}

/// <summary>Real camera through rpicam-still (rpicam-apps).</summary>
public sealed class RpiCameraService(
    ImageStore store,
    CameraSettingsStore settings,
    IOptions<RpiCamOptions> options,
    CameraGate gate,
    IProcessRunner runner) : CameraServiceBase(store, settings, options, gate)
{
    protected override async Task CaptureToFileAsync(string path, CameraSettings s, CancellationToken ct)
    {
        // -n: no preview window (headless), -t: time the auto exposure gets to settle.
        var args = new List<string>
        {
            "-n", "-t", "1000",
            "--width", s.Width.ToString(),
            "--height", s.Height.ToString(),
            "-q", s.Quality.ToString(),
        };
        args.AddRange(ImageControlArgs(s));
        args.AddRange(["-o", path]);
        var result = await runner.RunAsync(Options.CameraCommand, args, TimeSpan.FromSeconds(40), ct);
        if (!result.Success)
        {
            throw new CameraException(Truncate(result.ErrorText));
        }
    }

    protected override async Task CaptureProbeToFileAsync(string path, CancellationToken ct)
    {
        // Small, quick, fixed settle time: this only feeds the motion-detection frame diff, not the gallery.
        string[] args = ["-n", "-t", "300", "--width", "160", "--height", "120", "-q", "50", "-o", path];
        var result = await runner.RunAsync(Options.CameraCommand, args, TimeSpan.FromSeconds(10), ct);
        if (!result.Success)
        {
            throw new CameraException(Truncate(result.ErrorText));
        }
    }

    /// <summary>
    /// The image-control flags shared by rpicam-still and rpicam-vid (brightness, AWB, flip, ...).
    /// Kept invariant-culture: rpicam-apps parses these as plain C doubles, not locale-formatted ones.
    /// </summary>
    internal static IEnumerable<string> ImageControlArgs(CameraSettings s)
    {
        yield return "--brightness";
        yield return s.Brightness.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        yield return "--contrast";
        yield return s.Contrast.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        yield return "--saturation";
        yield return s.Saturation.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        yield return "--sharpness";
        yield return s.Sharpness.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        yield return "--ev";
        yield return s.Ev.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        yield return "--awb";
        yield return s.Awb.ToString().ToLowerInvariant();
        if (s.HFlip)
        {
            yield return "--hflip";
        }
        if (s.VFlip)
        {
            yield return "--vflip";
        }
    }

    private static string Truncate(string text) =>
        string.IsNullOrWhiteSpace(text) ? "rpicam-still failed." : text.Length <= 300 ? text : text[..300];
}

/// <summary>Stand-in camera for development: writes a colour gradient that changes with every shot.</summary>
public sealed class SimulatedCameraService(
    ImageStore store,
    CameraSettingsStore settings,
    IOptions<RpiCamOptions> options,
    CameraGate gate) : CameraServiceBase(store, settings, options, gate)
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

    protected override async Task CaptureProbeToFileAsync(string path, CancellationToken ct)
    {
        // Mostly static so "no motion" is the default in Simulate mode; briefly shows a red block
        // every 20 seconds so the motion detector has something to react to during development.
        const int width = 160, height = 120;
        var blip = DateTime.UtcNow.Second % 20 < 2;
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                var inBlipRow = blip && y is > 40 and < 80;
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = inBlipRow && x is > 60 and < 100 ? new Rgb24(220, 60, 60) : new Rgb24(60, 60, 60);
                }
            }
        });
        await image.SaveAsJpegAsync(path, ct);
    }
}
