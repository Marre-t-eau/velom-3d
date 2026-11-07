namespace Velom3d.Server;

/// <summary>Refuse les mots de passe trop faciles à deviner : trop courts, égaux au nom
/// d'utilisateur, un seul caractère répété, suite de clavier, ou parmi les plus courants.</summary>
public static class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 200;

    /// <summary>Message d'erreur, ou null si le mot de passe convient.</summary>
    public static string? Problem(string password, string username)
    {
        if (password.Length < MinLength) return $"Le mot de passe doit faire au moins {MinLength} caractères.";
        if (password.Length > MaxLength) return $"Le mot de passe doit faire au plus {MaxLength} caractères.";
        var lower = password.ToLowerInvariant();
        var user = username.Trim().ToLowerInvariant();
        if (lower == user || (user.Length >= 4 && (lower.Contains(user) || user.Contains(lower))))
            return "Le mot de passe ne doit pas contenir le nom d'utilisateur.";
        if (lower.Distinct().Count() <= 2 || IsSequence(lower) || Common.Contains(lower) || Common.Contains(TrimDigits(lower)))
            return "Ce mot de passe est trop courant : choisissez-en un plus difficile à deviner.";
        return null;
    }

    /// <summary>Suite régulière (« 12345678 », « abcdefgh », « 87654321 »).</summary>
    static bool IsSequence(string s)
    {
        int step = s[1] - s[0];
        if (Math.Abs(step) != 1) return false;
        for (int i = 2; i < s.Length; i++)
            if (s[i] - s[i - 1] != step) return false;
        return true;
    }

    /// <summary>« soleil2024 » → « soleil » : un mot courant suivi d'une année ou d'un chiffre reste courant.</summary>
    static string TrimDigits(string s) => s.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '!', '.', '*');

    // Les plus fréquents dans les fuites publiques (anglais et français) et ceux qu'on pourrait
    // essayer pour cette appli. En minuscules.
    static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "passw0rd", "p@ssword", "p@ssw0rd", "motdepasse", "mot2passe", "motdepass", "azerty",
        "azertyuiop", "azerty123", "qwerty", "qwertyuiop", "qwerty123", "123456789", "1234567890",
        "12345678", "987654321", "11111111", "00000000", "123123123", "123456abc", "abc12345", "abcd1234",
        "1q2w3e4r", "1q2w3e4r5t", "1qaz2wsx", "zaq12wsx", "qazwsx", "q1w2e3r4", "a1b2c3d4", "aqwzsx",
        "iloveyou", "jetaime", "jesuisla", "bonjour", "bonsoir", "salut", "coucou", "soleil", "doudou",
        "loulou", "chouchou", "marseille", "paris", "france", "nicolas", "camille", "julien", "thomas",
        "alexandre", "princesse", "chocolat", "football", "baseball", "basketball", "superman", "batman",
        "pokemon", "starwars", "dragon", "monkey", "letmein", "welcome", "bienvenue", "admin",
        "administrator", "administrateur", "root", "toor", "master", "secret", "secure", "changeme",
        "default", "trustno1", "sunshine", "princess", "shadow", "michael", "jennifer", "charlie",
        "computer", "internet", "samsung", "google", "apple", "orange", "freebox", "livebox", "wifi",
        "velo", "velos", "vélo", "cyclisme", "cycliste", "bicyclette", "bike", "cycling", "zwift",
        "velom3d", "velom", "strava", "garmin", "tourdefrance", "pedale", "pedaler",
        "whatever", "nothing", "test", "testtest", "test1234", "demo", "guest", "user", "utilisateur",
        "abcdefgh", "abcdefg", "aaaaaa", "qwertz", "asdfghjkl", "asdfasdf", "zxcvbnm", "poiuytreza",
        "wxcvbn", "azertyui", "123azerty", "azerty1234",
    };
}
