using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

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

    public ImageStore(IOptions<RpiCamOptions> options, ILogger<ImageStore> log)
    {
        _log = log;
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
            if (!File.Exists(thumb))
            {
                using var image = await Image.LoadAsync(source, ct);
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(ThumbnailSize, ThumbnailSize),
                }));
                var tmp = thumb + ".tmp";
                await image.SaveAsJpegAsync(tmp, new JpegEncoder { Quality = 70 }, ct);
                File.Move(tmp, thumb, overwrite: true);
            }
            return thumb;
        }
        finally
        {
            _thumbLock.Release();
        }
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
