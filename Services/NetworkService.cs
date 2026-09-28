using System.Text;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public enum NetworkMode { Unavailable, Hotspot, Client, Disconnected }

public sealed record NetworkStatus(NetworkMode Mode, string? ConnectionName, string? Ssid, string? IpAddress);

public sealed record WifiNetwork(string Ssid, int Signal, string Security, bool InUse)
{
    public bool IsOpen => Security is "" or "--";

    public bool IsEnterprise => Security.Contains("802.1X", StringComparison.OrdinalIgnoreCase);
}

public sealed record SavedNetwork(string Name, string Uuid, string Ssid, bool Active);

public enum NetworkOutcome { Connected, ConnectFailed, HotspotActive, HotspotFailed, Removed }

/// <summary>Result of the last background operation. Kept so a browser that lost its connection can show it after reconnecting.</summary>
public sealed record NetworkEvent(NetworkOutcome Outcome, string? Ssid, DateTime At);

public enum RequestResult { Accepted, Busy, Invalid }

public sealed class NetworkException(string message) : Exception(message);

public interface INetworkService
{
    /// <summary>True while a switch between hotspot and client mode is running.</summary>
    bool Busy { get; }

    NetworkEvent? LastEvent { get; }

    event Action? Changed;

    Task<NetworkStatus> GetStatusAsync(CancellationToken ct = default);

    /// <exception cref="NetworkException">The scan failed.</exception>
    Task<IReadOnlyList<WifiNetwork>> ScanAsync(bool rescan, CancellationToken ct = default);

    Task<IReadOnlyList<SavedNetwork>> GetSavedNetworksAsync(CancellationToken ct = default);

    // Everything below changes the radio mode, which drops the browser's own connection when it
    // is talking to the hotspot. So these only validate, then run in the background after a short
    // delay and report through LastEvent.
    RequestResult BeginConnect(string ssid, string? password);

    RequestResult BeginConnectSaved(string uuid);

    RequestResult BeginRemove(string uuid);

    RequestResult BeginHotspot();
}

public abstract class NetworkServiceBase(ILogger logger) : INetworkService
{
    /// <summary>Time for the HTTP response and the UI update to reach the browser before the radio is switched.</summary>
    private static readonly TimeSpan SwitchDelay = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim _operation = new(1, 1);
    private volatile bool _busy;
    private volatile NetworkEvent? _lastEvent;

    public bool Busy => _busy;

    public NetworkEvent? LastEvent => _lastEvent;

    public event Action? Changed;

    public abstract Task<NetworkStatus> GetStatusAsync(CancellationToken ct = default);

    public abstract Task<IReadOnlyList<WifiNetwork>> ScanAsync(bool rescan, CancellationToken ct = default);

    public abstract Task<IReadOnlyList<SavedNetwork>> GetSavedNetworksAsync(CancellationToken ct = default);

    public RequestResult BeginConnect(string ssid, string? password)
    {
        if (!IsValidSsid(ssid) || !IsValidPassword(password))
        {
            return RequestResult.Invalid;
        }
        return Begin(() => ConnectAsync(ssid, string.IsNullOrEmpty(password) ? null : password));
    }

    public RequestResult BeginConnectSaved(string uuid) =>
        Guid.TryParse(uuid, out _) ? Begin(() => ConnectSavedAsync(uuid)) : RequestResult.Invalid;

    public RequestResult BeginRemove(string uuid) =>
        Guid.TryParse(uuid, out _) ? Begin(() => RemoveAsync(uuid)) : RequestResult.Invalid;

    public RequestResult BeginHotspot() => Begin(HotspotAsync);

    protected abstract Task ConnectAsync(string ssid, string? password);

    protected abstract Task ConnectSavedAsync(string uuid);

    protected abstract Task RemoveAsync(string uuid);

    protected abstract Task HotspotAsync();

    protected void Publish(NetworkOutcome outcome, string? ssid)
    {
        _lastEvent = new NetworkEvent(outcome, ssid, DateTime.Now);
        Changed?.Invoke();
    }

    /// <summary>SSIDs are up to 32 bytes; control characters would only cause trouble further down.</summary>
    public static bool IsValidSsid(string? ssid) =>
        !string.IsNullOrEmpty(ssid) && Encoding.UTF8.GetByteCount(ssid) <= 32 && !ssid.Any(char.IsControl);

    /// <summary>Empty means an open network; otherwise WPA-PSK: 8 to 63 characters, or 64 hex digits.</summary>
    public static bool IsValidPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return true;
        }
        if (password.Any(char.IsControl))
        {
            return false;
        }
        return password.Length is >= 8 and <= 63
            || (password.Length == 64 && password.All(Uri.IsHexDigit));
    }

    private RequestResult Begin(Func<Task> work)
    {
        if (!_operation.Wait(0))
        {
            return RequestResult.Busy;
        }
        _busy = true;
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SwitchDelay);
                await work();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Network operation failed");
            }
            finally
            {
                _busy = false;
                _operation.Release();
                Changed?.Invoke();
            }
        });
        return RequestResult.Accepted;
    }
}

/// <summary>
/// Real backend: drives NetworkManager through nmcli. One profile (<see cref="RpiCamOptions.HotspotConnection"/>)
/// is the access point; every other Wi-Fi profile is a saved client network. The radio can only do one
/// of the two at a time, so activating one deactivates the other.
/// </summary>
public sealed class NmcliNetworkService(
    IOptions<RpiCamOptions> options,
    IProcessRunner runner,
    ILogger<NmcliNetworkService> log) : NetworkServiceBase(log)
{
    private const string Nmcli = "nmcli";
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);

    private RpiCamOptions Opt => options.Value;

    public override async Task<NetworkStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var devices = await RunAsync(["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"], QuickTimeout, ct);
        if (!devices.Success)
        {
            return new NetworkStatus(NetworkMode.Unavailable, null, null, null);
        }

        List<string>? wifi = null;
        foreach (var line in Lines(devices.StdOut))
        {
            var f = Terse.Split(line);
            if (f.Count >= 4 && f[0] == Opt.WifiInterface && f[1] == "wifi")
            {
                wifi = f;
                break;
            }
        }
        if (wifi is null)
        {
            return new NetworkStatus(NetworkMode.Unavailable, null, null, null);
        }

        var connection = wifi[3];
        if (!wifi[2].StartsWith("connected", StringComparison.Ordinal) || connection is "" or "--")
        {
            return new NetworkStatus(NetworkMode.Disconnected, null, null, null);
        }

        var ssid = (await ShowAsync(["connection", "show", "id", connection], ["802-11-wireless.ssid"], ct))
            .GetValueOrDefault("802-11-wireless.ssid");
        var addresses = await ShowAsync(["device", "show", Opt.WifiInterface], ["IP4.ADDRESS"], ct);
        var ip = addresses.Where(kv => kv.Key.StartsWith("IP4.ADDRESS", StringComparison.Ordinal))
            .Select(kv => kv.Value.Split('/')[0])
            .FirstOrDefault(v => v.Length > 0);

        var mode = connection == Opt.HotspotConnection ? NetworkMode.Hotspot : NetworkMode.Client;
        return new NetworkStatus(mode, connection, string.IsNullOrEmpty(ssid) ? connection : ssid, ip);
    }

    public override async Task<IReadOnlyList<WifiNetwork>> ScanAsync(bool rescan, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY", "device", "wifi", "list", "ifname", Opt.WifiInterface,
             "--rescan", rescan ? "yes" : "no"],
            TimeSpan.FromSeconds(30), ct);
        if (!result.Success)
        {
            throw new NetworkException(result.ErrorText);
        }

        var found = new List<WifiNetwork>();
        foreach (var line in Lines(result.StdOut))
        {
            var f = Terse.Split(line);
            if (f.Count >= 4 && f[1].Length > 0)
            {
                _ = int.TryParse(f[2], out var signal);
                found.Add(new WifiNetwork(f[1], signal, f[3], f[0].Trim() == "*"));
            }
        }

        // Several access points can share one SSID (mesh, repeaters): show the strongest one once.
        return found
            .GroupBy(n => n.Ssid)
            .Select(g => g.OrderByDescending(n => n.Signal).First() with { InUse = g.Any(n => n.InUse) })
            .OrderByDescending(n => n.InUse)
            .ThenByDescending(n => n.Signal)
            .ToList();
    }

    public override async Task<IReadOnlyList<SavedNetwork>> GetSavedNetworksAsync(CancellationToken ct = default)
    {
        var list = await RunAsync(["-t", "-f", "NAME,UUID,TYPE,ACTIVE", "connection", "show"], QuickTimeout, ct);
        if (!list.Success)
        {
            throw new NetworkException(list.ErrorText);
        }

        var saved = new List<SavedNetwork>();
        foreach (var line in Lines(list.StdOut))
        {
            var f = Terse.Split(line);
            if (f.Count < 4 || f[2] != "802-11-wireless")
            {
                continue;
            }
            var details = await ShowAsync(["connection", "show", "uuid", f[1]], ["802-11-wireless.mode", "802-11-wireless.ssid"], ct);
            if (details.GetValueOrDefault("802-11-wireless.mode") == "ap")
            {
                continue; // our own hotspot profile is not a "saved network"
            }
            var ssid = details.GetValueOrDefault("802-11-wireless.ssid");
            saved.Add(new SavedNetwork(f[0], f[1], string.IsNullOrEmpty(ssid) ? f[0] : ssid, f[3] == "yes"));
        }
        return saved;
    }

    /// <summary>Creates the hotspot profile if it is missing. With <paramref name="sync"/> an existing profile is also updated to the configured SSID/password.</summary>
    public async Task<bool> EnsureHotspotProfileAsync(bool sync)
    {
        if (!NetworkServiceBase.IsValidSsid(Opt.HotspotSsid) || Opt.HotspotPassword.Length is < 8 or > 63)
        {
            log.LogWarning("Hotspot SSID/password are not configured (RpiCam__HotspotSsid / RpiCam__HotspotPassword), cannot manage the hotspot profile");
            return false;
        }

        var names = await RunAsync(["-t", "-f", "NAME", "connection", "show"], QuickTimeout);
        var exists = names.Success && Lines(names.StdOut).Select(l => Terse.Split(l)[0]).Contains(Opt.HotspotConnection);
        if (exists)
        {
            if (!sync)
            {
                return true;
            }
            var modified = await RunAsync(
                ["connection", "modify", "id", Opt.HotspotConnection,
                 "802-11-wireless.ssid", Opt.HotspotSsid, "wifi-sec.psk", Opt.HotspotPassword],
                QuickTimeout);
            return LogIfFailed(modified, "update hotspot profile");
        }

        var created = await RunAsync(
            ["connection", "add", "type", "wifi", "ifname", Opt.WifiInterface,
             "con-name", Opt.HotspotConnection, "ssid", Opt.HotspotSsid,
             "connection.autoconnect", "yes", "connection.autoconnect-priority", "0",
             "802-11-wireless.mode", "ap", "802-11-wireless.band", "bg", "802-11-wireless.channel", "6",
             "ipv4.method", "shared", "ipv4.addresses", Opt.HotspotAddress, "ipv6.method", "disabled",
             "wifi-sec.key-mgmt", "wpa-psk", "wifi-sec.proto", "rsn",
             "wifi-sec.pairwise", "ccmp", "wifi-sec.group", "ccmp", "wifi-sec.psk", Opt.HotspotPassword],
            QuickTimeout);
        return LogIfFailed(created, "create hotspot profile");
    }

    protected override async Task ConnectAsync(string ssid, string? password)
    {
        // The fallback must exist before the hotspot is taken down.
        await EnsureHotspotProfileAsync(sync: false);

        foreach (var old in (await GetSavedNetworksAsync()).Where(n => n.Ssid == ssid))
        {
            await RunAsync(["connection", "delete", "uuid", old.Uuid], QuickTimeout);
        }

        var name = "rpicam-client-" + ssid;
        List<string> add =
        [
            "connection", "add", "type", "wifi", "ifname", Opt.WifiInterface, "con-name", name, "ssid", ssid,
            "connection.autoconnect", "yes",
            // Above the hotspot (0), so a known network in range wins at boot.
            "connection.autoconnect-priority", "10",
        ];
        if (password is not null)
        {
            add.AddRange(["wifi-sec.key-mgmt", "wpa-psk", "wifi-sec.psk", password]);
        }
        var added = await RunAsync(add, QuickTimeout);
        if (!LogIfFailed(added, "add client profile"))
        {
            Publish(NetworkOutcome.ConnectFailed, ssid);
            return;
        }

        if (await ActivateAsync(["id", name]))
        {
            Publish(NetworkOutcome.Connected, ssid);
            return;
        }

        // Wrong password or out of range: drop the new profile and go back to the hotspot.
        await RunAsync(["connection", "delete", "id", name], QuickTimeout);
        await FallbackToHotspotAsync(ssid);
    }

    protected override async Task ConnectSavedAsync(string uuid)
    {
        var ssid = (await GetSavedNetworksAsync()).FirstOrDefault(n => n.Uuid == uuid)?.Ssid;
        if (ssid is null)
        {
            Publish(NetworkOutcome.ConnectFailed, null);
            return;
        }
        await EnsureHotspotProfileAsync(sync: false);

        if (await ActivateAsync(["uuid", uuid]))
        {
            Publish(NetworkOutcome.Connected, ssid);
            return;
        }
        await FallbackToHotspotAsync(ssid);
    }

    protected override async Task RemoveAsync(string uuid)
    {
        var target = (await GetSavedNetworksAsync()).FirstOrDefault(n => n.Uuid == uuid);
        if (target is null)
        {
            return; // already gone, or not a client profile (e.g. the hotspot itself)
        }
        await EnsureHotspotProfileAsync(sync: false);

        var deleted = await RunAsync(["connection", "delete", "uuid", uuid], QuickTimeout);
        if (!LogIfFailed(deleted, "delete network"))
        {
            return;
        }

        // Deleting the active connection leaves the radio idle: back to the hotspot.
        var status = await GetStatusAsync();
        if (status.Mode != NetworkMode.Hotspot)
        {
            await ActivateAsync(["id", Opt.HotspotConnection]);
        }
        Publish(NetworkOutcome.Removed, target.Ssid);
    }

    protected override async Task HotspotAsync()
    {
        if (!await EnsureHotspotProfileAsync(sync: false))
        {
            Publish(NetworkOutcome.HotspotFailed, null);
            return;
        }
        Publish(await ActivateAsync(["id", Opt.HotspotConnection]) ? NetworkOutcome.HotspotActive : NetworkOutcome.HotspotFailed, null);
    }

    private async Task FallbackToHotspotAsync(string failedSsid)
    {
        var back = await ActivateAsync(["id", Opt.HotspotConnection]);
        Publish(back ? NetworkOutcome.ConnectFailed : NetworkOutcome.HotspotFailed, failedSsid);
    }

    /// <summary>Activates a profile (["id", name] or ["uuid", uuid]) and waits for the result.</summary>
    private async Task<bool> ActivateAsync(string[] profile)
    {
        var wait = Opt.ConnectTimeoutSeconds;
        var result = await RunAsync(
            ["--wait", wait.ToString(), "connection", "up", profile[0], profile[1], "ifname", Opt.WifiInterface],
            TimeSpan.FromSeconds(wait + 15));
        return LogIfFailed(result, "activate connection");
    }

    private Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default) =>
        runner.RunAsync(Nmcli, args, timeout, ct);

    /// <summary>Reads "key:value" lines from `nmcli -t -f fields ...`.</summary>
    private async Task<Dictionary<string, string>> ShowAsync(string[] command, string[] fields, CancellationToken ct)
    {
        var result = await RunAsync(["-t", "-f", string.Join(',', fields), .. command], QuickTimeout, ct);
        return result.Success ? Terse.KeyValues(result.StdOut) : [];
    }

    private bool LogIfFailed(ProcessResult result, string what)
    {
        if (!result.Success)
        {
            // nmcli error texts never contain the password, so this is safe to log.
            log.LogWarning("nmcli {What} failed ({Code}): {Error}", what, result.ExitCode, result.ErrorText);
        }
        return result.Success;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Parsing of nmcli's terse output, where ':' and '\' inside values are escaped with a backslash.</summary>
internal static class Terse
{
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                current.Append(line[++i]);
            }
            else if (line[i] == ':')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line[i]);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Parses "key:value" lines. Only the first colon separates the key, the rest is the (unescaped) value.</summary>
    public static Dictionary<string, string> KeyValues(string output)
    {
        var values = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                values[line[..colon]] = Unescape(line[(colon + 1)..]);
            }
        }
        return values;
    }

    private static string Unescape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            text.Append(value[i] == '\\' && i + 1 < value.Length ? value[++i] : value[i]);
        }
        return text.ToString();
    }
}

/// <summary>Development stand-in: pretends to be a Pi with a few networks around. A password starting with "wrong" fails.</summary>
public sealed class SimulatedNetworkService(ILogger<SimulatedNetworkService> log) : NetworkServiceBase(log)
{
    private static readonly WifiNetwork[] Around =
    [
        new("Home-WLAN", 82, "WPA2", false),
        new("Nachbar-5G", 55, "WPA1 WPA2", false),
        new("Cafe Free", 40, "", false),
        new("Firma-Corp", 60, "WPA2 802.1X", false),
    ];

    private readonly object _lock = new();
    private readonly List<SavedNetwork> _saved = [];
    private string? _activeUuid;

    public override Task<NetworkStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var active = _saved.FirstOrDefault(n => n.Uuid == _activeUuid);
            return Task.FromResult(active is null
                ? new NetworkStatus(NetworkMode.Hotspot, "rpicam-hotspot", "rpi_camera", "192.168.4.1")
                : new NetworkStatus(NetworkMode.Client, active.Name, active.Ssid, "192.168.178.50"));
        }
    }

    public override async Task<IReadOnlyList<WifiNetwork>> ScanAsync(bool rescan, CancellationToken ct = default)
    {
        await Task.Delay(rescan ? 1200 : 100, ct);
        lock (_lock)
        {
            var activeSsid = _saved.FirstOrDefault(n => n.Uuid == _activeUuid)?.Ssid;
            return Around.Select(n => n with { InUse = n.Ssid == activeSsid }).OrderByDescending(n => n.Signal).ToList();
        }
    }

    public override Task<IReadOnlyList<SavedNetwork>> GetSavedNetworksAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<SavedNetwork>>(
                _saved.Select(n => n with { Active = n.Uuid == _activeUuid }).ToList());
        }
    }

    protected override async Task ConnectAsync(string ssid, string? password)
    {
        await Task.Delay(2000);
        if (password?.StartsWith("wrong", StringComparison.Ordinal) == true)
        {
            SetActive(null);
            Publish(NetworkOutcome.ConnectFailed, ssid);
            return;
        }
        string uuid;
        lock (_lock)
        {
            _saved.RemoveAll(n => n.Ssid == ssid);
            uuid = Guid.NewGuid().ToString();
            _saved.Add(new SavedNetwork("rpicam-client-" + ssid, uuid, ssid, false));
        }
        SetActive(uuid);
        Publish(NetworkOutcome.Connected, ssid);
    }

    protected override async Task ConnectSavedAsync(string uuid)
    {
        await Task.Delay(1500);
        string? ssid;
        lock (_lock)
        {
            ssid = _saved.FirstOrDefault(n => n.Uuid == uuid)?.Ssid;
        }
        SetActive(ssid is null ? null : uuid);
        Publish(ssid is null ? NetworkOutcome.ConnectFailed : NetworkOutcome.Connected, ssid);
    }

    protected override Task RemoveAsync(string uuid)
    {
        string? ssid;
        lock (_lock)
        {
            ssid = _saved.FirstOrDefault(n => n.Uuid == uuid)?.Ssid;
            _saved.RemoveAll(n => n.Uuid == uuid);
            if (_activeUuid == uuid)
            {
                _activeUuid = null; // back to the hotspot
            }
        }
        if (ssid is not null)
        {
            Publish(NetworkOutcome.Removed, ssid);
        }
        return Task.CompletedTask;
    }

    protected override async Task HotspotAsync()
    {
        await Task.Delay(1000);
        SetActive(null);
        Publish(NetworkOutcome.HotspotActive, null);
    }

    private void SetActive(string? uuid)
    {
        lock (_lock)
        {
            _activeUuid = uuid;
        }
    }
}

/// <summary>Creates/updates the hotspot profile from the configuration when the app starts.</summary>
public sealed class HotspotBootstrapper(INetworkService network, ILogger<HotspotBootstrapper> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (network is not NmcliNetworkService real)
        {
            return;
        }
        try
        {
            await real.EnsureHotspotProfileAsync(sync: true);
        }
        catch (Exception ex)
        {
            // Not fatal: the profile from the first-boot script keeps working.
            log.LogWarning(ex, "Could not sync the hotspot profile");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
