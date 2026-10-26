using System.Security.Claims;
using IdeaBoard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// Everything lives next to the exe: config in appsettings.json, state in data\.
var baseDir = AppContext.BaseDirectory;
var dataDir = Path.Combine(baseDir, "data");
Directory.CreateDirectory(dataDir);

if (args is ["set-password", ..])
    return PasswordCli.Run(dataDir);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir });
builder.Host.UseWindowsService(o => o.ServiceName = "IdeaBoard");

builder.Services.AddSingleton(new BoardStore(dataDir));
builder.Services.AddSingleton(new AuthStore(dataDir));
builder.Services.AddSingleton<LoginThrottle>();

// Keys stay in data\keys so logins survive restarts; DPAPI-encrypted for this machine.
builder.Services.AddDataProtection()
    .SetApplicationName("IdeaBoard")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "ideaboard";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var current = ctx.HttpContext.RequestServices.GetRequiredService<AuthStore>().Current;
        if (current is null || ctx.Principal?.FindFirst("stamp")?.Value != current.Stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddAuthorization();

// cloudflared connects from loopback; trust its X-Forwarded-* (only loopback proxies are trusted by default).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

app.UseForwardedHeaders();

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

// --- API ---

static IResult Board(string json) => Results.Content(json, "application/json; charset=utf-8");

static string ClientIp(HttpContext ctx) =>
    ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } ip ? ip
    : ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

var pub = app.MapGroup("/api");

pub.MapGet("/me", (HttpContext ctx, AuthStore auth) => new
{
    authenticated = ctx.User.Identity?.IsAuthenticated == true,
    passwordSet = auth.Current is not null,
});

pub.MapPost("/login", async (LoginInput input, HttpContext ctx, AuthStore auth, LoginThrottle throttle) =>
{
    var ip = ClientIp(ctx);
    if (throttle.IsBlocked(ip))
        return Results.Json(new { error = "Za dużo nieudanych prób. Spróbuj ponownie za 15 minut." }, statusCode: 429);

    var current = auth.Current;
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
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner"), new Claim("stamp", current.Stamp)],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { ok = true });
});

pub.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { ok = true });
});

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/board", (BoardStore s) => Board(s.Snapshot()));
api.MapGet("/export", (BoardStore s) => Results.File(System.Text.Encoding.UTF8.GetBytes(s.Snapshot()),
    "application/json", $"ideaboard-{DateTime.Now:yyyy-MM-dd}.json"));

api.MapPost("/cards", (CardInput input, BoardStore s) => Board(s.AddCard(input)));
api.MapPut("/cards/{id}", (string id, CardInput input, BoardStore s) => Board(s.UpdateCard(id, input)));
api.MapPost("/cards/{id}/move", (string id, MoveInput input, BoardStore s) => Board(s.MoveCard(id, input)));
api.MapDelete("/cards/{id}", (string id, BoardStore s) => Board(s.DeleteCard(id)));
api.MapPost("/cards/{id}/notes", (string id, NoteInput input, BoardStore s) => Board(s.AddNote(id, input)));
api.MapDelete("/cards/{id}/notes/{noteId}", (string id, string noteId, BoardStore s) => Board(s.DeleteNote(id, noteId)));

api.MapPost("/columns", (ColumnInput input, BoardStore s) => Board(s.AddColumn(input)));
api.MapPut("/columns/{id}", (string id, ColumnInput input, BoardStore s) => Board(s.RenameColumn(id, input)));
api.MapPost("/columns/{id}/move", (string id, ColumnMoveInput input, BoardStore s) => Board(s.MoveColumn(id, input)));
api.MapDelete("/columns/{id}", (string id, BoardStore s) => Board(s.DeleteColumn(id)));

app.Run();
return 0;

sealed record LoginInput(string? Password);
