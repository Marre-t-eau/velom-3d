using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velom3d.Sync;

/// <summary>
/// Réglages et mémoire de la synchronisation, dans <c>user://sync.json</c> (jamais envoyé au serveur).
/// <see cref="Known"/> garde l'empreinte de chaque fichier au moment où il a été synchronisé pour la
/// dernière fois : un fichier dont l'empreinte a changé depuis a été modifié sur cet appareil.
/// </summary>
public sealed class SyncState
{
    /// <summary>Synchronisation activée par le joueur. Désactivée par défaut : l'appli marche sans serveur.</summary>
    public bool Enabled { get; set; }
    public string ServerUrl { get; set; } = SyncClient.DefaultServerUrl;
    public string Username { get; set; } = "";
    /// <summary>Jeton de connexion donné par le serveur, vide si déconnecté.</summary>
    public string Token { get; set; } = "";
    /// <summary>Compte (adresse + nom) auquel se rapportent <see cref="Cursor"/> et <see cref="Known"/>.</summary>
    public string Account { get; set; } = "";
    /// <summary>Dernier changement du serveur déjà reçu.</summary>
    public long Cursor { get; set; }
    /// <summary>« kind/id » → empreinte du fichier à la dernière synchronisation.</summary>
    public Dictionary<string, string> Known { get; set; } = new();
    /// <summary>Sorties reçues dont le fichier de données n'était pas encore sur le serveur : réessayées.</summary>
    public Dictionary<string, SyncChange> Pending { get; set; } = new();
    public DateTimeOffset? LastSyncAt { get; set; }
    /// <summary>Message de la dernière erreur, vide si la dernière synchronisation a réussi.</summary>
    public string LastError { get; set; } = "";

    [JsonIgnore] public bool LoggedIn => Token.Length > 0;

    public static SyncState Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), SyncStateJson.Default.SyncState) is { } s)
                return s;
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new SyncState();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SyncStateJson.Default.SyncState));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SyncState))]
internal partial class SyncStateJson : JsonSerializerContext;
