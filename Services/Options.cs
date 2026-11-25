namespace RpiCam.Services;

/// <summary>
/// Device configuration (section "RpiCam"). On the Pi the values come from
/// /etc/rpicam/rpicam.env as RpiCam__* environment variables, see deploy/.
/// </summary>
public sealed class RpiCamOptions
{
    public const string Section = "RpiCam";

    /// <summary>
    /// Fake camera and fake network backend. Never touches nmcli or the camera, so the
    /// app can run on a development machine without disturbing its real Wi-Fi.
    /// </summary>
    public bool Simulate { get; set; }

    public string DataDir { get; set; } = "/var/lib/rpicam";

    public string CameraCommand { get; set; } = "rpicam-still";

    public string VideoCommand { get; set; } = "rpicam-vid";

    public string FfmpegCommand { get; set; } = "ffmpeg";

    /// <summary>
    /// H.264 encoder for USB webcams (their MJPEG/raw frames have to be encoded for the browser).
    /// h264_v4l2m2m is the Pi's hardware encoder; "libx264" for a development PC (software, CPU-heavy).
    /// </summary>
    public string H264Encoder { get; set; } = "h264_v4l2m2m";

    /// <summary>Capturing pauses when less free space than this is left on the data volume.</summary>
    public int MinFreeMegabytes { get; set; } = 200;

    public string WifiInterface { get; set; } = "wlan0";

    public string HotspotConnection { get; set; } = "rpicam-hotspot";

    public string HotspotSsid { get; set; } = "";

    public string HotspotPassword { get; set; } = "";

    public string HotspotAddress { get; set; } = "192.168.4.1/24";

    public int ConnectTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Old way to set the login (section "Auth", Auth__User / Auth__Password). Only read once: if no
/// account.json exists yet, <see cref="AccountStore"/> takes these values over. New devices create the
/// account on the setup page instead.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public string User { get; set; } = "";

    public string Password { get; set; } = "";
}
