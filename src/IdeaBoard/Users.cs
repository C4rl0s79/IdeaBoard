using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace IdeaBoard;

public static class Roles
{
    public const string Admin = "admin";
    public const string Editor = "editor";
    public const string Viewer = "viewer";
    public static readonly string[] All = [Admin, Editor, Viewer];

    public static bool CanEdit(string role) => role is Admin or Editor;
}

/// <summary>An invited Google account. The lowercased e-mail is the user's key.</summary>
public sealed class User
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Editor;
    public string InvitedBy { get; set; } = "";
    public DateTimeOffset InvitedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>Whoever is signed in: an invited Google user, or the built-in password account.</summary>
public sealed record AppUser(string Key, string Name, string Email, string Role)
{
    public const string LocalAdminKey = "admin";
    public static readonly AppUser LocalAdmin = new(LocalAdminKey, "Administrator", "", Roles.Admin);
}

public sealed record InviteInput(string? Email, string? Role, string? Name);
public sealed record RoleInput(string? Role);

/// <summary>
/// data/users.json — the invite list. Re-read when the file changes, so `IdeaBoard.exe user …`
/// works while the service keeps running.
/// </summary>
public sealed class UserStore(string dataDir)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string _path = Path.Combine(dataDir, "users.json");
    readonly Lock _gate = new();
    List<User> _users = [];
    DateTime _mtime = DateTime.MinValue;

    List<User> Load()
    {
        var mtime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (mtime != _mtime)
        {
            _users = mtime == DateTime.MinValue ? [] : JsonSerializer.Deserialize<List<User>>(File.ReadAllText(_path), Json) ?? [];
            _mtime = mtime;
        }
        return _users;
    }

    void Save()
    {
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_users, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
        _mtime = File.GetLastWriteTimeUtc(_path);
    }

    static User Copy(User u) => new()
    {
        Email = u.Email, Name = u.Name, Role = u.Role, InvitedBy = u.InvitedBy, InvitedAt = u.InvitedAt, LastLoginAt = u.LastLoginAt,
    };

    public List<User> All()
    {
        lock (_gate) return Load().Select(Copy).ToList();
    }

    public User? Find(string email)
    {
        var key = email.Trim().ToLowerInvariant();
        lock (_gate) return Load().FirstOrDefault(u => u.Email == key) is { } u ? Copy(u) : null;
    }

    public User Invite(string? email, string? role, string? name, string invitedBy)
    {
        var key = CleanEmail(email);
        var r = CleanRole(role);
        lock (_gate)
        {
            var users = Load();
            if (users.Any(u => u.Email == key)) throw new BoardException("Ten adres jest już na liście.", 409);
            var user = new User
            {
                Email = key,
                Name = (name ?? "").Trim() is { Length: > 0 and <= 80 } n ? n : "",
                Role = r,
                InvitedBy = invitedBy,
                InvitedAt = DateTimeOffset.Now,
            };
            users.Add(user);
            Save();
            return Copy(user);
        }
    }

    public User SetRole(string email, string? role)
    {
        var r = CleanRole(role);
        lock (_gate)
        {
            var user = Load().FirstOrDefault(u => u.Email == email.ToLowerInvariant())
                ?? throw new BoardException("Nie ma takiego użytkownika.", 404);
            user.Role = r;
            Save();
            return Copy(user);
        }
    }

    public void Remove(string email)
    {
        lock (_gate)
        {
            if (Load().RemoveAll(u => u.Email == email.ToLowerInvariant()) == 0)
                throw new BoardException("Nie ma takiego użytkownika.", 404);
            Save();
        }
    }

    /// <summary>Called after Google sign-in; null when the address was never invited.</summary>
    public User? RecordLogin(string email, string? googleName)
    {
        lock (_gate)
        {
            var user = Load().FirstOrDefault(u => u.Email == email.Trim().ToLowerInvariant());
            if (user is null) return null;
            user.LastLoginAt = DateTimeOffset.Now;
            if (user.Name.Length == 0 && !string.IsNullOrWhiteSpace(googleName)) user.Name = googleName.Trim();
            Save();
            return Copy(user);
        }
    }

    static string CleanEmail(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        if (e.Length is 0 or > 254 || !MailAddress.TryCreate(e, out var parsed) || parsed.Address != e)
            throw new BoardException("Nieprawidłowy adres e-mail.");
        return e;
    }

    static string CleanRole(string? role) =>
        Roles.All.Contains(role) ? role! : throw new BoardException("Rola musi być: admin, editor lub viewer.");
}

public static class UserCli
{
    public static int Run(string dataDir, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var store = new UserStore(dataDir);
        try
        {
            switch (args)
            {
                case ["list"]:
                    var all = store.All();
                    if (all.Count == 0) Console.WriteLine("Brak zaproszonych użytkowników.");
                    foreach (var u in all)
                        Console.WriteLine($"{u.Email,-40} {u.Role,-7} {u.Name,-25} {(u.LastLoginAt is { } t ? $"ostatnio {t:yyyy-MM-dd HH:mm}" : "jeszcze się nie logował")}");
                    return 0;
                case ["add", var email, ..]:
                    var added = store.Invite(email, args.Length > 2 ? args[2] : Roles.Editor, null, "konsola");
                    Console.WriteLine($"Zaproszono {added.Email} ({added.Role}). Może się zalogować kontem Google.");
                    return 0;
                case ["role", var email, var role]:
                    store.SetRole(email, role);
                    Console.WriteLine($"{email}: rola {role}.");
                    return 0;
                case ["remove", var email]:
                    store.Remove(email);
                    Console.WriteLine($"Usunięto {email}. Jego sesje wygasną przy następnym żądaniu.");
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
              IdeaBoard.exe user list
              IdeaBoard.exe user add <email> [admin|editor|viewer]
              IdeaBoard.exe user role <email> <admin|editor|viewer>
              IdeaBoard.exe user remove <email>
            """);
        return 2;
    }
}
