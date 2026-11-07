using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Velom3d.Sync;
using Xunit;

namespace Velom3d.Server.Tests;

/// <summary>Sécurité des comptes : mots de passe faibles, blocage après plusieurs échecs, codes de
/// secours, e-mail, sessions, commandes d'administration.</summary>
public sealed class AuthTests : IDisposable
{
    const string Url = "http://localhost";
    const string Pw = "Roue-Libre-42";
    readonly string _root = Path.Combine(Path.GetTempPath(), "velom3d-auth-" + Guid.NewGuid().ToString("N"));
    readonly FakeMailer _mailer = new();
    readonly WebApplicationFactory<Program> _factory;

    /// <summary>Garde les e-mails au lieu de les envoyer.</summary>
    sealed class FakeMailer() : Mailer(new ConfigurationBuilder().Build(), NullLogger<Mailer>.Instance)
    {
        public bool On { get; set; }
        public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();
        public override bool Enabled => On;
        protected override void Send(string to, string subject, string body)
        {
            if (On) Sent.Enqueue((to, subject, body));
        }

        /// <summary>Codes de réinitialisation envoyés (adresse, code).</summary>
        public (string To, string Code)[] Codes => Sent.Where(m => m.Subject.Contains("code pour changer"))
            .Select(m => (m.To, System.Text.RegularExpressions.Regex.Match(m.Body, "[0-9A-Z]{4}-[0-9A-Z]{4}").Value)).ToArray();

        public string[] Subjects => Sent.Select(m => m.Subject).ToArray();

        public void Clear() { while (Sent.TryDequeue(out _)) { } }
    }

    public AuthTests()
    {
        Directory.CreateDirectory(_root);
        _factory = Factory();
    }

    string DbPath => Path.Combine(_root, "server", "velom3d.db");

    WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Velom3d:Database", DbPath);
            b.ConfigureServices(s => s.AddSingleton<Mailer>(_mailer));
        });

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    int _devices;

    SyncClient NewDevice()
    {
        var dir = Path.Combine(_root, "device" + _devices++);
        return new SyncClient(Path.Combine(dir, "sync.json"), Path.Combine(dir, "rides"), Path.Combine(dir, "workouts"),
            _factory.Server.CreateHandler());
    }

    [Fact]
    public async Task Weak_passwords_are_refused()
    {
        var a = NewDevice();
        foreach (var weak in new[] { "motdepasse", "12345678", "azertyuiop", "aaaaaaaa", "abababab", "Soleil2024", "black-black", "87654321" })
        {
            var e = await Assert.ThrowsAsync<SyncException>(() => a.RegisterAsync(Url, "black", weak));
            Assert.True(e.Message.Contains("courant") || e.Message.Contains("nom d'utilisateur"), weak + " : " + e.Message);
        }
        Assert.False(a.State.LoggedIn);
        var code = await a.RegisterAsync(Url, "black", Pw);
        Assert.Matches("^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$", code);
    }

    [Fact]
    public async Task Account_is_locked_after_repeated_failures_whatever_the_address()
    {
        var a = NewDevice();
        await a.RegisterAsync(Url, "black", Pw);
        var b = NewDevice();
        for (int i = 0; i < 2; i++)
            Assert.Contains("incorrect", (await Assert.ThrowsAsync<SyncException>(() => b.LoginAsync(Url, "black", "Mauvais-" + i))).Message);
        var locked = await Assert.ThrowsAsync<SyncException>(() => b.LoginAsync(Url, "BLACK", "Mauvais-3"));
        Assert.Contains("Trop d'essais pour ce compte", locked.Message);
        // Même le bon mot de passe attend la fin du blocage.
        locked = await Assert.ThrowsAsync<SyncException>(() => b.LoginAsync(Url, "black", Pw));
        Assert.Contains("réessayez dans 1 min", locked.Message);
        // Un nom qui n'existe pas se bloque pareil : le blocage ne dit pas si un compte existe.
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsAsync<SyncException>(() => b.LoginAsync(Url, "personne", "Mauvais-" + i));
        Assert.Contains("Trop d'essais pour ce compte", (await Assert.ThrowsAsync<SyncException>(() => b.LoginAsync(Url, "personne", Pw))).Message);
        // Les appareils déjà connectés continuent de synchroniser.
        await a.SyncAsync();

        // L'administrateur débloque le compte.
        var db = new Database(DbPath);
        Assert.Equal(0, Admin.Run(["unlock", "black"], db));
        await b.LoginAsync(Url, "black", Pw);
    }

    [Fact]
    public void Lock_grows_then_failures_are_forgotten_after_success()
    {
        var db = new Database(DbPath);
        var max = TimeSpan.FromMinutes(60);
        for (int i = 1; i < 3; i++) Assert.Null(db.RecordFailure("login:x", 3, max, TimeSpan.FromDays(1)));
        var first = db.RecordFailure("login:x", 3, max, TimeSpan.FromDays(1))!.Value;
        var second = db.RecordFailure("login:x", 3, max, TimeSpan.FromDays(1))!.Value;
        Assert.InRange((first - DateTimeOffset.UtcNow).TotalMinutes, 0.9, 1.1);
        Assert.InRange((second - DateTimeOffset.UtcNow).TotalMinutes, 1.9, 2.1);
        for (int i = 0; i < 20; i++) db.RecordFailure("login:x", 3, max, TimeSpan.FromDays(1));
        Assert.InRange((db.LockedUntil("login:x")!.Value - DateTimeOffset.UtcNow).TotalMinutes, 59, 60.1);
        db.ClearFailures("login:x");
        Assert.Null(db.LockedUntil("login:x"));
    }

    [Fact]
    public async Task Forgotten_password_with_the_recovery_code()
    {
        var a = NewDevice();
        var code = await a.RegisterAsync(Url, "black", Pw);
        var b = NewDevice();
        await b.LoginAsync(Url, "black", Pw);

        var c = NewDevice();
        var wrong = await Assert.ThrowsAsync<SyncException>(() => c.RecoverAsync(Url, "black", "AAAA-BBBB-CCCC-DDDD", "Nouveau-Mot-77"));
        Assert.Contains("code incorrect", wrong.Message);
        // Recopié à la main : minuscules, sans tirets, O pour 0.
        var typed = code.Replace("-", " ").ToLowerInvariant().Replace('0', 'o');
        var res = await c.RecoverAsync(Url, "black", typed, "Nouveau-Mot-77");
        Assert.True(c.State.LoggedIn);
        Assert.NotEqual(code, res.RecoveryCode);
        Assert.Matches("^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$", res.RecoveryCode);

        // Tous les autres appareils sont déconnectés, l'ancien mot de passe et l'ancien code ne marchent plus.
        Assert.Contains("reconnectez-vous", (await Assert.ThrowsAsync<SyncException>(() => a.SyncAsync())).Message);
        Assert.Contains("reconnectez-vous", (await Assert.ThrowsAsync<SyncException>(() => b.SyncAsync())).Message);
        await Assert.ThrowsAsync<SyncException>(() => a.LoginAsync(Url, "black", Pw));
        await Assert.ThrowsAsync<SyncException>(() => a.RecoverAsync(Url, "black", code, "Encore-Autre-88"));
        await a.LoginAsync(Url, "black", "Nouveau-Mot-77");

        // Le nouveau code marche à son tour.
        await NewDevice().RecoverAsync(Url, "black", res.RecoveryCode, "Encore-Autre-88");
    }

    [Fact]
    public async Task Recovery_attempts_are_locked_too()
    {
        var a = NewDevice();
        var code = await a.RegisterAsync(Url, "black", Pw);
        for (int i = 0; i < 2; i++)
            await Assert.ThrowsAsync<SyncException>(() => a.RecoverAsync(Url, "black", "AAAA-BBBB-CCCC-000" + i, "Nouveau-Mot-77"));
        var e = await Assert.ThrowsAsync<SyncException>(() => a.RecoverAsync(Url, "black", "AAAA-BBBB-CCCC-0009", "Nouveau-Mot-77"));
        Assert.Contains("Trop d'essais", e.Message);
        await Assert.ThrowsAsync<SyncException>(() => a.RecoverAsync(Url, "black", code, "Nouveau-Mot-77"));
    }

    [Fact]
    public async Task Forgotten_password_by_email()
    {
        var a = NewDevice();
        // Sans envoi d'e-mails configuré : message clair.
        await a.RegisterAsync(Url, "black", Pw, "black@exemple.fr");
        Assert.Equal("black@exemple.fr", a.State.Email);
        Assert.False((await a.InfoAsync(Url)).Email);
        Assert.Contains("code de secours", (await Assert.ThrowsAsync<SyncException>(() => a.ForgotAsync(Url, "black"))).Message);

        _mailer.On = true;
        Assert.True((await a.InfoAsync(Url)).Email);
        var b = NewDevice();
        await b.ForgotAsync(Url, "black");
        await b.ForgotAsync(Url, "personne"); // même réponse, rien n'est envoyé
        await b.ForgotAsync(Url, "black");    // moins d'une minute après : pas de second e-mail
        var mail = Assert.Single(_mailer.Codes);
        Assert.Equal("black@exemple.fr", mail.To);
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}$", mail.Code);

        var res = await b.RecoverAsync(Url, "black", mail.Code, "Nouveau-Mot-77");
        Assert.True(b.State.LoggedIn);
        Assert.Equal("", res.RecoveryCode); // le code de secours ne change pas
        Assert.Contains("reconnectez-vous", (await Assert.ThrowsAsync<SyncException>(() => a.SyncAsync())).Message);
        // Le code e-mail ne sert qu'une fois.
        await Assert.ThrowsAsync<SyncException>(() => NewDevice().RecoverAsync(Url, "black", mail.Code, "Encore-Autre-88"));

        // Adresse retirée : plus d'e-mail.
        await Assert.ThrowsAsync<SyncException>(() => b.SetEmailAsync("Mauvais-1", ""));
        Assert.Contains("invalide", (await Assert.ThrowsAsync<SyncException>(() => b.SetEmailAsync("Nouveau-Mot-77", "pas-une-adresse"))).Message);
        await b.SetEmailAsync("Nouveau-Mot-77", "");
        Assert.Equal("", b.State.Email);
        _mailer.Clear();
        await NewDevice().ForgotAsync(Url, "black");
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public async Task Old_hashes_are_upgraded()
    {
        // Compte créé avant ces changements : 100 000 itérations, pas de code de secours.
        var db = new Database(DbPath);
        var salt = new byte[16];
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(Pw, salt, 100_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        db.CreateUser("ancien", $"pbkdf2-sha256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");

        var a = NewDevice();
        await a.LoginAsync(Url, "ancien", Pw);
        Assert.StartsWith($"pbkdf2-sha256${Passwords.Iterations}$", db.FindUser("ancien")!.PasswordHash);
        await NewDevice().LoginAsync(Url, "ancien", Pw);
    }

    [Fact]
    public async Task Sessions_expire_and_other_devices_can_be_logged_out()
    {
        var a = NewDevice();
        await a.RegisterAsync(Url, "black", Pw);
        var b = NewDevice();
        await b.LoginAsync(Url, "black", Pw);
        await a.LogoutOthersAsync();
        await a.SyncAsync();
        Assert.Contains("reconnectez-vous", (await Assert.ThrowsAsync<SyncException>(() => b.SyncAsync())).Message);

        var db = new Database(DbPath);
        var tokenHash = Passwords.TokenHash(a.State.Token);
        Assert.NotNull(db.UserForToken(tokenHash, TimeSpan.FromDays(90)));
        await Task.Delay(20);
        Assert.Null(db.UserForToken(tokenHash, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, db.DeleteExpiredTokens(TimeSpan.FromMilliseconds(1)));
        Assert.Null(db.UserForToken(tokenHash, TimeSpan.FromDays(90)));
    }

    [Fact]
    public async Task Password_change_checks_strength_and_logs_out_others()
    {
        var a = NewDevice();
        await a.RegisterAsync(Url, "black", Pw);
        var b = NewDevice();
        await b.LoginAsync(Url, "black", Pw);
        await Assert.ThrowsAsync<SyncException>(() => a.ChangePasswordAsync("Mauvais-1", "Nouveau-Mot-77"));
        Assert.Contains("courant", (await Assert.ThrowsAsync<SyncException>(() => a.ChangePasswordAsync(Pw, "password1"))).Message);
        await a.ChangePasswordAsync(Pw, "Nouveau-Mot-77");
        await a.SyncAsync();
        await Assert.ThrowsAsync<SyncException>(() => b.SyncAsync());
    }

    [Fact]
    public async Task No_limit_per_address_only_per_account()
    {
        // Beaucoup de comptes et de connexions depuis la même adresse : rien n'est refusé.
        for (int i = 0; i < 30; i++) await NewDevice().RegisterAsync(Url, "compte" + i, Pw);
        for (int i = 0; i < 30; i++) await NewDevice().LoginAsync(Url, "compte" + i, Pw);
    }

    [Fact]
    public async Task Security_alerts_by_email()
    {
        _mailer.On = true;
        var a = NewDevice();
        a.DeviceName = "Pixel 8";
        await a.RegisterAsync(Url, "black", Pw, "black@exemple.fr");
        await NewDevice().RegisterAsync(Url, "sansadresse", Pw); // pas d'adresse : jamais d'e-mail
        Assert.Empty(_mailer.Sent); // la création n'envoie rien

        // Nouvelle connexion : nom de l'appareil (sans accents dans l'en-tête) et adresse.
        var b = NewDevice();
        b.DeviceName = "Ordinateur de Black";
        await b.LoginAsync(Url, "black", Pw);
        await NewDevice().LoginAsync(Url, "sansadresse", Pw);
        var login = Assert.Single(_mailer.Sent);
        Assert.Equal("black@exemple.fr", login.To);
        Assert.Contains("nouvelle connexion", login.Subject);
        Assert.Contains("« Ordinateur de Black »", login.Body);
        _mailer.Clear();

        // Compte bloqué : un seul e-mail, au premier blocage.
        for (int i = 0; i < 5; i++) await Assert.ThrowsAsync<SyncException>(() => NewDevice().LoginAsync(Url, "black", "Mauvais-" + i));
        Assert.Equal(["Velom3d : trop d'essais sur votre compte"], _mailer.Subjects);
        new Database(DbPath).ClearFailures("login:black");
        _mailer.Clear();

        // Mot de passe changé.
        await b.ChangePasswordAsync(Pw, "Nouveau-Mot-77");
        Assert.Contains("mot de passe changé", Assert.Single(_mailer.Sent).Subject);
        _mailer.Clear();

        // Adresse changée : l'ancienne est prévenue.
        await b.SetEmailAsync("Nouveau-Mot-77", "autre@exemple.fr");
        var changed = Assert.Single(_mailer.Sent);
        Assert.Equal("black@exemple.fr", changed.To);
        Assert.Contains("autre@exemple.fr", changed.Body);
        _mailer.Clear();

        // Compte supprimé.
        await b.DeleteAccountAsync("Nouveau-Mot-77");
        var deleted = Assert.Single(_mailer.Sent);
        Assert.Equal("autre@exemple.fr", deleted.To);
        Assert.Contains("compte supprimé", deleted.Subject);
    }

    [Fact]
    public async Task Account_deletion_from_the_app()
    {
        var a = NewDevice();
        await a.RegisterAsync(Url, "black", Pw);
        var b = NewDevice();
        await b.LoginAsync(Url, "black", Pw);
        Assert.Contains("incorrect", (await Assert.ThrowsAsync<SyncException>(() => a.DeleteAccountAsync("Mauvais-1"))).Message);
        Assert.True(a.State.LoggedIn); // un mauvais mot de passe ne déconnecte pas
        await a.DeleteAccountAsync(Pw);
        Assert.False(a.State.LoggedIn);
        Assert.Equal("", a.State.Account);
        Assert.Contains("reconnectez-vous", (await Assert.ThrowsAsync<SyncException>(() => b.SyncAsync())).Message);
        await Assert.ThrowsAsync<SyncException>(() => NewDevice().LoginAsync(Url, "black", Pw));
        await NewDevice().RegisterAsync(Url, "black", Pw); // le nom est libre à nouveau
    }

    [Fact]
    public void Daily_backups_keep_the_most_recent()
    {
        var db = new Database(DbPath);
        db.CreateUser("black", Passwords.Hash(Pw));
        var dir = Path.Combine(_root, "backups");
        for (int day = 1; day <= 9; day++) File.WriteAllText(Path.Combine(dir.EnsureDir(), $"velom3d-2026-01-0{day}.db"), "ancienne");
        var path = db.Backup(dir, 7);
        var kept = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.Equal(7, kept.Length);
        Assert.Contains(Path.GetFileName(path), kept);
        Assert.DoesNotContain("velom3d-2026-01-03.db", kept); // les plus anciennes partent
        Assert.Contains("velom3d-2026-01-09.db", kept);
        // La copie est une vraie base, avec le compte.
        Assert.NotNull(new Database(path).FindUser("black"));
        db.Backup(dir, 7); // même jour : remplacée, pas doublée
        Assert.Equal(7, Directory.GetFiles(dir, "*.db").Length);
    }

    [Fact]
    public void Admin_gives_a_new_recovery_code()
    {
        var db = new Database(DbPath);
        db.CreateUser("black", Passwords.Hash(Pw), email: "black@exemple.fr");
        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(0, Admin.Run(["recovery-code", "Black"], db));
            Assert.Equal(0, Admin.Run(["users"], db));
            Assert.Equal(1, Admin.Run(["recovery-code", "personne"], db));
        }
        finally { Console.SetOut(stdout); }
        var code = output.ToString().Split('\n').Select(l => l.Trim()).First(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$"));
        Assert.Equal(Passwords.RecoveryHash(code), db.FindUser("black")!.RecoveryHash);
        Assert.Contains("black@exemple.fr", output.ToString());
        Assert.True(Admin.IsCommand(["users"]));
        Assert.False(Admin.IsCommand(["--urls", "http://0.0.0.0:8080"]));
    }
}

static class TestPaths
{
    public static string EnsureDir(this string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
