using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public sealed record ClipInfo(string Name, DateTime Taken, long Size);

/// <summary>
/// Motion-triggered video clips (MP4), kept as plain files under DataDir/motion. Mirrors
/// <see cref="ImageStore"/>'s name-validation approach: the file name is the only identifier and
/// every name coming from a request is checked against <see cref="NamePattern"/> before use.
/// </summary>
public sealed partial class ClipStore
{
    private readonly string _clipsDir;

    public ClipStore(IOptions<RpiCamOptions> options)
    {
        _clipsDir = Path.Combine(Path.GetFullPath(options.Value.DataDir), "motion");
        Directory.CreateDirectory(_clipsDir);
    }

    public event Action? Changed;

    [GeneratedRegex(@"^[0-9]{8}_[0-9]{6}(_[0-9]{1,3})?\.mp4$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>All clips, newest first.</summary>
    public IReadOnlyList<ClipInfo> List()
    {
        var result = new List<ClipInfo>();
        foreach (var file in new DirectoryInfo(_clipsDir).EnumerateFiles("*.mp4"))
        {
            if (IsValidName(file.Name))
            {
                result.Add(new ClipInfo(file.Name, ParseTaken(file), file.Length));
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        return result;
    }

    public string? ResolvePath(string? name)
    {
        if (!IsValidName(name))
        {
            return null;
        }
        var path = Path.Combine(_clipsDir, name!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Reserves a new, unused file name for a clip recorded now.</summary>
    public string NewClipPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(_clipsDir, stamp + ".mp4");
        for (var n = 1; File.Exists(path) && n < 1000; n++)
        {
            path = Path.Combine(_clipsDir, $"{stamp}_{n}.mp4");
        }
        return path;
    }

    public void NotifyAdded() => Changed?.Invoke();

    public bool Delete(string name)
    {
        var path = ResolvePath(name);
        if (path is null)
        {
            return false;
        }
        File.Delete(path);
        Changed?.Invoke();
        return true;
    }

    private static DateTime ParseTaken(FileInfo file)
    {
        var stem = file.Name[..15];
        return DateTime.TryParseExact(stem, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)
            ? taken
            : file.LastWriteTime;
    }
}
