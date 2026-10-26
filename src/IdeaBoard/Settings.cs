using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdeaBoard;

/// <summary>A mail domain the admin listed; JoinRole "" means only invited addresses from it get in.</summary>
public sealed class DomainRule
{
    public string Domain { get; set; } = "";
    public string JoinRole { get; set; } = "";
}

public sealed class LoginSettings
{
    public List<DomainRule> Domains { get; set; } = [];
    /// <summary>When on, Google logins from any other domain are refused, invited or not.</summary>
    public bool RestrictToDomains { get; set; }
}

public sealed record DomainInput(string? Domain, string? JoinRole);
public sealed record RestrictInput(bool Restrict);

/// <summary>data/settings.json — login rules per mail domain. Re-read when the file changes.</summary>
public sealed partial class SettingsStore(string dataDir)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string _path = Path.Combine(dataDir, "settings.json");
    readonly Lock _gate = new();
    LoginSettings _settings = new();
    DateTime _mtime = DateTime.MinValue;

    LoginSettings Load()
    {
        var mtime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (mtime != _mtime)
        {
            _settings = mtime == DateTime.MinValue ? new() : JsonSerializer.Deserialize<LoginSettings>(File.ReadAllText(_path), Json) ?? new();
            _mtime = mtime;
        }
        return _settings;
    }

    void Save()
    {
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_settings, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
        _mtime = File.GetLastWriteTimeUtc(_path);
    }

    public LoginSettings Current
    {
        get
        {
            lock (_gate)
            {
                var s = Load();
                return new LoginSettings
                {
                    RestrictToDomains = s.RestrictToDomains,
                    Domains = s.Domains.Select(d => new DomainRule { Domain = d.Domain, JoinRole = d.JoinRole }).ToList(),
                };
            }
        }
    }

    public static string DomainOf(string email) => email[(email.LastIndexOf('@') + 1)..].ToLowerInvariant();

    /// <summary>False when domain restriction is on and the address is outside the listed domains.</summary>
    public bool DomainAllowed(string email)
    {
        var s = Current;
        return !s.RestrictToDomains || s.Domains.Any(d => d.Domain == DomainOf(email));
    }

    /// <summary>Role for an uninvited address from an open domain, or null.</summary>
    public string? JoinRoleFor(string email) =>
        Current.Domains.FirstOrDefault(d => d.Domain == DomainOf(email)) is { JoinRole.Length: > 0 } rule ? rule.JoinRole : null;

    public void SetDomain(string? domain, string? joinRole)
    {
        var d = CleanDomain(domain);
        var role = joinRole ?? "";
        if (role is not ("" or Roles.Viewer or Roles.Editor))
            throw new BoardException("Z domeny można wpuszczać tylko jako viewer lub editor.");
        lock (_gate)
        {
            var s = Load();
            if (s.Domains.FirstOrDefault(x => x.Domain == d) is { } rule) rule.JoinRole = role;
            else if (s.Domains.Count >= 50) throw new BoardException("Za dużo domen (maks. 50).");
            else s.Domains.Add(new DomainRule { Domain = d, JoinRole = role });
            Save();
        }
    }

    public void RemoveDomain(string domain)
    {
        lock (_gate)
        {
            if (Load().Domains.RemoveAll(x => x.Domain == domain.Trim().TrimStart('@').ToLowerInvariant()) == 0)
                throw new BoardException("Nie ma takiej domeny na liście.", 404);
            Save();
        }
    }

    public void SetRestrict(bool restrict)
    {
        lock (_gate)
        {
            var s = Load();
            if (restrict && s.Domains.Count == 0)
                throw new BoardException("Najpierw dodaj co najmniej jedną domenę — inaczej nikt nie zaloguje się przez Google.");
            s.RestrictToDomains = restrict;
            Save();
        }
    }

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainPattern();

    static string CleanDomain(string? domain)
    {
        var d = (domain ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (!DomainPattern().IsMatch(d)) throw new BoardException("Nieprawidłowa domena (np. pyrkon.pl).");
        return d;
    }
}

public sealed record RefusedLogin(DateTimeOffset At, string Email, string Reason, string Ip);

/// <summary>The last refused Google logins, for the admin panel. Kept in memory only.</summary>
public sealed class RefusedLog
{
    const int Keep = 30;
    readonly ConcurrentQueue<RefusedLogin> _items = new();

    public void Add(string email, string reason, string ip)
    {
        _items.Enqueue(new RefusedLogin(DateTimeOffset.Now, email, reason, ip));
        while (_items.Count > Keep) _items.TryDequeue(out _);
    }

    public List<RefusedLogin> Recent() => _items.Reverse().ToList();
}

public static class DomainCli
{
    public static int Run(string dataDir, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var store = new SettingsStore(dataDir);
        try
        {
            switch (args)
            {
                case ["list"]:
                    var s = store.Current;
                    Console.WriteLine($"Blokada spoza domen: {(s.RestrictToDomains ? "WŁĄCZONA" : "wyłączona")}");
                    if (s.Domains.Count == 0) Console.WriteLine("Brak domen.");
                    foreach (var d in s.Domains)
                        Console.WriteLine($"  {d.Domain,-30} {(d.JoinRole.Length > 0 ? $"każdy jako {d.JoinRole}" : "tylko zaproszeni")}");
                    return 0;
                case ["add", var domain, ..]:
                    var role = args.Length > 2 ? args[2] : "";
                    if (role == "invited") role = "";
                    store.SetDomain(domain, role);
                    Console.WriteLine($"Domena {domain}: {(role.Length > 0 ? $"każdy wchodzi jako {role}" : "tylko zaproszeni")}.");
                    return 0;
                case ["remove", var domain]:
                    store.RemoveDomain(domain);
                    Console.WriteLine($"Usunięto domenę {domain}.");
                    return 0;
                case ["restrict", "on" or "off"]:
                    store.SetRestrict(args[1] == "on");
                    Console.WriteLine(args[1] == "on" ? "Logowanie Google tylko z listy domen." : "Blokada domen wyłączona.");
                    return 0;
            }
        }
        catch (BoardException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        Console.Error.WriteLine("""
            Użycie:
              IdeaBoard.exe domain list
              IdeaBoard.exe domain add <domena> [viewer|editor|invited]   (invited = tylko zaproszeni)
              IdeaBoard.exe domain remove <domena>
              IdeaBoard.exe domain restrict on|off
            """);
        return 2;
    }
}
