using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdeaBoard;

public sealed record AuthFile(string Hash, string Stamp);

/// <summary>
/// data/auth.json holds the password hash and a session stamp. Setting a new password changes
/// the stamp, which signs out every existing session. Re-read when the file changes, so
/// `IdeaBoard.exe set-password` takes effect while the service keeps running.
/// </summary>
public sealed class AuthStore(string dataDir)
{
    readonly string _path = Path.Combine(dataDir, "auth.json");
    readonly Lock _gate = new();
    AuthFile? _cached;
    DateTime _mtime = DateTime.MinValue;

    public AuthFile? Current
    {
        get
        {
            lock (_gate)
            {
                var mtime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
                if (mtime != _mtime)
                {
                    _cached = mtime == DateTime.MinValue ? null : JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(_path), JsonSerializerOptions.Web);
                    _mtime = mtime;
                }
                return _cached;
            }
        }
    }

    public static void Write(string dataDir, string password)
    {
        var file = new AuthFile(Passwords.Hash(password), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        var path = Path.Combine(dataDir, "auth.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file, JsonSerializerOptions.Web));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public static class Passwords
{
    const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Blocks an address for 15 minutes after 5 failed logins.</summary>
public sealed class LoginThrottle
{
    const int MaxFails = 5;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    sealed class Entry
    {
        public int Fails;
        public DateTime First = DateTime.UtcNow;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool IsBlocked(string ip)
    {
        if (!_entries.TryGetValue(ip, out var e)) return false;
        lock (e)
        {
            if (DateTime.UtcNow - e.First <= Window) return e.Fails >= MaxFails;
        }
        _entries.TryRemove(ip, out _);
        return false;
    }

    public void Fail(string ip)
    {
        if (_entries.Count > 10_000)
            foreach (var (key, old) in _entries)
                if (DateTime.UtcNow - old.First > Window) _entries.TryRemove(key, out _);

        var e = _entries.GetOrAdd(ip, _ => new Entry());
        lock (e)
        {
            if (DateTime.UtcNow - e.First > Window) { e.Fails = 0; e.First = DateTime.UtcNow; }
            e.Fails++;
        }
    }

    public void Succeed(string ip) => _entries.TryRemove(ip, out _);
}

public static class PasswordCli
{
    public static int Run(string dataDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Write("Nowe hasło (min. 10 znaków): ");
        var first = ReadHidden();
        Console.Write("Powtórz hasło: ");
        var second = ReadHidden();
        if (first != second)
        {
            Console.Error.WriteLine("Hasła się różnią — nic nie zmieniono.");
            return 1;
        }
        if (first.Length < 10)
        {
            Console.Error.WriteLine("Hasło jest za krótkie — nic nie zmieniono.");
            return 1;
        }
        AuthStore.Write(dataDir, first);
        Console.WriteLine("Hasło zapisane. Dotychczasowe sesje zostały wylogowane.");
        return 0;
    }

    static string ReadHidden()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
