using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Velom3d.Server;
using Velom3d.Sync;

// Serveur de synchronisation de Velom3d : comptes, réglages, entraînements et sorties de chaque
// joueur, dans une base SQLite. L'appli marche sans lui ; il ne fait que garder une copie et la
// partager entre les appareils d'un même compte. Réglages : appsettings.json ou variables
// d'environnement (Velom3d__Database, Velom3d__AllowRegistration…), voir Server/README.md.

// Commandes d'administration (« dotnet Velom3d.Server.dll recovery-code <nom> »…) : voir Admin.cs.
bool isAdminCommand = Admin.IsCommand(args);
var builder = WebApplication.CreateBuilder(isAdminCommand ? args[Admin.ArgCount(args)..] : args);
var config = builder.Configuration.GetSection("Velom3d");
var dbPath = config["Database"] ?? "data/velom3d.db";
if (isAdminCommand) return Admin.Run(args, new Database(dbPath));

bool allowRegistration = config.GetValue("AllowRegistration", true);
int freeAttempts = config.GetValue("MaxFailedLogins", 3);
var maxLock = TimeSpan.FromMinutes(config.GetValue("LockoutMaxMinutes", 60));
var sessionIdle = TimeSpan.FromDays(config.GetValue("SessionDays", 90));
int backupsKept = config.GetValue("Backups", 7);
var backupDir = config["BackupDirectory"] is { Length: > 0 } bd ? bd
    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "backups");
bool trustProxy = config.GetValue("TrustProxy", false);

// Sans adresse précisée (--urls, ASPNETCORE_URLS…) : port 8080 sur toutes les interfaces, pour que
// les téléphones du réseau local le trouvent (par défaut ASP.NET n'écoute que localhost).
bool defaultUrls = string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"]);
if (defaultUrls) builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.AddSingleton(new Database(dbPath));
builder.Services.AddSingleton(sp => new Mailer(config.GetSection("Smtp"), sp.GetRequiredService<ILogger<Mailer>>()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, SyncJson.Default));
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024 * 1024);

var app = builder.Build();
var mailer = app.Services.GetRequiredService<Mailer>();
if (trustProxy)
{
    // Derrière un proxy (Caddy, nginx…) qui fait le HTTPS : vraie adresse du client.
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fwd.KnownNetworks.Clear();
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}
app.Lifetime.ApplicationStarted.Register(() =>
{
    // Adresses à saisir dans l'appli (Réglages > Synchronisation) depuis le réseau local.
    var listening = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
    var lan = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
        .ToList();
    foreach (var address in listening)
    {
        if (!Uri.TryCreate(address.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out var u)) continue;
        if (u.IsLoopback || u.Host == "localhost")
            app.Logger.LogWarning("Le serveur n'écoute que ce PC ({Address}) : les téléphones ne le verront pas. Lancez-le avec --urls http://0.0.0.0:8080.", address);
        else if (u.Host is "0.0.0.0" or "[::]")
            foreach (var ip in lan)
                app.Logger.LogInformation("Adresse pour l'appli : {Scheme}://{Ip}:{Port}", u.Scheme, ip, u.Port);
    }
});

var usernamePattern = new Regex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant);
var emailPattern = new Regex(@"^[^@\s]{1,64}@[^@\s]+\.[^@\s]{2,}$", RegexOptions.CultureInvariant);
var resetValidity = TimeSpan.FromMinutes(30);

static IResult Error(int status, string message) => Results.Json(new ErrorResponse { Error = message }, statusCode: status);

static string? BearerToken(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
}

long? Auth(HttpContext ctx, Database db) =>
    BearerToken(ctx) is { Length: > 0 } t ? db.UserForToken(Passwords.TokenHash(t), sessionIdle) : null;

static IResult Unauthorized() => Error(401, "Session expirée : reconnectez-vous.");

static IResult Locked(HttpContext ctx, DateTimeOffset until)
{
    var wait = Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalMinutes));
    ctx.Response.Headers.RetryAfter = (wait * 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
    return Error(429, $"Trop d'essais pour ce compte : réessayez dans {wait} min.");
}

static string FailureKey(string kind, string username) => kind + ":" + username.Trim().ToLowerInvariant();

AccountResponse NewSession(Database db, long userId, string username)
{
    var token = Passwords.NewToken();
    db.AddToken(Passwords.TokenHash(token), userId);
    return new AccountResponse { Username = username, Token = token };
}

// Vérifie le mot de passe du compte, avec le blocage après plusieurs échecs ; null si bon.
// Mauvais mot de passe : 401 à la connexion ; 403 sur une route déjà connectée (le 401 y veut dire
// « session expirée », et l'appli oublie alors son jeton).
// Même calcul que le compte existe ou non : ni le temps de réponse ni le blocage n'indiquent si un nom existe.
IResult? CheckPassword(HttpContext ctx, Database db, string username, string password,
    UserRow? user, string wrongMessage, int wrongStatus = 403)
{
    var key = FailureKey("login", username);
    if (db.LockedUntil(key) is { } locked) return Locked(ctx, locked);
    if (Passwords.Verify(password, user?.PasswordHash ?? Passwords.Dummy) && user is not null)
    {
        db.ClearFailures(key);
        if (Passwords.NeedsRehash(user.PasswordHash)) db.SetPassword(user.Id, Passwords.Hash(password));
        return null;
    }
    return Failure(db, key, user) is { } until
        ? Locked(ctx, until)
        : Error(wrongStatus, wrongMessage);
}

// Nom de l'appareil (envoyé par l'appli) et adresse du client, pour les alertes par e-mail.
static string? DeviceOf(HttpContext ctx) =>
    ctx.Request.Headers["X-Velom3d-Device"].ToString() is { Length: > 0 } d ? new string(d.Take(64).Where(c => !char.IsControl(c)).ToArray()) : null;
static string? IpOf(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

// Échec noté ; au premier blocage d'une série, le propriétaire du compte est prévenu par e-mail.
DateTimeOffset? Failure(Database db, string key, UserRow? user)
{
    var until = db.RecordFailure(key, freeAttempts, maxLock, TimeSpan.FromDays(1), out bool justLocked);
    if (justLocked && until is { } u && user is not null) mailer.NotifyLocked(user.Email, user.Username, u);
    return until;
}

// Pour les routes qui redemandent le mot de passe : le compte du jeton.
UserRow? CurrentUser(Database db, long userId) =>
    db.Username(userId) is { } name ? db.FindUser(name) : null;

var api = app.MapGroup("/api");

api.MapGet("/info", () => new ServerInfo { Name = "velom3d", Version = 1, Registration = allowRegistration, Recovery = true, Email = mailer.Enabled });

// ---- Comptes ----

var account = api.MapGroup("/account");

account.MapPost("/register", (AccountRequest req, Database db) =>
{
    if (!allowRegistration) return Error(403, "Ce serveur n'accepte pas de nouveaux comptes.");
    var username = req.Username.Trim();
    if (!usernamePattern.IsMatch(username))
        return Error(400, "Nom d'utilisateur : 3 à 32 caractères, lettres, chiffres, « . », « - » ou « _ ».");
    if (PasswordRules.Problem(req.Password, username) is { } problem) return Error(400, problem);
    var email = req.Email.Trim();
    if (email.Length > 0 && (email.Length > 254 || !emailPattern.IsMatch(email))) return Error(400, "Adresse e-mail invalide.");
    var code = Passwords.NewRecoveryCode();
    if (db.CreateUser(username, Passwords.Hash(req.Password), Passwords.RecoveryHash(code), email.Length > 0 ? email : null) is not { } id)
        return Error(409, "Ce nom d'utilisateur est déjà pris.");
    var res = NewSession(db, id, username);
    res.RecoveryCode = code;
    res.Email = email;
    return Results.Json(res, statusCode: 201);
}); // pas de limite sur la création de comptes ; AllowRegistration=false pour fermer le serveur

account.MapPost("/login", (AccountRequest req, HttpContext ctx, Database db) =>
{
    var user = db.FindUser(req.Username.Trim());
    if (CheckPassword(ctx, db, req.Username, req.Password, user, "Nom d'utilisateur ou mot de passe incorrect.", 401) is { } fail)
        return fail;
    var res = NewSession(db, user!.Id, user.Username);
    res.Email = user.Email ?? "";
    mailer.NotifyLogin(user.Email, user.Username, DeviceOf(ctx), IpOf(ctx));
    return Results.Json(res);
});

// Mot de passe oublié, 1re étape facultative : un code envoyé à l'adresse e-mail du compte.
// La réponse est toujours la même, que le compte existe ou non, ait une adresse ou non.
account.MapPost("/forgot", (ForgotRequest req, Database db) =>
{
    if (!mailer.Enabled) return Error(400, "Ce serveur n'envoie pas d'e-mails : utilisez votre code de secours.");
    if (db.FindUser(req.Username.Trim()) is { Email: { } email } user
        // Pas plus d'un e-mail par minute pour un même compte.
        && !(user.ResetExpires is { } exp && exp - resetValidity > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)))
    {
        var code = Passwords.NewResetCode();
        db.SetResetCode(user.Id, Passwords.RecoveryHash(code), DateTimeOffset.UtcNow + resetValidity);
        mailer.SendResetCode(email, user.Username, code, resetValidity);
    }
    return Results.Accepted();
});

// Mot de passe oublié : le code de secours, ou le code reçu par e-mail, permet d'en choisir un
// nouveau. Tous les appareils sont déconnectés. Un code ne sert qu'une fois : après un code de
// secours, un nouveau le remplace (renvoyé dans la réponse).
account.MapPost("/recover", (RecoverRequest req, HttpContext ctx, Database db) =>
{
    var key = FailureKey("recover", req.Username);
    if (db.LockedUntil(key) is { } locked) return Locked(ctx, locked);
    var user = db.FindUser(req.Username.Trim());
    var given = Passwords.RecoveryHash(req.RecoveryCode);
    bool bySecours = SameHash(given, user?.RecoveryHash);
    bool byEmail = SameHash(given, user?.ResetHash) && user!.ResetExpires > DateTimeOffset.UtcNow;
    if (!bySecours && !byEmail)
        return Failure(db, key, user) is { } until
            ? Locked(ctx, until)
            : Error(401, "Nom d'utilisateur ou code incorrect (un code reçu par e-mail ne vaut que 30 minutes).");
    if (PasswordRules.Problem(req.NewPassword, user!.Username) is { } problem) return Error(400, problem);
    db.ClearFailures(key);
    db.ClearFailures(FailureKey("login", user.Username));
    db.SetPassword(user.Id, Passwords.Hash(req.NewPassword));
    db.SetResetCode(user.Id, null, null);
    db.DeleteAllTokens(user.Id);
    var res = NewSession(db, user.Id, user.Username);
    res.Email = user.Email ?? "";
    if (bySecours)
    {
        res.RecoveryCode = Passwords.NewRecoveryCode();
        db.SetRecoveryHash(user.Id, Passwords.RecoveryHash(res.RecoveryCode));
    }
    mailer.NotifyPasswordChanged(user.Email, user.Username, bySecours ? "avec le code de secours" : "avec un code reçu par e-mail",
        DeviceOf(ctx), IpOf(ctx));
    return Results.Json(res);
});

static bool SameHash(string given, string? expected) => expected is not null &&
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.ASCII.GetBytes(given), System.Text.Encoding.ASCII.GetBytes(expected));

// Adresse e-mail du compte : ajout, changement ou suppression (vide). Mot de passe demandé.
account.MapPost("/email", (EmailRequest req, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    var user = CurrentUser(db, userId);
    if (user is null) return Unauthorized();
    if (CheckPassword(ctx, db, user.Username, req.Password, user, "Mot de passe incorrect.") is { } fail) return fail;
    var email = req.Email.Trim();
    if (email.Length > 0 && (email.Length > 254 || !emailPattern.IsMatch(email))) return Error(400, "Adresse e-mail invalide.");
    db.SetEmail(userId, email.Length > 0 ? email : null);
    db.SetResetCode(userId, null, null);
    if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        mailer.NotifyEmailChanged(user.Email, user.Username, email);
    return Results.NoContent();
});

account.MapPost("/logout", (HttpContext ctx, Database db) =>
{
    if (BearerToken(ctx) is { Length: > 0 } t) db.DeleteToken(Passwords.TokenHash(t));
    return Results.NoContent();
});

// Déconnecte tous les autres appareils du compte (téléphone perdu, ordinateur prêté…).
account.MapPost("/logout-others", (HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    db.DeleteOtherTokens(userId, Passwords.TokenHash(BearerToken(ctx)!));
    return Results.NoContent();
});

account.MapPost("/password", (PasswordRequest req, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    var user = CurrentUser(db, userId);
    if (user is null) return Unauthorized();
    if (CheckPassword(ctx, db, user.Username, req.Password, user, "Mot de passe actuel incorrect.") is { } fail) return fail;
    if (PasswordRules.Problem(req.NewPassword, user.Username) is { } problem) return Error(400, problem);
    db.SetPassword(userId, Passwords.Hash(req.NewPassword));
    db.DeleteOtherTokens(userId, Passwords.TokenHash(BearerToken(ctx)!));
    mailer.NotifyPasswordChanged(user.Email, user.Username, "avec l'ancien mot de passe", DeviceOf(ctx), IpOf(ctx));
    return Results.NoContent();
});

// Suppression du compte et de toutes ses données (mot de passe demandé).
account.MapDelete("", ([Microsoft.AspNetCore.Mvc.FromBody] AccountRequest req, HttpContext ctx, Database db) =>
{
    if (Auth(ctx, db) is not { } userId) return Unauthorized();
    var user = CurrentUser(db, userId);
    if (user is null) return Unauthorized();
    if (CheckPassword(ctx, db, user.Username, req.Password, user, "Mot de passe incorrect.") is { } fail) return fail;
    db.DeleteUser(userId);
    mailer.NotifyAccountDeleted(user.Email, user.Username);
    return Results.NoContent();
});

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

// Chaque jour (et au démarrage) : jetons expirés et vieux échecs oubliés, copie de sauvegarde de la base.
var expiry = new Timer(_ =>
{
    try
    {
        var db = app.Services.GetRequiredService<Database>();
        db.DeleteExpiredTokens(sessionIdle);
        db.DeleteOldFailures();
        if (backupsKept > 0)
            app.Logger.LogInformation("Sauvegarde de la base : {Path}", db.Backup(backupDir, backupsKept));
    }
    catch (Exception e) { app.Logger.LogWarning(e, "Nettoyage ou sauvegarde de la base impossible."); }
}, null, TimeSpan.Zero, TimeSpan.FromDays(1));

app.Run();
expiry.Dispose();
return 0;

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

/// <summary>Point d'entrée, visible des tests (WebApplicationFactory).</summary>
public partial class Program;
