using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Velom3d.Rides;

namespace Velom3d.Sync;

/// <summary>Erreur de synchronisation, avec un message à montrer au joueur.</summary>
public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Ce qu'a fait une synchronisation.</summary>
public sealed record SyncReport(int Sent, int Received, int Deleted);

/// <summary>
/// Synchronisation facultative avec un serveur (Server/ dans le dépôt, auto-hébergeable).
///
/// Les fichiers de l'appareil font foi : le jeu les lit et les écrit comme avant, sans jamais
/// attendre le serveur. Une synchronisation compare les fichiers locaux à ce qu'elle a vu la
/// dernière fois (<see cref="SyncState.Known"/>) et aux changements reçus du serveur :
/// - un fichier modifié (ou supprimé) sur l'appareil depuis la dernière synchro est envoyé et
///   remplace la version du serveur, même si un autre appareil l'a changé entre-temps ;
/// - un fichier inchangé ici prend la version du serveur (modifiée ou supprimée ailleurs) ;
/// - un fichier jamais synchronisé, présent des deux côtés (premier passage d'un appareil sur un
///   compte) : la version la plus récente gagne, l'appareil à égalité ; une même sortie reste celle de l'appareil.
/// Le fichier de données d'une sortie est envoyé avant son en-tête : un autre appareil qui voit
/// l'en-tête trouve les données.
/// </summary>
public sealed class SyncClient : IDisposable
{
    /// <summary>Adresse proposée par défaut (vide : à saisir dans les Réglages).</summary>
    public const string DefaultServerUrl = "";

    readonly string _statePath;
    readonly HttpClient _http;
    readonly object _lock = new();
    Task<SyncReport>? _running;

    /// <summary>Dossier des sorties et de profile.json (celui de <see cref="RideStore"/>).</summary>
    public string RidesDirectory { get; set; }
    /// <summary>Dossier des entraînements (celui de <see cref="WorkoutStore"/>).</summary>
    public string WorkoutsDirectory { get; set; }

    public SyncState State { get; }

    public bool IsRunning { get { lock (_lock) return _running is { IsCompleted: false }; } }

    public SyncClient(string statePath, string ridesDirectory, string workoutsDirectory, HttpMessageHandler? handler = null)
    {
        _statePath = statePath;
        RidesDirectory = ridesDirectory;
        WorkoutsDirectory = workoutsDirectory;
        State = SyncState.Load(statePath);
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    public void Dispose() => _http.Dispose();

    public void SaveState()
    {
        lock (State) State.Save(_statePath);
    }

    /// <summary>Adresse nettoyée, sans « / » final, ou null si invalide. Sans « http:// » ni
    /// « https:// », on ajoute http:// pour une adresse du réseau local (IP, localhost, *.local),
    /// où le serveur est en général lancé sans HTTPS, et https:// sinon.</summary>
    public static string? NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0) return null;
        if (!url.Contains("://"))
            url = (Uri.TryCreate("http://" + url, UriKind.Absolute, out var guess) && IsLocal(guess) ? "http://" : "https://") + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") && u.Host.Length > 0
            ? url : null;
    }

    static bool IsLocal(Uri u) =>
        u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || u.IsLoopback
        || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !u.Host.Contains('.');

    // ---- Compte ----

    public Task RegisterAsync(string serverUrl, string username, string password) =>
        SignInAsync("/api/account/register", serverUrl, username, password);

    public Task LoginAsync(string serverUrl, string username, string password) =>
        SignInAsync("/api/account/login", serverUrl, username, password);

    async Task SignInAsync(string path, string serverUrl, string username, string password)
    {
        var url = NormalizeUrl(serverUrl) ?? throw new SyncException("Adresse du serveur invalide.");
        var info = await Send(HttpMethod.Get, url, "/api/info", null, SyncJson.Default.ServerInfo, auth: false)
            .ConfigureAwait(false);
        if (info.Name != "velom3d") throw new SyncException("Cette adresse n'est pas un serveur Velom3d.");
        var body = Json(new AccountRequest { Username = username.Trim(), Password = password }, SyncJson.Default.AccountRequest);
        var res = await Send(HttpMethod.Post, url, path, body, SyncJson.Default.AccountResponse, auth: false).ConfigureAwait(false);
        lock (State)
        {
            var account = url.ToLowerInvariant() + "|" + res.Username.ToLowerInvariant();
            if (State.Account != account)
            {
                // Autre compte ou autre serveur : tout est à comparer à nouveau.
                State.Account = account;
                State.Cursor = 0;
                State.Known.Clear();
                State.Pending.Clear();
                State.LastSyncAt = null;
            }
            State.ServerUrl = url;
            State.Username = res.Username;
            State.Token = res.Token;
            State.Enabled = true;
            State.LastError = "";
        }
        SaveState();
    }

    /// <summary>Oublie le jeton (et le révoque sur le serveur si possible). Les fichiers locaux restent.</summary>
    public async Task LogoutAsync()
    {
        if (State.LoggedIn)
        {
            try { await SendNoContent(HttpMethod.Post, State.ServerUrl, "/api/account/logout", null).ConfigureAwait(false); }
            catch (SyncException) { } // hors ligne : le jeton est oublié quand même
        }
        lock (State) State.Token = "";
        SaveState();
    }

    // ---- Synchronisation ----

    /// <summary>Lance une synchronisation, ou renvoie celle en cours. Le travail se fait hors du fil
    /// principal. Erreur : <see cref="SyncException"/>, aussi notée dans <see cref="SyncState.LastError"/>.</summary>
    public Task<SyncReport> SyncAsync()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false }) return _running;
            return _running = Task.Run(RunAsync);
        }
    }

    async Task<SyncReport> RunAsync()
    {
        if (!State.LoggedIn) throw new SyncException("Connectez-vous d'abord à un serveur.");
        try
        {
            var report = await SyncOnce().ConfigureAwait(false);
            lock (State)
            {
                State.LastSyncAt = DateTimeOffset.Now;
                State.LastError = "";
            }
            SaveState();
            return report;
        }
        catch (Exception e)
        {
            var err = e as SyncException ?? new SyncException("Synchronisation impossible : " + e.Message, e);
            lock (State) State.LastError = err.Message;
            SaveState();
            throw err;
        }
    }

    sealed record LocalDoc(string Kind, string Id, string Content, string Fingerprint, DateTimeOffset UpdatedAt)
    {
        public SyncChange ToChange() => new() { Kind = Kind, Id = Id, Content = Content, UpdatedAt = UpdatedAt };
    }

    async Task<SyncReport> SyncOnce()
    {
        int sent = 0, received = 0, deleted = 0;

        // 1. Ce qui a changé sur le serveur depuis la dernière fois (et les sorties en attente).
        var incoming = new Dictionary<string, SyncChange>(State.Pending);
        long cursor = State.Cursor;
        while (true)
        {
            var page = await Send(HttpMethod.Get, State.ServerUrl, $"/api/sync?since={cursor}", null,
                SyncJson.Default.SyncPullResponse).ConfigureAwait(false);
            foreach (var ch in page.Changes)
                if (SyncKinds.IsKnown(ch.Kind) && SyncKinds.IsValidId(ch.Id))
                    incoming[SyncKinds.Key(ch.Kind, ch.Id)] = ch;
            cursor = Math.Max(cursor, page.Cursor);
            if (!page.More || page.Changes.Count == 0) break;
        }

        // 2. Ce qui a changé ici : ces fichiers l'emportent.
        var local = ScanLocal();
        var push = new List<SyncChange>();
        foreach (var key in local.Keys.Union(State.Known.Keys).ToList())
        {
            local.TryGetValue(key, out var doc);
            State.Known.TryGetValue(key, out var known);
            bool changedHere = doc is null ? known is not null : doc.Fingerprint != known;
            if (!changedHere) continue;
            if (incoming.TryGetValue(key, out var inc))
            {
                if (doc is not null && !inc.Deleted && inc.Content == doc.Content)
                {
                    // Déjà pareil des deux côtés.
                    lock (State) { State.Known[key] = doc.Fingerprint; State.Pending.Remove(key); }
                    incoming.Remove(key);
                    continue;
                }
                bool localWins = known is not null || doc is null
                    || (doc.Kind == SyncKinds.Ride && !inc.Deleted)
                    || doc.UpdatedAt >= inc.UpdatedAt;
                if (!localWins) continue; // la version du serveur est appliquée plus bas
                incoming.Remove(key);
                lock (State) State.Pending.Remove(key);
            }
            var (kind, id) = Split(key);
            push.Add(doc?.ToChange() ?? new SyncChange { Kind = kind, Id = id, Deleted = true, UpdatedAt = DateTimeOffset.Now });
        }

        // 3. Envoi : données des sorties d'abord, puis les documents par paquets.
        foreach (var ch in push.Where(c => c.Kind == SyncKinds.Ride && !c.Deleted))
            await UploadRideData(ch.Id).ConfigureAwait(false);
        foreach (var batch in Batches(push))
        {
            await Send(HttpMethod.Post, State.ServerUrl, "/api/sync",
                Json(new SyncPushRequest { Changes = batch }, SyncJson.Default.SyncPushRequest),
                SyncJson.Default.SyncPushResponse).ConfigureAwait(false);
            lock (State)
                foreach (var ch in batch)
                {
                    var key = SyncKinds.Key(ch.Kind, ch.Id);
                    if (ch.Deleted) State.Known.Remove(key);
                    else State.Known[key] = local[key].Fingerprint;
                }
            sent += batch.Count;
        }

        // 4. Réception : ce qui a changé ailleurs et pas ici.
        foreach (var (key, inc) in incoming)
        {
            local.TryGetValue(key, out var doc);
            if (inc.Deleted)
            {
                if (doc is not null)
                {
                    DeleteLocal(inc.Kind, inc.Id);
                    deleted++;
                }
                lock (State) { State.Known.Remove(key); State.Pending.Remove(key); }
                continue;
            }
            if (doc is not null && doc.Content == inc.Content)
            {
                lock (State) { State.Known[key] = doc.Fingerprint; State.Pending.Remove(key); }
                continue;
            }
            if (!IsValidContent(inc)) continue; // version d'un autre appli ? on garde la nôtre
            if (inc.Kind == SyncKinds.Ride)
            {
                var data = await DownloadRideData(inc.Id).ConfigureAwait(false);
                if (data is null)
                {
                    lock (State) State.Pending[key] = inc; // données pas encore envoyées : plus tard
                    continue;
                }
                Directory.CreateDirectory(RidesDirectory);
                WriteAtomic(Path.Combine(RidesDirectory, inc.Id + ".csv"), data);
                WriteAtomic(Path.Combine(RidesDirectory, inc.Id + ".json"), Encoding.UTF8.GetBytes(inc.Content));
            }
            else
            {
                var path = PathOf(inc.Kind, inc.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteAtomic(path, Encoding.UTF8.GetBytes(inc.Content));
            }
            var written = Read(inc.Kind, inc.Id);
            lock (State)
            {
                if (written is not null) State.Known[key] = written.Fingerprint;
                State.Pending.Remove(key);
            }
            received++;
        }

        lock (State) State.Cursor = cursor;
        return new SyncReport(sent, received, deleted);
    }

    static (string Kind, string Id) Split(string key)
    {
        int slash = key.IndexOf('/');
        return (key[..slash], key[(slash + 1)..]);
    }

    /// <summary>Paquets d'au plus 100 documents et ~4 Mo.</summary>
    static IEnumerable<List<SyncChange>> Batches(List<SyncChange> changes)
    {
        var batch = new List<SyncChange>();
        long size = 0;
        foreach (var ch in changes)
        {
            if (batch.Count > 0 && (batch.Count >= 100 || size + ch.Content.Length > 4_000_000))
            {
                yield return batch;
                batch = new List<SyncChange>();
                size = 0;
            }
            batch.Add(ch);
            size += ch.Content.Length;
        }
        if (batch.Count > 0) yield return batch;
    }

    // ---- Fichiers locaux ----

    string PathOf(string kind, string id) => kind switch
    {
        SyncKinds.Profile => Path.Combine(RidesDirectory, "profile.json"),
        SyncKinds.Workout => Path.Combine(WorkoutsDirectory, id + ".json"),
        _ => Path.Combine(RidesDirectory, id + ".json"),
    };

    Dictionary<string, LocalDoc> ScanLocal()
    {
        var docs = new Dictionary<string, LocalDoc>();
        void Add(LocalDoc? d)
        {
            if (d is not null) docs[SyncKinds.Key(d.Kind, d.Id)] = d;
        }
        Add(Read(SyncKinds.Profile, SyncKinds.ProfileId));
        if (Directory.Exists(WorkoutsDirectory))
            foreach (var path in Directory.GetFiles(WorkoutsDirectory, "*.json"))
                if (Path.GetFileNameWithoutExtension(path) is var id && SyncKinds.IsValidId(id))
                    Add(Read(SyncKinds.Workout, id));
        if (Directory.Exists(RidesDirectory))
            foreach (var path in Directory.GetFiles(RidesDirectory, "*.json"))
                if (Path.GetFileNameWithoutExtension(path) is var id && id != "profile" && SyncKinds.IsValidId(id))
                    Add(Read(SyncKinds.Ride, id));
        return docs;
    }

    /// <summary>Un fichier local, ou null s'il est absent ou illisible (alors il n'est pas synchronisé).</summary>
    LocalDoc? Read(string kind, string id)
    {
        var path = PathOf(kind, id);
        try
        {
            if (!File.Exists(path)) return null;
            var content = File.ReadAllText(path);
            DateTimeOffset updatedAt;
            string fingerprint = Hash(content);
            switch (kind)
            {
                case SyncKinds.Profile:
                    if (JsonSerializer.Deserialize(content, RideJson.Default.RiderProfile) is not { } p) return null;
                    updatedAt = p.UpdatedAt;
                    break;
                case SyncKinds.Workout:
                    if (JsonSerializer.Deserialize(content, RideJson.Default.Workout) is not { } w || w.Id != id) return null;
                    updatedAt = w.UpdatedAt;
                    break;
                default:
                    if (JsonSerializer.Deserialize(content, RideJson.Default.RideInfo) is not { } r || r.Id != id) return null;
                    updatedAt = r.EndedAt ?? r.StartedAt;
                    var csv = Path.Combine(RidesDirectory, id + ".csv");
                    // Les données grossissent pendant une sortie : leur taille compte dans l'empreinte.
                    fingerprint += ":" + (File.Exists(csv) ? new FileInfo(csv).Length : 0).ToString(CultureInfo.InvariantCulture);
                    break;
            }
            return new LocalDoc(kind, id, content, fingerprint, updatedAt);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    static bool IsValidContent(SyncChange ch)
    {
        try
        {
            return ch.Kind switch
            {
                SyncKinds.Profile => JsonSerializer.Deserialize(ch.Content, RideJson.Default.RiderProfile) is not null,
                SyncKinds.Workout => JsonSerializer.Deserialize(ch.Content, RideJson.Default.Workout) is { } w && w.Id == ch.Id,
                _ => JsonSerializer.Deserialize(ch.Content, RideJson.Default.RideInfo) is { } r && r.Id == ch.Id,
            };
        }
        catch (JsonException) { return false; }
    }

    void DeleteLocal(string kind, string id)
    {
        File.Delete(PathOf(kind, id));
        if (kind == SyncKinds.Ride)
        {
            File.Delete(Path.Combine(RidesDirectory, id + ".csv"));
            File.Delete(Path.Combine(RidesDirectory, id + ".raw.csv"));
        }
    }

    static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    // ---- Données des sorties ----

    async Task UploadRideData(string id)
    {
        var csv = Path.Combine(RidesDirectory, id + ".csv");
        var bytes = File.Exists(csv) ? await File.ReadAllBytesAsync(csv).ConfigureAwait(false) : Array.Empty<byte>();
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(bytes);
        var content = new ByteArrayContent(output.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Headers.ContentEncoding.Add("gzip");
        await SendNoContent(HttpMethod.Put, State.ServerUrl, $"/api/rides/{id}/data", content).ConfigureAwait(false);
    }

    /// <summary>Fichier de données d'une sortie, ou null s'il n'est pas (encore) sur le serveur.</summary>
    async Task<byte[]?> DownloadRideData(string id)
    {
        using var res = await Request(HttpMethod.Get, State.ServerUrl, $"/api/rides/{id}/data", null, auth: true).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(res, auth: true).ConfigureAwait(false);
        var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (!res.Content.Headers.ContentEncoding.Contains("gzip")) return bytes;
        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        await gz.CopyToAsync(output).ConfigureAwait(false);
        return output.ToArray();
    }

    // ---- HTTP ----

    static StringContent Json<T>(T value, JsonTypeInfo<T> type) =>
        new(JsonSerializer.Serialize(value, type), Encoding.UTF8, "application/json");

    async Task<HttpResponseMessage> Request(HttpMethod method, string baseUrl, string path, HttpContent? body, bool auth)
    {
        var req = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path) { Content = body };
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", State.Token);
        try
        {
            return await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // La cause précise (refusé, délai, certificat…) aide à trouver le problème.
            var cause = e.InnerException?.Message ?? e.Message;
            throw new SyncException($"Serveur injoignable ({baseUrl}) : {cause}", e);
        }
        catch (TaskCanceledException e)
        {
            throw new SyncException("Le serveur ne répond pas.", e);
        }
    }

    async Task EnsureSuccess(HttpResponseMessage res, bool auth)
    {
        if (res.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            message = JsonSerializer.Deserialize(text, SyncJson.Default.ErrorResponse)?.Error;
        }
        catch (JsonException) { }
        if (res.StatusCode == HttpStatusCode.Unauthorized && auth)
        {
            lock (State) State.Token = ""; // jeton révoqué ou compte supprimé
            SaveState();
        }
        if (string.IsNullOrEmpty(message))
            message = res.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Trop d'essais : réessayez dans une minute.",
                HttpStatusCode.NotFound => "Cette adresse n'est pas un serveur Velom3d.",
                _ => $"Erreur du serveur ({(int)res.StatusCode}).",
            };
        throw new SyncException(message);
    }

    async Task<T> Send<T>(HttpMethod method, string baseUrl, string path, HttpContent? body, JsonTypeInfo<T> type, bool auth = true)
    {
        using var res = await Request(method, baseUrl, path, body, auth).ConfigureAwait(false);
        await EnsureSuccess(res, auth).ConfigureAwait(false);
        try
        {
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize(text, type) ?? throw new SyncException("Réponse vide du serveur.");
        }
        catch (JsonException e)
        {
            throw new SyncException("Cette adresse n'est pas un serveur Velom3d.", e);
        }
    }

    async Task SendNoContent(HttpMethod method, string baseUrl, string path, HttpContent? body)
    {
        using var res = await Request(method, baseUrl, path, body, auth: true).ConfigureAwait(false);
        await EnsureSuccess(res, auth: true).ConfigureAwait(false);
    }
}
