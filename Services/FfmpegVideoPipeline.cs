using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

/// <summary>The video pipeline for <see cref="CameraSource.Pi"/>: rpicam-vid, or its stand-in in Simulate mode.</summary>
public interface IPiVideoPipeline : IVideoPipeline;

/// <summary>Hands clips and the live view to the pipeline of the configured camera source.</summary>
public sealed class VideoPipeline(IPiVideoPipeline pi, FfmpegVideoPipeline ffmpeg, CameraSettingsStore settings) : IVideoPipeline
{
    private IVideoPipeline Current => settings.Current.Source == CameraSource.Pi ? pi : ffmpeg;

    public Task RecordClipAsync(string outputPath, int seconds, CancellationToken ct) => Current.RecordClipAsync(outputPath, seconds, ct);

    public Task<ILiveStream> StartLiveAsync(CancellationToken ct) => Current.StartLiveAsync(ct);
}

/// <summary>
/// USB webcams and network cameras through a single ffmpeg process.
/// <list type="bullet">
/// <item>RTSP: most cameras already send H.264, which is only remuxed (-c copy, almost no CPU). A
/// camera set to H.265 produces files browsers cannot play; set it to H.264.</item>
/// <item>USB: webcams send MJPEG or raw frames, which have to be encoded to H.264. On the Pi that is
/// the hardware encoder (h264_v4l2m2m, needs bcm2835-codec), see <see cref="RpiCamOptions.H264Encoder"/>.</item>
/// </list>
/// </summary>
public sealed class FfmpegVideoPipeline(
    IOptions<RpiCamOptions> options,
    CameraSettingsStore settings,
    IProcessRunner runner,
    ILogger<FfmpegVideoPipeline> log) : IVideoPipeline
{
    // Same fixed sizes as the Pi pipeline; only requested from webcams, a network camera sends its own.
    private const int ClipWidth = 1280;
    private const int ClipHeight = 720;
    private const int LiveWidth = 640;
    private const int LiveHeight = 480;
    private const int Framerate = 20;

    private static readonly string[] FragmentedMp4 = ["-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof"];

    public async Task RecordClipAsync(string outputPath, int seconds, CancellationToken ct)
    {
        var s = settings.Current;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(CameraInput.Args(s, ClipWidth, ClipHeight, Framerate));
        args.AddRange(["-t", seconds.ToString(CultureInfo.InvariantCulture), "-an"]);
        args.AddRange(VideoCodecArgs(s));
        args.AddRange(FragmentedMp4);
        args.AddRange(["-y", outputPath]);

        // The RTSP connect timeout is 10 s; on top of that the clip itself plus some slack.
        var result = await runner.RunAsync(options.Value.FfmpegCommand, args, TimeSpan.FromSeconds(seconds + 30), ct);
        if (!result.Success)
        {
            throw new CameraException($"ffmpeg failed: {Truncate(result.ErrorText)}");
        }
    }

    public async Task<ILiveStream> StartLiveAsync(CancellationToken ct)
    {
        var s = settings.Current;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(CameraInput.Args(s, LiveWidth, LiveHeight, Framerate));
        args.Add("-an");
        args.AddRange(VideoCodecArgs(s));
        args.AddRange(FragmentedMp4);
        args.Add("pipe:1");

        var startInfo = new ProcessStartInfo(options.Value.FfmpegCommand)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }
        startInfo.Environment["LC_ALL"] = "C";

        Process ffmpeg;
        try
        {
            ffmpeg = Process.Start(startInfo) ?? throw new CameraException("ffmpeg did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CameraException($"ffmpeg: {ex.Message}");
        }

        // Drained in the background: a full stderr pipe would block ffmpeg. Kept for the error message.
        var stderr = ffmpeg.StandardError.ReadToEndAsync(CancellationToken.None);
        var stream = new FfmpegLiveStream(ffmpeg);
        // Give it a moment to fail fast (device busy, wrong address, ...) so the caller can show an
        // error instead of a stream that never produces bytes.
        await Task.Delay(500, ct);
        if (ffmpeg.HasExited)
        {
            var error = await stderr;
            await stream.DisposeAsync();
            log.LogWarning("Live view ffmpeg exited: {Error}", Truncate(error));
            throw new CameraException($"ffmpeg exited immediately: {Truncate(error)}");
        }
        return stream;
    }

    private IEnumerable<string> VideoCodecArgs(CameraSettings s)
    {
        if (s.Source == CameraSource.Rtsp)
        {
            return ["-c:v", "copy"];
        }
        var encoder = options.Value.H264Encoder;
        // -g: a keyframe every 2 s, so the live view starts quickly and clips can be cut cleanly.
        return encoder == "libx264"
            ? ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", "40"]
            : ["-c:v", encoder, "-pix_fmt", "yuv420p", "-b:v", "2M", "-g", "40"];
    }

    private static string Truncate(string text) =>
        string.IsNullOrWhiteSpace(text) ? "(no output)" : text.Length <= 300 ? text.Trim() : text[..300];

    private sealed class FfmpegLiveStream(Process ffmpeg) : ILiveStream
    {
        public Stream Output => ffmpeg.StandardOutput.BaseStream;

        public ValueTask DisposeAsync()
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ffmpeg.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
