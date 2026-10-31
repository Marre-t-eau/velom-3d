using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Velom3d.Rides;
using Velom3d.Sync;
using Xunit;

namespace Velom3d.Server.Tests;

/// <summary>Un serveur en mémoire (base SQLite temporaire) et des « appareils » (dossiers temporaires).</summary>
public sealed class SyncTests : IDisposable
{
    const string Url = "http://localhost";
    readonly string _root = Path.Combine(Path.GetTempPath(), "velom3d-sync-" + Guid.NewGuid().ToString("N"));
    readonly WebApplicationFactory<Program> _factory;

    public SyncTests()
    {
        Directory.CreateDirectory(_root);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Velom3d:Database", Path.Combine(_root, "server", "velom3d.db"));
            b.UseSetting("Velom3d:AuthRequestsPerMinute", "1000");
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class Device(string dir, SyncClient sync)
    {
        public SyncClient Sync { get; } = sync;
        public RideStore Rides { get; } = new(Path.Combine(dir, "rides"));
        public WorkoutStore Workouts { get; } = new(Path.Combine(dir, "workouts"));
        public string Dir { get; } = dir;
    }

    Device NewDevice(string name)
    {
        var dir = Path.Combine(_root, name);
        var sync = new SyncClient(Path.Combine(dir, "sync.json"), Path.Combine(dir, "rides"), Path.Combine(dir, "workouts"),
            _factory.Server.CreateHandler());
        return new Device(dir, sync);
    }

    static Ride NewRide(DateTimeOffset start, int powerW)
    {
        var ride = new Ride(new RideInfo
        {
            Id = Ride.NewId(start), StartedAt = start, EndedAt = start.AddMinutes(10), Mode = RideMode.TargetPower,
            TargetPowerW = powerW, FtpW = 250, MassKg = 83, RiderMassKg = 75, BikeMassKg = 8,
        });
        ride.Events.Add(new RideEvent(0, "start", 0, "TargetPower"));
        for (int t = 1; t <= 30; t++) ride.Events.Add(new RideEvent(t, "power", 1, powerW.ToString()));
        return ride;
    }

    static Workout NewWorkout(string name, DateTimeOffset at) => Workout.CreateDefault(at, name);

    [Fact]
    public async Task Accounts_register_login_and_errors()
    {
        var a = NewDevice("a");
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        Assert.True(a.Sync.State.LoggedIn);
        Assert.True(a.Sync.State.Enabled);

        var b = NewDevice("b");
        var dup = await Assert.ThrowsAsync<SyncException>(() => b.Sync.RegisterAsync(Url, "BLACK", "autremotdepasse"));
        Assert.Contains("déjà pris", dup.Message);
        var wrong = await Assert.ThrowsAsync<SyncException>(() => b.Sync.LoginAsync(Url, "black", "mauvais!!"));
        Assert.Contains("incorrect", wrong.Message);
        var unknown = await Assert.ThrowsAsync<SyncException>(() => b.Sync.LoginAsync(Url, "personne", "motdepasse"));
        Assert.Contains("incorrect", unknown.Message);
        var shortPw = await Assert.ThrowsAsync<SyncException>(() => b.Sync.RegisterAsync(Url, "autre", "court"));
        Assert.Contains("8 caractères", shortPw.Message);
        await Assert.ThrowsAsync<SyncException>(() => b.Sync.RegisterAsync(Url, "a b/../c", "motdepasse"));
        Assert.False(b.Sync.State.LoggedIn);

        await b.Sync.LoginAsync(Url, "Black", "motdepasse");
        Assert.Equal("black", b.Sync.State.Username); // le nom tel qu'il a été créé
    }

    [Fact]
    public async Task Second_device_receives_everything()
    {
        var a = NewDevice("a");
        var profile = new RiderProfile { FtpW = 287, RiderMassKg = 68 };
        a.Rides.SaveProfile(profile);
        var w = NewWorkout("Seuil 2 × 20", DateTimeOffset.Now);
        a.Workouts.Save(w);
        var ride = NewRide(new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero), 210);
        a.Rides.Save(ride);

        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        var sent = await a.Sync.SyncAsync();
        Assert.Equal(3, sent.Sent);

        var b = NewDevice("b");
        await b.Sync.LoginAsync(Url, "black", "motdepasse");
        var got = await b.Sync.SyncAsync();
        Assert.Equal(3, got.Received);
        Assert.Equal(287, b.Rides.LoadProfile().FtpW);
        Assert.Equal("Seuil 2 × 20", b.Workouts.Load(w.Id)!.Name);
        var copy = b.Rides.Load(ride.Info.Id)!;
        Assert.Equal(ride.Events.Count, copy.Events.Count);
        Assert.Equal(File.ReadAllText(Path.Combine(a.Rides.Directory, ride.Info.Id + ".csv")),
            File.ReadAllText(Path.Combine(b.Rides.Directory, ride.Info.Id + ".csv")));

        // Rien n'a changé : rien ne passe.
        Assert.Equal(new SyncReport(0, 0, 0), await a.Sync.SyncAsync());
        Assert.Equal(new SyncReport(0, 0, 0), await b.Sync.SyncAsync());
    }

    [Fact]
    public async Task Changes_and_deletions_travel_both_ways()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var w = NewWorkout("Endurance", DateTimeOffset.Now);
        a.Workouts.Save(w);
        var ride = NewRide(new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.Zero), 180);
        a.Rides.Save(ride);
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        await a.Sync.SyncAsync();
        await b.Sync.LoginAsync(Url, "black", "motdepasse");
        await b.Sync.SyncAsync();

        // Modifié sur B, reçu sur A.
        var wb = b.Workouts.Load(w.Id)!;
        wb.Name = "Endurance longue";
        wb.UpdatedAt = DateTimeOffset.Now;
        b.Workouts.Save(wb);
        var p = b.Rides.LoadProfile();
        p.FtpW = 301;
        b.Rides.SaveProfile(p);
        Assert.Equal(2, (await b.Sync.SyncAsync()).Sent);
        Assert.Equal(2, (await a.Sync.SyncAsync()).Received);
        Assert.Equal("Endurance longue", a.Workouts.Load(w.Id)!.Name);
        Assert.Equal(301, a.Rides.LoadProfile().FtpW);

        // Supprimés sur A, supprimés sur B.
        a.Workouts.Delete(w.Id);
        a.Rides.Delete(ride.Info.Id);
        Assert.Equal(2, (await a.Sync.SyncAsync()).Sent);
        Assert.Equal(2, (await b.Sync.SyncAsync()).Deleted);
        Assert.Null(b.Workouts.Load(w.Id));
        Assert.Null(b.Rides.Load(ride.Info.Id));
        Assert.False(File.Exists(Path.Combine(b.Rides.Directory, ride.Info.Id + ".csv")));
    }

    [Fact]
    public async Task Local_changes_win_over_the_server()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var w = NewWorkout("Sweet spot", DateTimeOffset.Now);
        a.Workouts.Save(w);
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        await a.Sync.SyncAsync();
        await b.Sync.LoginAsync(Url, "black", "motdepasse");
        await b.Sync.SyncAsync();

        // Les deux le modifient hors ligne ; A synchronise d'abord.
        var wa = a.Workouts.Load(w.Id)!;
        wa.Name = "Version A";
        wa.UpdatedAt = DateTimeOffset.Now.AddMinutes(5); // même plus récente : B garde la sienne
        a.Workouts.Save(wa);
        var wb = b.Workouts.Load(w.Id)!;
        wb.Name = "Version B";
        b.Workouts.Save(wb);
        await a.Sync.SyncAsync();
        var rb = await b.Sync.SyncAsync();
        Assert.Equal(1, rb.Sent);
        Assert.Equal(0, rb.Received);
        Assert.Equal("Version B", b.Workouts.Load(w.Id)!.Name);
        // A n'avait plus rien de neuf : il prend la dernière version envoyée.
        await a.Sync.SyncAsync();
        Assert.Equal("Version B", a.Workouts.Load(w.Id)!.Name);

        // Supprimé sur A, mais modifié sur B entre-temps : B le garde et le renvoie.
        a.Workouts.Delete(w.Id);
        await a.Sync.SyncAsync();
        wb = b.Workouts.Load(w.Id)!;
        wb.Name = "Version B2";
        b.Workouts.Save(wb);
        await b.Sync.SyncAsync();
        Assert.Equal("Version B2", b.Workouts.Load(w.Id)!.Name);
        await a.Sync.SyncAsync();
        Assert.Equal("Version B2", a.Workouts.Load(w.Id)!.Name);
    }

    [Fact]
    public async Task First_sync_of_a_device_keeps_the_most_recent_settings()
    {
        var a = NewDevice("a");
        a.Rides.SaveProfile(new RiderProfile { FtpW = 290 });
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        await a.Sync.SyncAsync();

        // Un nouvel appareil avec des réglages plus anciens : ceux du compte l'emportent.
        var b = NewDevice("b");
        b.Rides.SaveProfile(new RiderProfile { FtpW = 200 });
        var path = Path.Combine(b.Rides.Directory, "profile.json");
        var json = File.ReadAllText(path);
        var stale = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        stale["UpdatedAt"] = DateTimeOffset.Now.AddDays(-30);
        File.WriteAllText(path, stale.ToJsonString());
        await b.Sync.LoginAsync(Url, "black", "motdepasse");
        await b.Sync.SyncAsync();
        Assert.Equal(290, b.Rides.LoadProfile().FtpW);

        // Un appareil aux réglages plus récents les garde et les envoie.
        var c = NewDevice("c");
        c.Rides.SaveProfile(new RiderProfile { FtpW = 310 });
        await c.Sync.LoginAsync(Url, "black", "motdepasse");
        Assert.Equal(1, (await c.Sync.SyncAsync()).Sent);
        Assert.Equal(310, c.Rides.LoadProfile().FtpW);
        await a.Sync.SyncAsync();
        Assert.Equal(310, a.Rides.LoadProfile().FtpW);
    }

    [Fact]
    public async Task Examples_are_not_duplicated_across_devices()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        Assert.Equal(2, a.Workouts.List().Count);
        Assert.Equal(2, b.Workouts.List().Count);
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        await a.Sync.SyncAsync();
        await b.Sync.LoginAsync(Url, "black", "motdepasse");
        await b.Sync.SyncAsync();
        Assert.Equal(2, b.Workouts.List().Count);

        // Un exemple supprimé sur A disparaît sur B, et pas recréé.
        a.Workouts.Delete("exemple-pma");
        await a.Sync.SyncAsync();
        await b.Sync.SyncAsync();
        Assert.Single(b.Workouts.List());
        var c = NewDevice("c");
        Assert.Equal(2, c.Workouts.List().Count); // nouvel appareil : les exemples sont créés
        await c.Sync.LoginAsync(Url, "black", "motdepasse");
        await c.Sync.SyncAsync();
        Assert.Single(c.Workouts.List()); // la suppression, plus récente, l'emporte
    }

    [Fact]
    public async Task Accounts_do_not_see_each_other()
    {
        var a = NewDevice("a");
        a.Workouts.Save(NewWorkout("Privé", DateTimeOffset.Now));
        a.Rides.Save(NewRide(DateTimeOffset.Now.AddHours(-2), 200));
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        await a.Sync.SyncAsync();

        var other = NewDevice("other");
        await other.Sync.RegisterAsync(Url, "alice", "motdepasse");
        var r = await other.Sync.SyncAsync();
        Assert.Equal(0, r.Received);
        Assert.Empty(other.Rides.List());
    }

    [Fact]
    public async Task Ride_waiting_for_its_data_is_received_later()
    {
        var a = NewDevice("a");
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        var b = NewDevice("b");
        await b.Sync.LoginAsync(Url, "black", "motdepasse");

        // Un en-tête arrivé sans son fichier de données (envoi coupé) : B attend.
        var ride = NewRide(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), 230);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.Sync.State.Token);
        var header = JsonSerializer.Serialize(ride.Info);
        var push = new SyncPushRequest { Changes = [new SyncChange { Kind = SyncKinds.Ride, Id = ride.Info.Id, Content = header, UpdatedAt = ride.Info.StartedAt }] };
        (await http.PostAsync("/api/sync", new StringContent(JsonSerializer.Serialize(push, SyncJson.Default.SyncPushRequest), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
        Assert.Equal(0, (await b.Sync.SyncAsync()).Received);
        Assert.Single(b.Sync.State.Pending);

        (await http.PutAsync($"/api/rides/{ride.Info.Id}/data", new StringContent("t,type,sensor,value\n0,start,0,TargetPower\n")))
            .EnsureSuccessStatusCode();
        Assert.Equal(1, (await b.Sync.SyncAsync()).Received);
        Assert.Empty(b.Sync.State.Pending);
        Assert.NotNull(b.Rides.Load(ride.Info.Id));
    }

    [Fact]
    public async Task Revoked_session_asks_to_log_in_again()
    {
        var a = NewDevice("a");
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        var b = NewDevice("b");
        await b.Sync.LoginAsync(Url, "black", "motdepasse");
        await b.Sync.LogoutAsync();
        Assert.False(b.Sync.State.LoggedIn);
        Assert.True(a.Sync.State.LoggedIn);
        await a.Sync.SyncAsync(); // la session de A reste valable

        // Session de A révoquée sur le serveur (compte supprimé) : erreur claire, jeton oublié.
        var http = _factory.CreateClient();
        var del = new HttpRequestMessage(HttpMethod.Delete, "/api/account")
        {
            Content = new StringContent("{\"password\":\"motdepasse\"}", Encoding.UTF8, "application/json"),
        };
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.Sync.State.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(del)).StatusCode);
        var e = await Assert.ThrowsAsync<SyncException>(() => a.Sync.SyncAsync());
        Assert.Contains("reconnectez-vous", e.Message);
        Assert.False(a.Sync.State.LoggedIn);
        Assert.Equal(e.Message, a.Sync.State.LastError);
        // Les fichiers locaux ne bougent pas, et l'état est relu tel quel au prochain lancement.
        var reloaded = SyncState.Load(Path.Combine(a.Dir, "sync.json"));
        Assert.False(reloaded.LoggedIn);
    }

    [Fact]
    public async Task Not_a_velom3d_server()
    {
        var a = NewDevice("a");
        Assert.Null(SyncClient.NormalizeUrl("  "));
        Assert.Equal("https://exemple.fr", SyncClient.NormalizeUrl("exemple.fr/"));
        Assert.Equal("http://192.168.1.10:8080", SyncClient.NormalizeUrl("http://192.168.1.10:8080"));
        var e = await Assert.ThrowsAsync<SyncException>(() => a.Sync.LoginAsync(Url + "/autre-chose", "black", "motdepasse"));
        Assert.Contains("pas un serveur Velom3d", e.Message);
    }

    [Fact]
    public async Task Server_rejects_bad_documents()
    {
        var a = NewDevice("a");
        await a.Sync.RegisterAsync(Url, "black", "motdepasse");
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.Sync.State.Token);
        async Task<HttpStatusCode> Push(SyncChange ch) => (await http.PostAsync("/api/sync", new StringContent(
            JsonSerializer.Serialize(new SyncPushRequest { Changes = [ch] }, SyncJson.Default.SyncPushRequest), Encoding.UTF8, "application/json"))).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Push(new SyncChange { Kind = SyncKinds.Workout, Id = "../../etc", Content = "{}" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Push(new SyncChange { Kind = "autre", Id = "x", Content = "{}" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Push(new SyncChange { Kind = SyncKinds.Workout, Id = "x", Content = "pas du json" }));
        Assert.Equal(HttpStatusCode.OK, await Push(new SyncChange { Kind = SyncKinds.Workout, Id = "x", Content = "{}" }));

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sync")).StatusCode);
        // Un document que l'appli ne sait pas lire n'écrase rien.
        var r = await a.Sync.SyncAsync();
        Assert.Equal(0, r.Received);
        Assert.False(File.Exists(Path.Combine(a.Dir, "workouts", "x.json")));
    }
}
