using System.Globalization;
using System.Net;
using System.Net.Mail;

namespace Velom3d.Server;

/// <summary>
/// E-mails du serveur, par le serveur SMTP de l'hébergeur (réglages <c>Velom3d:Smtp:*</c>, voir
/// README) : code de réinitialisation du mot de passe, et alertes de sécurité (nouvelle connexion,
/// mot de passe changé, compte bloqué, adresse changée, compte supprimé). Sans <c>Host</c>, rien
/// n'est envoyé. Un compte sans adresse e-mail ne reçoit rien.
/// </summary>
public class Mailer(IConfiguration smtp, ILogger<Mailer> log)
{
    readonly string? _host = smtp["Host"] is { Length: > 0 } h ? h : null;
    readonly int _port = smtp.GetValue("Port", 587);
    readonly bool _ssl = smtp.GetValue("Ssl", true);
    readonly string? _user = smtp["User"];
    readonly string? _password = smtp["Password"];
    readonly string _from = smtp["From"] ?? smtp["User"] ?? "";
    readonly string _serverName = smtp["ServerName"] is { Length: > 0 } n ? n : "Velom3d";
    readonly bool _alerts = smtp.GetValue("SecurityAlerts", true);

    public virtual bool Enabled => _host is not null && _from.Length > 0;

    const string Footer = """

        Si ce n'était pas vous : dans l'appli, Réglages > Synchronisation > Mot de passe oublié, choisissez
        un nouveau mot de passe (tous les appareils seront déconnectés).
        """;

    static string When(DateTimeOffset t) =>
        t.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm", CultureInfo.InvariantCulture) + " (heure du serveur)";

    static string From(string? device, string? ip) =>
        (string.IsNullOrWhiteSpace(device) ? "un appareil" : $"« {device.Trim()} »") + (string.IsNullOrEmpty(ip) ? "" : $", adresse {ip}");

    /// <summary>Code pour choisir un nouveau mot de passe.</summary>
    public void SendResetCode(string to, string username, string code, TimeSpan validity) =>
        Send(to, $"{_serverName} : code pour changer votre mot de passe", $"""
            Bonjour {username},

            Quelqu'un (vous, sans doute) a demandé à changer le mot de passe de votre compte
            Velom3d sur ce serveur. Dans l'appli, Réglages > Synchronisation > Mot de passe
            oublié, saisissez ce code :

                {code}

            Il est valable {(int)validity.TotalMinutes} minutes et ne sert qu'une fois.
            Si vous n'avez rien demandé, ignorez ce message : votre mot de passe ne change pas.
            """);

    /// <summary>Connexion d'un appareil avec le mot de passe (ou après une réinitialisation).</summary>
    public void NotifyLogin(string? to, string username, string? device, string? ip) => Alert(to,
        $"{_serverName} : nouvelle connexion à votre compte", $"""
            Bonjour {username},

            Votre compte Velom3d vient d'être connecté depuis {From(device, ip)},
            le {When(DateTimeOffset.Now)}.
            {Footer}
            """);

    public void NotifyPasswordChanged(string? to, string username, string how, string? device, string? ip) => Alert(to,
        $"{_serverName} : mot de passe changé", $"""
            Bonjour {username},

            Le mot de passe de votre compte Velom3d a été changé ({how}) depuis {From(device, ip)},
            le {When(DateTimeOffset.Now)}. Les autres appareils ont été déconnectés.
            {Footer}
            """);

    public void NotifyLocked(string? to, string username, DateTimeOffset until) => Alert(to,
        $"{_serverName} : trop d'essais sur votre compte", $"""
            Bonjour {username},

            Plusieurs mots de passe (ou codes) faux ont été essayés de suite sur votre compte Velom3d :
            il est bloqué jusqu'au {When(until)}, puis de plus en plus longtemps si les essais
            continuent. Vos appareils déjà connectés continuent de synchroniser.

            Si ce n'était pas vous, quelqu'un essaie de deviner votre mot de passe : choisissez-en un
            long, que vous n'utilisez nulle part ailleurs.
            """);

    /// <summary>Envoyé à l'ancienne adresse, pour qu'un changement fait par quelqu'un d'autre se voie.</summary>
    public void NotifyEmailChanged(string? oldAddress, string username, string newAddress) => Alert(oldAddress,
        $"{_serverName} : adresse e-mail changée", $"""
            Bonjour {username},

            {(newAddress.Length > 0 ? $"L'adresse e-mail de votre compte Velom3d est maintenant {newAddress}." : "L'adresse e-mail de votre compte Velom3d a été retirée.")}
            Ce message est le dernier envoyé à cette adresse.
            {Footer}
            """);

    public void NotifyAccountDeleted(string? to, string username) => Alert(to,
        $"{_serverName} : compte supprimé", $"""
            Bonjour {username},

            Votre compte Velom3d et toutes ses données ont été supprimés de ce serveur, le {When(DateTimeOffset.Now)}.
            Les fichiers restés sur vos appareils ne sont pas touchés.
            """);

    void Alert(string? to, string subject, string body)
    {
        if (_alerts && !string.IsNullOrEmpty(to)) Send(to, subject, body);
    }

    /// <summary>Envoie en tâche de fond : la réponse au client ne dépend pas de l'envoi (ni de son
    /// temps, ni de son échec), pour ne rien dire de l'existence d'un compte ou d'une adresse.</summary>
    protected virtual void Send(string to, string subject, string body)
    {
        if (!Enabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new SmtpClient(_host!, _port) { EnableSsl = _ssl };
                if (!string.IsNullOrEmpty(_user)) client.Credentials = new NetworkCredential(_user, _password);
                using var msg = new MailMessage(_from, to) { Subject = subject, Body = body };
                await client.SendMailAsync(msg);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Envoi d'un e-mail impossible (vérifiez les réglages Smtp).");
            }
        });
    }
}
