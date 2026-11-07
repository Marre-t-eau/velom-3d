using System.Net;
using System.Net.Mail;

namespace Velom3d.Server;

/// <summary>
/// Envoi des e-mails de réinitialisation du mot de passe, par le serveur SMTP de l'hébergeur
/// (réglages <c>Velom3d:Smtp:*</c>, voir README). Sans <c>Host</c>, le serveur n'envoie rien et
/// seul le code de secours permet de retrouver un compte.
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

    public virtual bool Enabled => _host is not null && _from.Length > 0;

    /// <summary>Envoie le code en tâche de fond : la réponse au client ne dépend pas de l'envoi
    /// (ni de son temps, ni de son échec), pour ne rien dire de l'existence du compte.</summary>
    public virtual void SendResetCode(string to, string username, string code, TimeSpan validity) => _ = Task.Run(async () =>
    {
        try
        {
            using var client = new SmtpClient(_host!, _port) { EnableSsl = _ssl };
            if (!string.IsNullOrEmpty(_user)) client.Credentials = new NetworkCredential(_user, _password);
            using var msg = new MailMessage(_from, to)
            {
                Subject = $"{_serverName} : code pour changer votre mot de passe",
                Body = $"""
                    Bonjour {username},

                    Quelqu'un (vous, sans doute) a demandé à changer le mot de passe de votre compte
                    Velom3d sur ce serveur. Dans l'appli, Réglages > Synchronisation > Mot de passe
                    oublié, saisissez ce code :

                        {code}

                    Il est valable {(int)validity.TotalMinutes} minutes et ne sert qu'une fois.
                    Si vous n'avez rien demandé, ignorez ce message : votre mot de passe ne change pas.
                    """,
            };
            await client.SendMailAsync(msg);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Envoi de l'e-mail de réinitialisation impossible (vérifiez les réglages Smtp).");
        }
    });
}
