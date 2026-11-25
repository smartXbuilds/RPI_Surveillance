using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public sealed record ImageInfo(string Name, DateTime Taken, long Size);

/// <summary>
/// The captured JPEGs plus their thumbnails, kept as plain files. The file name
/// (yyyyMMdd_HHmmss[_n].jpg) is the only identifier, and every name coming from a request is
/// validated against <see cref="NamePattern"/> before it is turned into a path.
/// </summary>
public sealed partial class ImageStore
{
    private const int ThumbnailSize = 480;

    private readonly string _imagesDir;
    private readonly string _thumbsDir;
    private readonly SemaphoreSlim _thumbLock = new(1, 1);
    private readonly ILogger<ImageStore> _log;
    private readonly IProcessRunner _runner;
    private readonly string _ffmpeg;

    public ImageStore(IOptions<RpiCamOptions> options, IProcessRunner runner, ILogger<ImageStore> log)
    {
        _log = log;
        _runner = runner;
        _ffmpeg = options.Value.FfmpegCommand;
        // Absolute on purpose: Results.File() treats a relative path as a file below wwwroot.
        var dataDir = Path.GetFullPath(options.Value.DataDir);
        _imagesDir = Path.Combine(dataDir, "images");
        _thumbsDir = Path.Combine(dataDir, "thumbs");
        Directory.CreateDirectory(_imagesDir);
        Directory.CreateDirectory(_thumbsDir);
    }

    public event Action? Changed;

    [GeneratedRegex(@"^[0-9]{8}_[0-9]{6}(_[0-9]{1,3})?\.jpg$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>All images, newest first.</summary>
    public IReadOnlyList<ImageInfo> List()
    {
        var result = new List<ImageInfo>();
        foreach (var file in new DirectoryInfo(_imagesDir).EnumerateFiles("*.jpg"))
        {
            if (IsValidName(file.Name))
            {
                result.Add(new ImageInfo(file.Name, ParseTaken(file), file.Length));
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        return result;
    }

    public string? ResolveImagePath(string? name)
    {
        if (!IsValidName(name))
        {
            return null;
        }
        var path = Path.Combine(_imagesDir, name!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Reserves a new, unused file name for a capture taken now.</summary>
    public string NewImagePath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(_imagesDir, stamp + ".jpg");
        for (var n = 1; File.Exists(path) && n < 1000; n++)
        {
            path = Path.Combine(_imagesDir, $"{stamp}_{n}.jpg");
        }
        return path;
    }

    /// <summary>Called after a new file has been written, so the thumbnail exists before the UI asks for it.</summary>
    public async Task RegisterAsync(string imagePath)
    {
        try
        {
            await GetThumbnailPathAsync(Path.GetFileName(imagePath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The image itself is fine; the thumbnail is retried on first request.
            _log.LogWarning(ex, "Thumbnail for {Image} failed", imagePath);
        }
        Changed?.Invoke();
    }

    public async Task<string?> GetThumbnailPathAsync(string? name, CancellationToken ct = default)
    {
        var source = ResolveImagePath(name);
        if (source is null)
        {
            return null;
        }
        var thumb = Path.Combine(_thumbsDir, name!);
        if (File.Exists(thumb))
        {
            return thumb;
        }

        // One at a time: decoding a large JPEG needs tens of MB, which is a lot on a 512 MB Pi.
        await _thumbLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(thumb) && !await CreateThumbnailAsync(source, thumb, ct))
            {
                // The gallery still works, only with the full picture (slower on a phone).
                return source;
            }
            return thumb;
        }
        finally
        {
            _thumbLock.Release();
        }
    }

    /// <summary>
    /// Scales the picture down with ffmpeg (installed for the live view anyway) instead of decoding
    /// it in the app: no image library, and the memory is freed as soon as the process exits.
    /// </summary>
    private async Task<bool> CreateThumbnailAsync(string source, string thumb, CancellationToken ct)
    {
        var tmp = thumb + ".tmp";
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1",
            "-i", source,
            // Fit into a ThumbnailSize square, never enlarge; -q:v 5 is roughly JPEG quality 70.
            "-vf", $"scale='min({ThumbnailSize},iw)':'min({ThumbnailSize},ih)':force_original_aspect_ratio=decrease",
            "-frames:v", "1", "-q:v", "5", "-f", "mjpeg", "-y", tmp,
        ];
        var result = await _runner.RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(30), ct);
        if (!result.Success || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            _log.LogWarning("Thumbnail for {Image} failed: {Error}", Path.GetFileName(source), result.ErrorText);
            return false;
        }
        File.Move(tmp, thumb, overwrite: true);
        return true;
    }

    /// <summary>Deletes the named images and their thumbnails. Returns how many images were removed.</summary>
    public int Delete(IEnumerable<string> names)
    {
        var deleted = 0;
        foreach (var name in names.Distinct())
        {
            var path = ResolveImagePath(name);
            if (path is null)
            {
                continue;
            }
            File.Delete(path);
            File.Delete(Path.Combine(_thumbsDir, name));
            deleted++;
        }
        if (deleted > 0)
        {
            Changed?.Invoke();
        }
        return deleted;
    }

    public long FreeMegabytes() => new DriveInfo(Path.GetFullPath(_imagesDir)).AvailableFreeSpace / (1024 * 1024);

    private static DateTime ParseTaken(FileInfo file)
    {
        var stem = file.Name[..15];
        return DateTime.TryParseExact(stem, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)
            ? taken
            : file.LastWriteTime;
    }
}
