using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Velom3d.Sync;

/// <summary>
/// Ce qui passe entre l'appli et le serveur de synchronisation (fichier partagé par les deux).
/// Un « document » est un fichier de l'appli, envoyé tel quel dans <see cref="SyncChange.Content"/> :
/// <c>profile</c> (profile.json, les réglages), <c>workout</c> (un entraînement planifié) ou
/// <c>ride</c> (l'en-tête d'une sortie ; son fichier de données CSV passe à part, compressé).
/// </summary>
public static class SyncKinds
{
    public const string Profile = "profile";
    public const string Workout = "workout";
    public const string Ride = "ride";
    /// <summary>Le seul id du document <see cref="Profile"/>.</summary>
    public const string ProfileId = "profile";

    public static bool IsKnown(string kind) => kind is Profile or Workout or Ride;

    static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Id utilisable comme nom de fichier : lettres, chiffres, « - » et « _ » seulement.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    public static string Key(string kind, string id) => kind + "/" + id;

    /// <summary>Taille maximale d'un document (texte JSON).</summary>
    public const int MaxContentChars = 1_000_000;
    /// <summary>Taille maximale du fichier de données d'une sortie, décompressé.</summary>
    public const long MaxRideDataBytes = 200L * 1024 * 1024;
    /// <summary>Documents envoyés au plus par requête.</summary>
    public const int MaxChangesPerPush = 200;
}

/// <summary>Un document créé, modifié ou supprimé.</summary>
public sealed class SyncChange
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>Date de la version : <c>UpdatedAt</c> du fichier, départ d'une sortie, ou heure de la suppression.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Deleted { get; set; }
    /// <summary>Contenu du fichier, vide si supprimé.</summary>
    public string Content { get; set; } = "";
    /// <summary>Numéro d'ordre donné par le serveur (réponse seulement).</summary>
    public long Seq { get; set; }
}

public sealed class SyncPullResponse
{
    public List<SyncChange> Changes { get; set; } = new();
    /// <summary>À renvoyer au prochain appel (<c>since</c>).</summary>
    public long Cursor { get; set; }
    /// <summary>Il reste des changements : rappeler avec <see cref="Cursor"/>.</summary>
    public bool More { get; set; }
}

public sealed class SyncPushRequest
{
    public List<SyncChange> Changes { get; set; } = new();
}

public sealed class SyncPushResponse
{
    public int Accepted { get; set; }
}

public sealed class AccountRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AccountResponse
{
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
}

public sealed class ServerInfo
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public bool Registration { get; set; }
}

public sealed class ErrorResponse
{
    /// <summary>Message en français, affiché tel quel dans l'appli.</summary>
    public string Error { get; set; } = "";
}

/// <summary>Sérialisation générée à la compilation (sûr sur Android), en camelCase comme le serveur.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SyncPullResponse))]
[JsonSerializable(typeof(SyncPushRequest))]
[JsonSerializable(typeof(SyncPushResponse))]
[JsonSerializable(typeof(AccountRequest))]
[JsonSerializable(typeof(AccountResponse))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class SyncJson : JsonSerializerContext;
