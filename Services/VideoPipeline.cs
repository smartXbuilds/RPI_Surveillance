using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

/// <summary>A running live relay. Disposing it stops both the camera and ffmpeg.</summary>
public interface ILiveStream : IAsyncDisposable
{
    /// <summary>Fragmented MP4 bytes (h264, muxed by ffmpeg), ready to copy straight to an HTTP response.</summary>
    Stream Output { get; }
}

public interface IVideoPipeline
{
    /// <summary>Records a fixed-length H.264 clip and muxes it to a real MP4 file (motion "Stream" reactions).</summary>
    Task RecordClipAsync(string outputPath, int seconds, CancellationToken ct);

    /// <summary>Starts a continuous fragmented-MP4 stream for the live view. Dispose the result to stop it.</summary>
    Task<ILiveStream> StartLiveAsync(CancellationToken ct);
}

/// <summary>
/// rpicam-vid (the Pi's hardware H.264 encoder) piped into ffmpeg, which remuxes the raw stream
/// into MP4 without re-encoding (-c copy costs almost no CPU). Verified against the real device:
/// rpicam-vid needs the bcm2835-codec kernel module for the encoder, see deploy/.
/// </summary>
public sealed class RpiVideoPipeline(IOptions<RpiCamOptions> options, CameraSettingsStore settings, ILogger<RpiVideoPipeline> log) : IPiVideoPipeline
{
    // Fixed video resolution/framerate, independent of the still-picture settings: those can be set
    // far higher than sensible for video, and neither video feature asks the user to tune them.
    private const int Width = 1280;
    private const int Height = 720;
    private const int LiveWidth = 640;
    private const int LiveHeight = 480;
    private const int Framerate = 20;

    private RpiCamOptions Options => options.Value;

    public async Task RecordClipAsync(string outputPath, int seconds, CancellationToken ct)
    {
        using var rpicamVid = StartRpicamVid(Width, Height, seconds * 1000);
        using var ffmpeg = StartFfmpeg(["-y", outputPath]);
        await RunPipelineAsync(rpicamVid, ffmpeg, ct);
    }

    public async Task<ILiveStream> StartLiveAsync(CancellationToken ct)
    {
        var rpicamVid = StartRpicamVid(LiveWidth, LiveHeight, 0);
        var ffmpeg = StartFfmpeg(["pipe:1"]);
        var stream = new PipedLiveStream(rpicamVid, ffmpeg, log);
        stream.Start(ct);
        // Nothing was written yet: give the pipeline a moment to fail fast (wrong overlay, encoder
        // busy, ...) so the caller can report an error instead of handing back a stream that never
        // produces bytes.
        await Task.Delay(300, ct);
        if (rpicamVid.HasExited || ffmpeg.HasExited)
        {
            await stream.DisposeAsync();
            throw new CameraException("The video pipeline exited immediately, see the log for details.");
        }
        return stream;
    }

    private Process StartRpicamVid(int width, int height, int timeoutMs)
    {
        var args = new List<string>
        {
            "-n", "-t", timeoutMs.ToString(),
            "--width", width.ToString(),
            "--height", height.ToString(),
            "--framerate", Framerate.ToString(),
            "--codec", "h264",
            "--inline", // repeats SPS/PPS on every I-frame, so ffmpeg can start mid-stream
        };
        args.AddRange(RpiCameraBackend.ImageControlArgs(settings.Current));
        args.AddRange(["-o", "-"]);
        return StartProcess(Options.VideoCommand, args, redirectIn: false);
    }

    private Process StartFfmpeg(IEnumerable<string> outputArgs)
    {
        List<string> args =
        [
            "-hide_banner", "-loglevel", "warning",
            "-f", "h264", "-i", "pipe:0",
            "-c", "copy",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            .. outputArgs,
        ];
        return StartProcess(Options.FfmpegCommand, args, redirectIn: true);
    }

    private static Process StartProcess(string fileName, IEnumerable<string> args, bool redirectIn)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = redirectIn,
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
        try
        {
            return Process.Start(startInfo) ?? throw new CameraException($"{fileName} did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CameraException($"{fileName}: {ex.Message}");
        }
    }

    /// <summary>Runs a fixed-length clip pipeline to completion: copies bytes, waits for both processes, checks exit codes.</summary>
    private static async Task RunPipelineAsync(Process source, Process sink, CancellationToken ct)
    {
        var sourceErr = ReadAllAsync(source.StandardError);
        var sinkErr = ReadAllAsync(sink.StandardError);
        try
        {
            await source.StandardOutput.BaseStream.CopyToAsync(sink.StandardInput.BaseStream, ct);
        }
        catch (IOException)
        {
            // sink (ffmpeg) can close its stdin and exit before source (rpicam-vid) is fully drained
            // once it already has everything it needs; that is not a failure by itself.
        }
        finally
        {
            sink.StandardInput.Close();
        }
        await source.WaitForExitAsync(ct);
        await sink.WaitForExitAsync(ct);
        if (source.ExitCode != 0)
        {
            throw new CameraException($"{source.StartInfo.FileName} failed: {Truncate(await sourceErr)}");
        }
        if (sink.ExitCode != 0)
        {
            throw new CameraException($"{sink.StartInfo.FileName} failed: {Truncate(await sinkErr)}");
        }
    }

    private static async Task<string> ReadAllAsync(StreamReader reader)
    {
        try { return await reader.ReadToEndAsync(); }
        catch (IOException) { return ""; }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    /// <summary>Keeps both processes alive and relays bytes until disposed or the source stream ends.</summary>
    private sealed class PipedLiveStream(Process source, Process sink, ILogger log) : ILiveStream
    {
        private readonly CancellationTokenSource _stopping = new();
        private Task? _pump;

        public Stream Output => sink.StandardOutput.BaseStream;

        public void Start(CancellationToken ct)
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            _pump = PumpAsync(link.Token);
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await source.StandardOutput.BaseStream.CopyToAsync(sink.StandardInput.BaseStream, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Normal on stop: the reader stops pulling, the pipe breaks, or we cancel it ourselves.
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Live stream pipe copy ended unexpectedly");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            Kill(source);
            Kill(sink);
            if (_pump is not null)
            {
                try { await _pump; } catch (OperationCanceledException) { }
            }
            source.Dispose();
            sink.Dispose();
        }

        private static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
