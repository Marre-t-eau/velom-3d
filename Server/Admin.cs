namespace Velom3d.Server;

/// <summary>
/// Commandes de l'administrateur du serveur, sur la machine du serveur (pas d'accès réseau) :
/// <code>
/// dotnet Velom3d.Server.dll users                 # liste des comptes
/// dotnet Velom3d.Server.dll recovery-code black   # nouveau code de secours pour « black »
/// dotnet Velom3d.Server.dll unlock black          # lève le blocage après trop d'essais
/// </code>
/// Avec Docker : <c>docker compose -f Server/docker-compose.yml exec velom3d dotnet Velom3d.Server.dll users</c>.
/// Les options habituelles (--Velom3d:Database=…) peuvent suivre.
/// </summary>
public static class Admin
{
    static readonly Dictionary<string, int> Commands = new()
    {
        ["users"] = 1,
        ["recovery-code"] = 2,
        ["unlock"] = 2,
    };

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.ContainsKey(args[0]);

    /// <summary>Nombre d'arguments pris par la commande (le reste va à la configuration).</summary>
    public static int ArgCount(string[] args) => Math.Min(args.Length, Commands[args[0]]);

    public static int Run(string[] args, Database db)
    {
        if (args.Length < Commands[args[0]])
        {
            Console.Error.WriteLine($"Usage : {args[0]} <nom d'utilisateur>");
            return 2;
        }
        if (args[0] == "users")
        {
            var users = db.ListUsers();
            foreach (var (name, email, created) in users)
                Console.WriteLine($"{name,-32} {email ?? "-",-40} créé le {created[..10]}");
            Console.WriteLine($"{users.Count} compte(s).");
            return 0;
        }
        if (db.FindUser(args[1]) is not { } user)
        {
            Console.Error.WriteLine($"Compte inconnu : {args[1]}");
            return 1;
        }
        db.ClearFailures("login:" + user.Username.ToLowerInvariant());
        db.ClearFailures("recover:" + user.Username.ToLowerInvariant());
        if (args[0] == "unlock")
        {
            Console.WriteLine($"Compte « {user.Username} » débloqué.");
            return 0;
        }
        var code = Passwords.NewRecoveryCode();
        db.SetRecoveryHash(user.Id, Passwords.RecoveryHash(code));
        Console.WriteLine($"Nouveau code de secours pour « {user.Username} » (l'ancien ne marche plus) :");
        Console.WriteLine();
        Console.WriteLine("    " + code);
        Console.WriteLine();
        Console.WriteLine("Dans l'appli : Réglages > Synchronisation > Mot de passe oublié.");
        return 0;
    }
}
