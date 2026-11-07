using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Velom3d.Server;

/// <summary>Mots de passe (PBKDF2-SHA256, sel aléatoire), jetons de connexion et codes de secours
/// (aléatoires, seule leur empreinte SHA-256 est gardée en base).</summary>
public static class Passwords
{
    /// <summary>Recommandation OWASP 2023 pour PBKDF2-SHA256. Un hash plus ancien (moins d'itérations)
    /// est refait à la connexion suivante.</summary>
    public const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture,
            $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public static bool Verify(string password, string stored)
    {
        if (ParseIterations(stored) is not { } iterations) return false;
        var parts = stored.Split('$');
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Hash fait avec moins d'itérations qu'aujourd'hui : à refaire.</summary>
    public static bool NeedsRehash(string stored) => ParseIterations(stored) is not { } n || n < Iterations;

    static int? ParseIterations(string stored)
    {
        var parts = stored.Split('$');
        return parts.Length == 4 && parts[0] == "pbkdf2-sha256" &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : null;
    }

    /// <summary>Hash factice pour répondre en temps constant quand le compte n'existe pas.</summary>
    public static readonly string Dummy = Hash("velom3d-dummy-password");

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---- Codes de secours ----

    // Base 32 de Crockford : ni I, L, O ni U, pour ne pas confondre en recopiant à la main.
    const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Nouveau code de secours : 16 caractères (80 bits), en groupes de 4 (« 7K2M-Q9XD-… »).</summary>
    public static string NewRecoveryCode() => Group(RandomNumberGenerator.GetString(CodeAlphabet, 16), 4);

    /// <summary>Code envoyé par e-mail : 8 caractères (« 7K2M-Q9XD »), valable peu de temps, et les
    /// essais sont limités par le blocage après plusieurs échecs.</summary>
    public static string NewResetCode() => Group(RandomNumberGenerator.GetString(CodeAlphabet, 8), 4);

    /// <summary>Empreinte d'un code saisi : tirets, espaces et casse ignorés, O lu 0, I et L lus 1.</summary>
    public static string RecoveryHash(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var ch in code.ToUpperInvariant())
        {
            if (ch is '-' or ' ' or '\t') continue;
            sb.Append(ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch });
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("recovery:" + sb)));
    }

    static string Group(string s, int size) =>
        string.Join('-', Enumerable.Range(0, s.Length / size).Select(i => s.Substring(i * size, size)));
}
