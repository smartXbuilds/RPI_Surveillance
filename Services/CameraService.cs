using System.Globalization;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public sealed class CameraException(string message) : Exception(message);

public interface ICameraService
{
    /// <summary>Takes one picture with the current settings and adds it to the image store.</summary>
    Task<ImageInfo> CaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes a small, fast picture for internal use (motion detection probing) and returns its
    /// brightness values, <see cref="Probe.Width"/> × <see cref="Probe.Height"/> bytes, row by row.
    /// Never written to the gallery. Still goes through the same camera gate as every other capture.
    /// </summary>
    Task<byte[]> CaptureProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// The motion probe picture, as raw YUV420: the Y (brightness) plane comes first and is exactly
/// the grayscale picture the frame diff needs, no decoding. rpicam-still (--encoding yuv420) and
/// ffmpeg (-pix_fmt yuv420p -f rawvideo) both write this layout.
/// </summary>
public static class Probe
{
    public const int Width = 160;
    public const int Height = 120;

    public const int GrayBytes = Width * Height;

    /// <summary>Y plane plus the quarter-size U and V planes.</summary>
    public const int Yuv420Bytes = GrayBytes * 3 / 2;
}

/// <summary>Talks to one kind of camera. Only called while the camera gate is held.</summary>
public interface ICameraBackend
{
    /// <summary>Writes a JPEG to <paramref name="path"/>.</summary>
    Task CaptureAsync(string path, CameraSettings settings, CancellationToken ct);

    /// <summary>Writes a <see cref="Probe"/> picture (raw YUV420) to <paramref name="path"/>.</summary>
    Task CaptureProbeAsync(string path, CameraSettings settings, CancellationToken ct);
}

/// <summary>The backend for <see cref="CameraSource.Pi"/>: the real CSI camera, or its stand-in in Simulate mode.</summary>
public interface IPiCameraBackend : ICameraBackend;

/// <summary>Picks the backend for the configured camera source; free space, gate and file handling are shared.</summary>
public sealed class CameraService(
    ImageStore store,
    CameraSettingsStore settings,
    IOptions<RpiCamOptions> options,
    CameraGate gate,
    IPiCameraBackend pi,
    FfmpegCameraBackend ffmpeg) : ICameraService
{
    private ICameraBackend Backend(CameraSettings s) => s.Source == CameraSource.Pi ? pi : ffmpeg;

    public async Task<ImageInfo> CaptureAsync(CancellationToken ct = default)
    {
        var minFree = options.Value.MinFreeMegabytes;
        if (store.FreeMegabytes() < minFree)
        {
            throw new CameraException($"Less than {minFree} MB free storage left.");
        }

        using var _ = await gate.AcquireAsync(ct);
        var path = store.NewImagePath();
        var s = settings.Current;
        try
        {
            await Backend(s).CaptureAsync(path, s, ct);
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
        var tmpDir = Path.Combine(Path.GetFullPath(options.Value.DataDir), "tmp");
        Directory.CreateDirectory(tmpDir);
        var path = Path.Combine(tmpDir, $"probe-{Guid.NewGuid():N}.yuv");

        using var _ = await gate.AcquireAsync(ct);
        try
        {
            var s = settings.Current;
            await Backend(s).CaptureProbeAsync(path, s, ct);
            var yuv = await File.ReadAllBytesAsync(path, ct);
            // Any other size means a different resolution or row padding, and the bytes would be misread.
            if (yuv.Length != Probe.Yuv420Bytes)
            {
                throw new CameraException($"Probe picture has {yuv.Length} bytes, expected {Probe.Yuv420Bytes} (YUV420 {Probe.Width}x{Probe.Height}).");
            }
            return yuv[..Probe.GrayBytes];
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

/// <summary>Real camera through rpicam-still (rpicam-apps).</summary>
public sealed class RpiCameraBackend(IOptions<RpiCamOptions> options, IProcessRunner runner) : IPiCameraBackend
{
    public async Task CaptureAsync(string path, CameraSettings s, CancellationToken ct)
    {
        // -n: no preview window (headless), -t: time the auto exposure gets to settle.
        var args = new List<string>
        {
            "-n", "-t", "1000",
            "--width", s.Width.ToString(CultureInfo.InvariantCulture),
            "--height", s.Height.ToString(CultureInfo.InvariantCulture),
            "-q", s.Quality.ToString(CultureInfo.InvariantCulture),
        };
        args.AddRange(ImageControlArgs(s));
        args.AddRange(["-o", path]);
        var result = await runner.RunAsync(options.Value.CameraCommand, args, TimeSpan.FromSeconds(40), ct);
        if (!result.Success)
        {
            throw new CameraException(Truncate(result.ErrorText));
        }
    }

    public async Task CaptureProbeAsync(string path, CameraSettings s, CancellationToken ct)
    {
        // Small, quick, fixed settle time: this only feeds the motion-detection frame diff, not the gallery.
        string[] args =
        [
            "-n", "-t", "300", "--width", Probe.Width.ToString(CultureInfo.InvariantCulture),
            "--height", Probe.Height.ToString(CultureInfo.InvariantCulture), "--encoding", "yuv420", "-o", path,
        ];
        var result = await runner.RunAsync(options.Value.CameraCommand, args, TimeSpan.FromSeconds(10), ct);
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
        yield return s.Brightness.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "--contrast";
        yield return s.Contrast.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "--saturation";
        yield return s.Saturation.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "--sharpness";
        yield return s.Sharpness.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "--ev";
        yield return s.Ev.ToString("0.###", CultureInfo.InvariantCulture);
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

/// <summary>
/// Stand-in for the Pi camera during development: ffmpeg's test pattern (like the simulated live
/// view), in a different colour with every shot. Needs ffmpeg installed on the development machine.
/// </summary>
public sealed class SimulatedCameraBackend(IOptions<RpiCamOptions> options, IProcessRunner runner) : IPiCameraBackend
{
    private int _shot;

    public async Task CaptureAsync(string path, CameraSettings s, CancellationToken ct)
    {
        var hue = Interlocked.Increment(ref _shot) * 37 % 360;
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "testsrc2=size=800x600",
            "-vf", $"hue=h={hue}", "-frames:v", "1", "-q:v", "3", "-f", "mjpeg", "-y", path,
        ];
        var result = await runner.RunAsync(options.Value.FfmpegCommand, args, TimeSpan.FromSeconds(30), ct);
        if (!result.Success)
        {
            throw new CameraException($"Simulated camera needs ffmpeg: {result.ErrorText}");
        }
    }

    public Task CaptureProbeAsync(string path, CameraSettings s, CancellationToken ct)
    {
        // Mostly static so "no motion" is the default in Simulate mode; briefly shows a bright block
        // every 20 seconds so the motion detector has something to react to during development.
        // Written as the same raw YUV420 the real camera delivers: Y plane, then neutral U and V.
        var blip = DateTime.UtcNow.Second % 20 < 2;
        var yuv = new byte[Probe.Yuv420Bytes];
        for (var y = 0; y < Probe.Height; y++)
        {
            for (var x = 0; x < Probe.Width; x++)
            {
                yuv[(y * Probe.Width) + x] = blip && y is > 40 and < 80 && x is > 60 and < 100 ? (byte)200 : (byte)60;
            }
        }
        yuv.AsSpan(Probe.GrayBytes).Fill(128);
        return File.WriteAllBytesAsync(path, yuv, ct);
    }
}

/// <summary>
/// USB webcams (V4L2) and network cameras (RTSP), both read through ffmpeg. The image controls
/// (brightness, white balance, ...) are rpicam options and do not apply here.
/// </summary>
public sealed class FfmpegCameraBackend(IOptions<RpiCamOptions> options, IProcessRunner runner) : ICameraBackend
{
    public async Task CaptureAsync(string path, CameraSettings s, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(CameraInput.Args(s, s.Width, s.Height, framerate: null));
        if (s.Source == CameraSource.Usb)
        {
            // Webcams start dark and adjust exposure over the first frames; skip the first second.
            args.AddRange(["-ss", "1"]);
        }
        // ffmpeg's JPEG quality is a scale from 2 (best) to 31: map the 10..100 setting onto it.
        var qscale = (int)Math.Round(31 - ((s.Quality - 10) * 29 / 90.0));
        args.AddRange(["-frames:v", "1", "-q:v", qscale.ToString(CultureInfo.InvariantCulture), "-f", "mjpeg", "-y", path]);
        await RunAsync(args, ct);
    }

    public async Task CaptureProbeAsync(string path, CameraSettings s, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        // Ask a webcam for a small picture right away; ffmpeg scales whatever arrives to the probe size.
        args.AddRange(CameraInput.Args(s, 320, 240, framerate: null));
        args.AddRange(
        [
            "-vf", $"scale={Probe.Width}:{Probe.Height}", "-pix_fmt", "yuv420p",
            "-frames:v", "1", "-f", "rawvideo", "-y", path,
        ]);
        await RunAsync(args, ct);
    }

    private async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var result = await runner.RunAsync(options.Value.FfmpegCommand, args, TimeSpan.FromSeconds(30), ct);
        if (!result.Success)
        {
            var text = result.ErrorText;
            throw new CameraException(string.IsNullOrWhiteSpace(text) ? "ffmpeg failed." : text.Length <= 300 ? text : text[..300]);
        }
    }
}
