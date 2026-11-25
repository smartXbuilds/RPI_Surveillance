using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

/// <summary>
/// Stand-in for the Pi camera during development: ffmpeg's test pattern generator instead of rpicam-vid, so the
/// motion-clip and live-view code paths can be exercised without the Pi's camera. Needs a local
/// ffmpeg; if it is missing this reports a clear error instead of silently doing nothing.
/// </summary>
public sealed class SimulatedVideoPipeline(IOptions<RpiCamOptions> options) : IPiVideoPipeline
{
    private RpiCamOptions Options => options.Value;

    public async Task RecordClipAsync(string outputPath, int seconds, CancellationToken ct)
    {
        List<string> args =
        [
            "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=20",
            "-t", seconds.ToString(),
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-y", outputPath,
        ];
        using var ffmpeg = StartFfmpeg(args, redirectOut: false);
        var stderr = ffmpeg.StandardError.ReadToEndAsync(ct);
        await ffmpeg.WaitForExitAsync(ct);
        if (ffmpeg.ExitCode != 0)
        {
            throw new CameraException($"ffmpeg (simulated clip) failed: {Truncate(await stderr)}");
        }
    }

    public Task<ILiveStream> StartLiveAsync(CancellationToken ct)
    {
        List<string> args =
        [
            "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi", "-i", "testsrc=size=640x480:rate=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "pipe:1",
        ];
        var ffmpeg = StartFfmpeg(args, redirectOut: true);
        if (ffmpeg.HasExited)
        {
            throw new CameraException("ffmpeg (simulated live view) exited immediately.");
        }
        return Task.FromResult<ILiveStream>(new SimulatedLiveStream(ffmpeg));
    }

    private Process StartFfmpeg(IEnumerable<string> args, bool redirectOut)
    {
        var startInfo = new ProcessStartInfo(Options.FfmpegCommand)
        {
            RedirectStandardOutput = redirectOut,
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
            return Process.Start(startInfo)
                ?? throw new CameraException($"{Options.FfmpegCommand} did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CameraException(
                $"{Options.FfmpegCommand} could not be started ({ex.Message}). Install ffmpeg locally to try " +
                "motion clips / the live view in Simulate mode.");
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private sealed class SimulatedLiveStream(Process ffmpeg) : ILiveStream
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
