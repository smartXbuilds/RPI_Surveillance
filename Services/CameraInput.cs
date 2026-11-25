using System.Globalization;
using System.Text.RegularExpressions;

namespace RpiCam.Services;

/// <summary>Which camera the app reads from. Stored in settings.json as a number, so only append new values.</summary>
public enum CameraSource
{
    /// <summary>Raspberry Pi camera on the CSI ribbon cable, through rpicam-apps.</summary>
    Pi,

    /// <summary>USB webcam (V4L2 device such as /dev/video0), through ffmpeg.</summary>
    Usb,

    /// <summary>Network camera with an RTSP stream, through ffmpeg.</summary>
    Rtsp,
}

public sealed record UsbCamera(string Device, string Name);

/// <summary>ffmpeg input arguments for the USB and RTSP sources, plus validation and device discovery.</summary>
public static partial class CameraInput
{
    [GeneratedRegex(@"^/dev/video[0-9]{1,3}$")]
    private static partial Regex UsbDevicePattern();

    public static bool IsValidUsbDevice(string? device) => device is not null && UsbDevicePattern().IsMatch(device);

    /// <summary>
    /// Only rtsp:// and rtsps:// with a host. ffmpeg also understands file:, concat: and friends; an
    /// input URL from a form must never be able to point it at local files.
    /// </summary>
    public static bool IsValidRtspUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == "rtsp" || uri.Scheme == "rtsps")
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>The RTSP URL with user and password filled in (escaped), or unchanged when no user is set.</summary>
    public static string RtspUrlWithCredentials(CameraSettings s)
    {
        if (string.IsNullOrEmpty(s.RtspUser))
        {
            return s.RtspUrl;
        }
        var builder = new UriBuilder(s.RtspUrl)
        {
            UserName = Uri.EscapeDataString(s.RtspUser),
            Password = Uri.EscapeDataString(s.RtspPassword),
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Input arguments for ffmpeg. <paramref name="width"/>/<paramref name="height"/> are a request to a
    /// webcam (the driver picks the nearest size it supports); a network camera sends what it is set to.
    /// </summary>
    public static IEnumerable<string> Args(CameraSettings s, int width, int height, int? framerate)
    {
        switch (s.Source)
        {
            case CameraSource.Usb:
                if (!IsValidUsbDevice(s.UsbDevice))
                {
                    throw new CameraException("No USB camera selected (Settings → Camera).");
                }
                yield return "-f";
                yield return "v4l2";
                if (framerate is { } fps)
                {
                    yield return "-framerate";
                    yield return fps.ToString(CultureInfo.InvariantCulture);
                }
                yield return "-video_size";
                yield return $"{width}x{height}";
                yield return "-i";
                yield return s.UsbDevice;
                break;

            case CameraSource.Rtsp:
                if (!IsValidRtspUrl(s.RtspUrl))
                {
                    throw new CameraException("No valid RTSP address set (Settings → Camera).");
                }
                // TCP first (no lost packets, i.e. grey smears, over Wi-Fi), UDP if the camera does not offer it.
                // -timeout (microseconds): an unreachable camera fails after 10 s instead of holding the
                // camera gate forever.
                yield return "-rtsp_flags";
                yield return "prefer_tcp";
                yield return "-timeout";
                yield return "10000000";
                yield return "-i";
                yield return RtspUrlWithCredentials(s);
                break;

            default:
                throw new CameraException($"Camera source {s.Source} is not read through ffmpeg.");
        }
    }

    /// <summary>
    /// Video capture devices from /sys/class/video4linux. Only each device's first node (index 0; a
    /// webcam usually adds a metadata node as well), and without the Pi's own codec/ISP nodes.
    /// </summary>
    // Video nodes that are hardware codecs or ISPs, not cameras. Pi: bcm2835-codec/-isp, unicam (the
    // CSI camera itself), rpi-hevc-dec, pispbe/rp1-cfe (Pi 5). Other ARM boards: cedrus (Allwinner,
    // e.g. NanoPi NEO), hantro and rkvdec (Rockchip/NXP).
    private static readonly string[] NotACamera = ["bcm2835", "unicam", "rpi-", "pispbe", "rp1-", "cedrus", "hantro", "rkvdec"];

    public static IReadOnlyList<UsbCamera> FindUsbCameras()
    {
        const string sysDir = "/sys/class/video4linux";
        if (!Directory.Exists(sysDir))
        {
            return [];
        }
        var result = new List<UsbCamera>();
        foreach (var dir in Directory.EnumerateDirectories(sysDir, "video*"))
        {
            var device = "/dev/" + Path.GetFileName(dir);
            if (!IsValidUsbDevice(device))
            {
                continue;
            }
            var name = ReadSysFile(Path.Combine(dir, "name")) ?? device;
            var index = ReadSysFile(Path.Combine(dir, "index"));
            if (index is not null && index != "0")
            {
                continue;
            }
            if (NotACamera.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            result.Add(new UsbCamera(device, name));
        }
        return result.OrderBy(c => c.Device.Length).ThenBy(c => c.Device, StringComparer.Ordinal).ToList();
    }

    private static string? ReadSysFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
