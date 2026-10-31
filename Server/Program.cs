using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Velom3d.Server;
using Velom3d.Sync;

// Serveur de synchronisation de Velom3d : comptes, réglages, entraînements et sorties de chaque
// joueur, dans une base SQLite. L'appli marche sans lui ; il ne fait que garder une copie et la
// partager entre les appareils d'un même compte. Réglages : appsettings.json ou variables
// d'environnement (Velom3d__Database, Velom3d__AllowRegistration…), voir Server/README.md.

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration.GetSection("Velom3d");
var dbPath = config["Database"] ?? "data/velom3d.db";
bool allowRegistration = config.GetValue("AllowRegistration", true);
int authPerMinute = config.GetValue("AuthRequestsPerMinute", 20);
bool trustProxy = config.GetValue("TrustProxy", false);

builder.Services.AddSingleton(new Database(dbPath));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, SyncJson.Default));
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Contre les essais de mots de passe en boucle : par adresse IP.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
if (trustProxy)
{
    // Derrière un proxy (Caddy, nginx…) qui fait le HTTPS : vraie adresse du client.
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fwd.KnownNetworks.Clear();
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}
app.UseRateLimiter();

var usernamePattern = new Regex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant);

static IResult Error(int status, string message) => Results.Json(new ErrorResponse { Error = message }, statusCode: status);

static string? BearerToken(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
}

static long? Auth(HttpContext ctx, Database db) => BearerToken(ctx) is { Length: > 0 } t ? db.UserForToken(Passwords.TokenHash(t)) : null;

static IResult Unauthorized() => Error(401, "Session expirée : reconnectez-vous.");

AccountResponse NewSession(Database db, long userId, string username)
{
    var token = Passwords.NewToken();
    db.AddToken(Passwords.TokenHash(token), userId);
    return new AccountResponse { Username = username, Token = token };
}

var api = app.MapGroup("/api");

api.MapGet("/info", () => new ServerInfo { Name = "velom3d", Version = 1, Registration = allowRegistration });

// ---- Comptes ----

var account = api.MapGroup("/account");

account.MapPost("/register", (AccountRequest req, Database db) =>
{
    if (!allowRegistration) return Error(403, "Ce serveur n'accepte pas de nouveaux comptes.");
    var username = req.Username.Trim();
    if (!usernamePattern.IsMatch(username))
        return Error(400, "Nom d'utilisateur : 3 à 32 caractères, lettres, chiffres, « . », « - » ou « _ ».");
    if (req.Password.Length is < 8 or > 200) return Error(400, "Le mot de passe doit faire au moins 8 caractères.");
    if (db.CreateUser(username, Passwords.Hash(req.Password)) is not { } id)
        return Error(409, "Ce nom d'utilisateur est déjà pris.");
    return Results.Json(NewSession(db, id, username), statusCode: 201);
}).RequireRateLimiting("auth");

account.MapPost("/login", (AccountRequest req, Database db) =>
{
    var user = db.FindUser(req.Username.Trim());
    // Même calcul que le compte existe ou non : pas d'indice par le temps de réponse.
    bool ok = Passwords.Verify(req.Password, user?.PasswordHash ?? Passwords.Dummy) && user is not null;
    if (!ok) return Error(401, "Nom d'utilisateur ou mot de passe incorrect.");
    return Results.Json(NewSession(db, user!.Value.Id, user.Value.Username));
}).RequireRateLimiting("auth");

account.MapPost("/logout", (HttpContext ctx, Database db) =>
{
    if (BearerToken(ctx) is { Length: > 0 } t) db.DeleteToken(Passwords.TokenHash(t));
    return Results.NoContent();
});

account.MapPost("/password", (PasswordChangeRequest req, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    var user = db.FindUser(db.Username(userId) ?? "");
    if (user is null || !Passwords.Verify(req.Password, user.Value.PasswordHash))
        return Error(401, "Mot de passe actuel incorrect.");
    if (req.NewPassword.Length is < 8 or > 200) return Error(400, "Le mot de passe doit faire au moins 8 caractères.");
    db.SetPassword(userId, Passwords.Hash(req.NewPassword));
    db.DeleteOtherTokens(userId, Passwords.TokenHash(BearerToken(ctx)!));
    return Results.NoContent();
}).RequireRateLimiting("auth");

// Suppression du compte et de toutes ses données (mot de passe demandé).
account.MapDelete("", ([Microsoft.AspNetCore.Mvc.FromBody] AccountRequest req, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    var user = db.FindUser(db.Username(userId) ?? "");
    if (user is null || !Passwords.Verify(req.Password, user.Value.PasswordHash))
        return Error(401, "Mot de passe incorrect.");
    db.DeleteUser(userId);
    return Results.NoContent();
}).RequireRateLimiting("auth");

// ---- Synchronisation ----

api.MapGet("/sync", (long? since, int? limit, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    return Results.Json(db.Pull(userId, Math.Max(0, since ?? 0), Math.Clamp(limit ?? 200, 1, 500)));
});

api.MapPost("/sync", (SyncPushRequest req, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    if (req.Changes.Count > SyncKinds.MaxChangesPerPush)
        return Error(400, $"Au plus {SyncKinds.MaxChangesPerPush} documents par envoi.");
    foreach (var ch in req.Changes)
    {
        if (!SyncKinds.IsKnown(ch.Kind) || !SyncKinds.IsValidId(ch.Id) ||
            (ch.Kind == SyncKinds.Profile && ch.Id != SyncKinds.ProfileId))
            return Error(400, $"Document invalide : {ch.Kind}/{ch.Id}.");
        if (ch.Deleted) continue;
        if (ch.Content.Length > SyncKinds.MaxContentChars) return Error(413, $"Document trop gros : {ch.Kind}/{ch.Id}.");
        try { using var _ = JsonDocument.Parse(ch.Content); }
        catch (JsonException) { return Error(400, $"Document illisible : {ch.Kind}/{ch.Id}."); }
    }
    return Results.Json(new SyncPushResponse { Accepted = db.Push(userId, req.Changes) });
});

// Fichier de données (CSV) d'une sortie, gardé compressé. Envoyé avant l'en-tête de la sortie.
api.MapPut("/rides/{id}/data", async (string id, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    if (!SyncKinds.IsValidId(id)) return Error(400, "Sortie invalide.");
    using var body = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(body);
    var bytes = body.ToArray();
    bool gzipped = ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);
    if (gzipped)
    {
        if (CheckGzip(bytes) is { } problem) return problem;
    }
    else
    {
        if (bytes.LongLength > SyncKinds.MaxRideDataBytes) return Error(413, "Fichier de données trop gros.");
        bytes = Gzip(bytes);
    }
    db.PutRideData(userId, id, bytes);
    return Results.NoContent();
});

api.MapGet("/rides/{id}/data", (string id, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    if (!SyncKinds.IsValidId(id) || db.GetRideData(userId, id) is not { } gz)
        return Error(404, "Pas de fichier de données pour cette sortie.");
    if (ctx.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers.ContentEncoding = "gzip";
        return Results.Bytes(gz, "text/csv; charset=utf-8");
    }
    using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
    using var output = new MemoryStream();
    input.CopyTo(output);
    return Results.Bytes(output.ToArray(), "text/csv; charset=utf-8");
});

app.Run();

static byte[] Gzip(byte[] data)
{
    using var output = new MemoryStream();
    using (var gz = new GZipStream(output, CompressionLevel.Optimal)) gz.Write(data);
    return output.ToArray();
}

/// <summary>Vérifie qu'un envoi compressé se décompresse, sans dépasser la taille maximale ; null si bon.</summary>
static IResult? CheckGzip(byte[] data)
{
    try
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = gz.Read(buffer)) > 0)
            if ((total += n) > SyncKinds.MaxRideDataBytes) return Error(413, "Fichier de données trop gros.");
        return null;
    }
    catch (Exception e) when (e is InvalidDataException or IOException) { return Error(400, "Fichier compressé illisible."); }
}

public sealed class PasswordChangeRequest
{
    public string Password { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

/// <summary>Point d'entrée, visible des tests (WebApplicationFactory).</summary>
public partial class Program;
