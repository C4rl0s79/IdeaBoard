using System.Security.Claims;
using IdeaBoard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// Everything lives next to the exe: config in appsettings.json, state in data\.
var baseDir = AppContext.BaseDirectory;
var dataDir = Path.Combine(baseDir, "data");
Directory.CreateDirectory(dataDir);

switch (args)
{
    case ["set-password", ..]: return PasswordCli.Run(dataDir);
    case ["user", .. var rest]: return UserCli.Run(dataDir, rest);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir });
builder.Host.UseWindowsService(o => o.ServiceName = "IdeaBoard");

var config = builder.Configuration;
var googleId = config["Google:ClientId"];
var googleSecret = config["Google:ClientSecret"];
var googleEnabled = !string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret);
var passwordEnabled = config.GetValue("PasswordLogin", true);
var publicUrl = config["PublicUrl"] is { Length: > 0 } pu ? new Uri(pu) : null;

var users = new UserStore(dataDir);
var authStore = new AuthStore(dataDir);

string DisplayName(string key) =>
    key == AppUser.LocalAdminKey ? AppUser.LocalAdmin.Name
    : users.Find(key) is { } u ? (u.Name.Length > 0 ? u.Name : u.Email)
    : key;

builder.Services.AddSingleton(new BoardStore(dataDir, DisplayName));
builder.Services.AddSingleton(users);
builder.Services.AddSingleton(authStore);
builder.Services.AddSingleton<LoginThrottle>();

// Keys stay in data\keys so logins survive restarts; DPAPI-encrypted for this machine.
builder.Services.AddDataProtection()
    .SetApplicationName("IdeaBoard")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .ProtectKeysWithDpapi(protectToLocalMachine: true);

static ClaimsPrincipal AppPrincipal(string key, string stamp) => new(new ClaimsIdentity(
    [new Claim(ClaimTypes.NameIdentifier, key), new Claim("stamp", stamp)],
    CookieAuthenticationDefaults.AuthenticationScheme));

var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "ideaboard";
    o.Cookie.HttpOnly = true;
    // Lax, not Strict: the session cookie must survive the redirect back from Google.
    // Cross-site writes are still blocked by the X-IdeaBoard header check.
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    // A removed user, a changed admin password or a disabled login method ends the session.
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var key = ctx.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var valid = key == AppUser.LocalAdminKey
            ? passwordEnabled && authStore.Current is { } a && ctx.Principal!.FindFirst("stamp")?.Value == a.Stamp
            : googleEnabled && key is not null && users.Find(key) is not null;
        if (!valid)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync();
        }
    };
});

if (googleEnabled)
{
    authBuilder.AddGoogle(o =>
    {
        o.ClientId = googleId!;
        o.ClientSecret = googleSecret!;
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ClaimActions.MapJsonKey("email_verified", "email_verified");
        o.Events.OnRedirectToAuthorizationEndpoint = ctx =>
        {
            ctx.Response.Redirect(ctx.RedirectUri + "&prompt=select_account");
            return Task.CompletedTask;
        };
        // Only invited addresses get in; the Google identity is swapped for our own minimal principal.
        o.Events.OnTicketReceived = ctx =>
        {
            var email = ctx.Principal?.FindFirst(ClaimTypes.Email)?.Value ?? "";
            var verified = string.Equals(ctx.Principal?.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            var user = verified ? users.RecordLogin(email, ctx.Principal?.FindFirst(ClaimTypes.Name)?.Value) : null;
            if (user is null)
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
                    .LogWarning("Google login refused for {Email} (verified: {Verified})", email, verified);
                ctx.Response.Redirect("/?denied=" + Uri.EscapeDataString(email));
                ctx.HandleResponse();
                return Task.CompletedTask;
            }
            ctx.Principal = AppPrincipal(user.Email, "google");
            ctx.Properties!.IsPersistent = true;
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/?denied=");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization();

// cloudflared connects from loopback; trust its X-Forwarded-* (only loopback proxies are trusted by default).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

app.UseForwardedHeaders();

// PublicUrl pins the address Google redirects back to, whatever Host header arrives.
if (publicUrl is not null)
{
    app.Use((ctx, next) =>
    {
        ctx.Request.Scheme = publicUrl.Scheme;
        ctx.Request.Host = HostString.FromUriComponent(publicUrl);
        return next();
    });
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h.XFrameOptions = "DENY";

    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        h.CacheControl = "no-store";
        // CSRF guard: a cross-site form cannot set custom headers.
        if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-IdeaBoard"] != "1")
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        try
        {
            await next();
        }
        catch (BoardException e)
        {
            ctx.Response.StatusCode = e.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
        }
        return;
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

AppUser? CurrentUser(HttpContext ctx)
{
    if (ctx.User.Identity?.IsAuthenticated != true) return null;
    var key = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (key == AppUser.LocalAdminKey) return AppUser.LocalAdmin;
    return key is not null && users.Find(key) is { } u ? new AppUser(u.Email, DisplayName(u.Email), u.Email, u.Role) : null;
}

// Role checks for writes: viewers read only; users and columns are admin-only.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method)
        && !path.StartsWithSegments("/api/login") && !path.StartsWithSegments("/api/logout"))
    {
        var user = CurrentUser(ctx);
        var adminOnly = path.StartsWithSegments("/api/users") || path.StartsWithSegments("/api/columns");
        if (user is not null && (adminOnly ? user.Role != Roles.Admin : !Roles.CanEdit(user.Role)))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "Brak uprawnień do tej operacji." });
            return;
        }
    }
    await next();
});

// --- UI (embedded in the exe) ---

var assets = new Dictionary<string, (byte[] Body, string Type)>();
foreach (var (name, type) in new[]
{
    ("index.html", "text/html; charset=utf-8"),
    ("app.js", "text/javascript; charset=utf-8"),
    ("app.css", "text/css; charset=utf-8"),
    ("favicon.svg", "image/svg+xml"),
})
{
    using var stream = typeof(BoardStore).Assembly.GetManifestResourceStream("web." + name)
        ?? throw new InvalidOperationException($"Missing embedded web/{name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    assets[name] = (ms.ToArray(), type);
}

IResult Asset(HttpContext ctx, string name)
{
    if (!assets.TryGetValue(name, out var a)) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Bytes(a.Body, a.Type);
}

app.MapGet("/", ctx => Asset(ctx, "index.html").ExecuteAsync(ctx));
app.MapGet("/{file}", (HttpContext ctx, string file) => Asset(ctx, file));

if (googleEnabled)
{
    app.MapGet("/auth/google", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" },
        [GoogleDefaults.AuthenticationScheme]));
}

// --- API ---

static IResult Board(string json) => Results.Content(json, "application/json; charset=utf-8");

static string ClientIp(HttpContext ctx) =>
    ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } ip ? ip
    : ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

object UserView(User u) => new
{
    key = u.Email, name = DisplayName(u.Email), email = u.Email, role = u.Role,
    invitedBy = DisplayName(u.InvitedBy), invitedAt = u.InvitedAt, lastLoginAt = u.LastLoginAt,
};

object UsersView()
{
    var list = users.All().Select(UserView).ToList();
    if (passwordEnabled) list.Insert(0, new { key = AppUser.LocalAdminKey, name = AppUser.LocalAdmin.Name, email = "", role = Roles.Admin });
    return list;
}

var pub = app.MapGroup("/api");

pub.MapGet("/me", (HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    return new
    {
        authenticated = user is not null,
        user,
        google = googleEnabled,
        password = passwordEnabled,
        passwordSet = authStore.Current is not null,
    };
});

pub.MapPost("/login", async (LoginInput input, HttpContext ctx, LoginThrottle throttle) =>
{
    if (!passwordEnabled)
        return Results.Json(new { error = "Logowanie hasłem jest wyłączone." }, statusCode: 403);

    var ip = ClientIp(ctx);
    if (throttle.IsBlocked(ip))
        return Results.Json(new { error = "Za dużo nieudanych prób. Spróbuj ponownie za 15 minut." }, statusCode: 429);

    var current = authStore.Current;
    if (current is null)
        return Results.Json(new { error = "Hasło nie jest ustawione. Na serwerze uruchom: IdeaBoard.exe set-password" }, statusCode: 503);

    if (!Passwords.Verify(input.Password ?? "", current.Hash))
    {
        throttle.Fail(ip);
        app.Logger.LogWarning("Failed login from {Ip}", ip);
        await Task.Delay(500);
        return Results.Json(new { error = "Nieprawidłowe hasło." }, statusCode: 401);
    }

    throttle.Succeed(ip);
    await ctx.SignInAsync(AppPrincipal(AppUser.LocalAdminKey, current.Stamp), new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { ok = true });
});

pub.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { ok = true });
});

var api = app.MapGroup("/api").RequireAuthorization();

// Authorization already passed; a null user only happens if the account vanished mid-request.
AppUser Me(HttpContext ctx) => CurrentUser(ctx) ?? throw new BoardException("Sesja wygasła.", 401);

void CheckAssignee(CardInput input)
{
    if (string.IsNullOrEmpty(input.Assignee)) return;
    if (input.Assignee == AppUser.LocalAdminKey ? !passwordEnabled : users.Find(input.Assignee) is null)
        throw new BoardException("Odpowiedzialny musi być użytkownikiem tablicy.");
}

api.MapGet("/board", (BoardStore s) => Board(s.Snapshot()));
api.MapGet("/export", (BoardStore s) => Results.File(System.Text.Encoding.UTF8.GetBytes(s.Snapshot()),
    "application/json", $"ideaboard-{DateTime.Now:yyyy-MM-dd}.json"));

api.MapPost("/cards", (CardInput input, BoardStore s, HttpContext ctx) =>
{
    CheckAssignee(input);
    return Board(s.AddCard(input, Me(ctx).Key));
});
api.MapPut("/cards/{id}", (string id, CardInput input, BoardStore s, HttpContext ctx) =>
{
    CheckAssignee(input);
    return Board(s.UpdateCard(id, input, Me(ctx).Key));
});
api.MapPost("/cards/{id}/move", (string id, MoveInput input, BoardStore s, HttpContext ctx) => Board(s.MoveCard(id, input, Me(ctx).Key)));
api.MapDelete("/cards/{id}", (string id, BoardStore s) => Board(s.DeleteCard(id)));
api.MapPost("/cards/{id}/notes", (string id, NoteInput input, BoardStore s, HttpContext ctx) => Board(s.AddNote(id, input, Me(ctx).Key)));
api.MapDelete("/cards/{id}/notes/{noteId}", (string id, string noteId, BoardStore s, HttpContext ctx) =>
{
    var me = Me(ctx);
    return Board(s.DeleteNote(id, noteId, me.Key, me.Role == Roles.Admin));
});

api.MapPost("/columns", (NameInput input, BoardStore s) => Board(s.AddColumn(input)));
api.MapPut("/columns/{id}", (string id, NameInput input, BoardStore s) => Board(s.RenameColumn(id, input)));
api.MapPost("/columns/{id}/move", (string id, ColumnMoveInput input, BoardStore s) => Board(s.MoveColumn(id, input)));
api.MapDelete("/columns/{id}", (string id, BoardStore s) => Board(s.DeleteColumn(id)));

api.MapPost("/projects", (NameInput input, BoardStore s) => Board(s.AddProject(input)));
api.MapPut("/projects/{id}", (string id, NameInput input, BoardStore s) => Board(s.RenameProject(id, input)));
api.MapDelete("/projects/{id}", (string id, BoardStore s, HttpContext ctx) => Board(s.DeleteProject(id, Me(ctx).Key)));

api.MapGet("/users", () => UsersView());
api.MapPost("/users", (InviteInput input, HttpContext ctx) =>
{
    users.Invite(input.Email, input.Role, input.Name, Me(ctx).Key);
    return UsersView();
});
api.MapPut("/users/{email}", (string email, RoleInput input, HttpContext ctx) =>
{
    if (email == Me(ctx).Key) throw new BoardException("Nie możesz zmienić własnej roli.");
    users.SetRole(email, input.Role);
    return UsersView();
});
api.MapDelete("/users/{email}", (string email, HttpContext ctx) =>
{
    if (email == Me(ctx).Key) throw new BoardException("Nie możesz usunąć samego siebie.");
    users.Remove(email);
    return UsersView();
});

app.Logger.LogInformation("Login: Google {Google}, password {Password}", googleEnabled ? "on" : "off", passwordEnabled ? "on" : "off");
app.Run();
return 0;

sealed record LoginInput(string? Password);
