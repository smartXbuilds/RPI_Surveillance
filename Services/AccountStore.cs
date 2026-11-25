using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RpiCam.Services;

public enum AccountCreateResult
{
    Created,
    AlreadyExists,
    InvalidUser,
    PasswordTooShort,
}

/// <summary>
/// The single admin account, persisted as account.json in the data directory. The password is only
/// stored as a salted PBKDF2 hash. As long as no account exists, the app shows the setup page
/// (/setup) instead of the login, so the first person to open it creates the account. Deleting the
/// file resets the device to that state.
/// </summary>
public sealed class AccountStore
{
    public const int MinPasswordLength = 8;
    public const int MaxUserLength = 64;

    // 100k rounds take a few hundred milliseconds on the Pi 3 A+: noticeable only at login.
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private sealed record StoredAccount(string User, string Salt, string Hash, int Iterations);

    private readonly string _path;
    private readonly ILogger<AccountStore> _log;
    private readonly Lock _lock = new();
    private StoredAccount? _account;

    // account.json exists but cannot be read. Treated as "has an account" so a broken file never
    // reopens the setup page to anybody on the network; nobody can sign in until it is deleted.
    private readonly bool _unreadable;

    public AccountStore(IOptions<RpiCamOptions> options, IOptions<AuthOptions> legacy, ILogger<AccountStore> log)
    {
        _log = log;
        _path = Path.Combine(options.Value.DataDir, "account.json");
        _account = Load(out _unreadable);

        // Devices set up before the setup page existed got their login from Auth__User / Auth__Password.
        // Take it over once so they keep working; from then on the file is what counts.
        if (_account is null && !_unreadable && !string.IsNullOrWhiteSpace(legacy.Value.User) && !string.IsNullOrEmpty(legacy.Value.Password))
        {
            _account = Hash(legacy.Value.User.Trim(), legacy.Value.Password);
            Save(_account);
            _log.LogInformation("Account taken over from the Auth configuration");
        }
    }

    public bool HasAccount => _unreadable || Volatile.Read(ref _account) is not null;

    public AccountCreateResult Create(string? user, string? password)
    {
        user = user?.Trim() ?? "";
        if (user.Length is 0 or > MaxUserLength)
        {
            return AccountCreateResult.InvalidUser;
        }
        if ((password?.Length ?? 0) < MinPasswordLength)
        {
            return AccountCreateResult.PasswordTooShort;
        }

        var account = Hash(user, password!);
        lock (_lock)
        {
            // Two browsers submitting the setup form at the same time: only the first one wins.
            if (HasAccount)
            {
                return AccountCreateResult.AlreadyExists;
            }
            Save(account);
            Volatile.Write(ref _account, account);
        }
        _log.LogInformation("Account {User} created", user);
        return AccountCreateResult.Created;
    }

    /// <summary>Returns the account name on success. Always does the full hash, so timing does not reveal what was wrong.</summary>
    public string? Verify(string? user, string? password)
    {
        var account = Volatile.Read(ref _account);
        if (account is null)
        {
            return null;
        }
        var salt = Convert.FromBase64String(account.Salt);
        var expected = Convert.FromBase64String(account.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, account.Iterations, HashAlgorithmName.SHA256, expected.Length);
        var passwordOk = CryptographicOperations.FixedTimeEquals(actual, expected);
        // Hashed first, so the comparison does not stop early on a different length.
        var userOk = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(user?.Trim() ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(account.User)));
        return passwordOk & userOk ? account.User : null;
    }

    private static StoredAccount Hash(string user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return new StoredAccount(user, Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
    }

    private StoredAccount? Load(out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            var account = JsonSerializer.Deserialize<StoredAccount>(File.ReadAllText(_path));
            if (account is { User.Length: > 0, Salt.Length: > 0, Hash.Length: > 0, Iterations: > 0 }
                // Throws FormatException now instead of at the first login.
                && Convert.FromBase64String(account.Salt).Length > 0 && Convert.FromBase64String(account.Hash).Length > 0)
            {
                return account;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException)
        {
            _log.LogError(ex, "Could not read {Path}", _path);
        }
        _log.LogError("{Path} is invalid, nobody can sign in. Delete it to create a new account", _path);
        unreadable = true;
        return null;
    }

    private void Save(StoredAccount account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
