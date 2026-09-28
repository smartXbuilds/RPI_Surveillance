using System.Collections.Concurrent;

namespace RpiCam.Services;

/// <summary>Locks a client address out for a minute after 5 wrong logins in a row.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntil)> _clients = new();

    public bool IsLocked(string client) =>
        _clients.TryGetValue(client, out var state) && state.LockedUntil > DateTime.UtcNow;

    public void RegisterFailure(string client)
    {
        _clients.AddOrUpdate(
            client,
            _ => (1, DateTime.MinValue),
            (_, old) =>
            {
                var failures = old.LockedUntil > DateTime.MinValue && old.LockedUntil <= DateTime.UtcNow ? 1 : old.Failures + 1;
                return failures >= MaxFailures ? (0, DateTime.UtcNow + LockDuration) : (failures, DateTime.MinValue);
            });
    }

    public void Reset(string client) => _clients.TryRemove(client, out _);
}
