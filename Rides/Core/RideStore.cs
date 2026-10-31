using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velom3d.Rides;

/// <summary>
/// Sorties enregistrées sur l'appareil, deux fichiers par sortie :
/// <c>{id}.json</c>, l'en-tête (réglages, capteurs et leur fiche, bilan ; lu pour la liste), et
/// <c>{id}.csv</c>, le fichier de données : une ligne par valeur reçue ou événement,
/// <c>t,type,sensor,value</c>, sans aucun traitement. Les mesures seconde par seconde des graphiques
/// en sont reconstruites à l'ouverture (<see cref="RideReplay"/>). Les sorties enregistrées avant ce
/// format (une ligne par seconde, colonne <c>time_s</c>) restent lisibles.
/// </summary>
public sealed class RideStore
{
    public const string EventsHeader = "t,type,sensor,value";

    public string Directory { get; }

    public RideStore(string directory) => Directory = directory;

    string InfoPath(string id) => Path.Combine(Directory, id + ".json");
    string SamplesPath(string id) => Path.Combine(Directory, id + ".csv");
    string ProfilePath => Path.Combine(Directory, "profile.json");

    public void Save(Ride ride)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Données d'abord : un en-tête présent garantit des données complètes.
        AppendEvents(ride);
        WriteAtomic(InfoPath(ride.Info.Id), JsonSerializer.Serialize(ride.Info, RideJson.Default.RideInfo));
    }

    /// <summary>Ajoute les lignes pas encore écrites : le fichier grossit pendant la sortie sans être
    /// réécrit à chaque sauvegarde. Une sortie relue sans nouvelles lignes n'y touche pas.</summary>
    void AppendEvents(Ride ride)
    {
        if (ride.Events.Count <= ride.SavedEvents) return;
        var text = new StringBuilder();
        if (ride.SavedEvents == 0) text.Append(EventsHeader).Append('\n');
        for (int i = ride.SavedEvents; i < ride.Events.Count; i++)
        {
            var e = ride.Events[i];
            text.Append(string.Create(CultureInfo.InvariantCulture, $"{e.T:0.###},{Clean(e.Type)},{e.Sensor},{Clean(e.Value)}\n"));
        }
        if (ride.SavedEvents == 0) File.WriteAllText(SamplesPath(ride.Info.Id), text.ToString());
        else File.AppendAllText(SamplesPath(ride.Info.Id), text.ToString());
        ride.SavedEvents = ride.Events.Count;
    }

    static string Clean(string s) => s.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>En-têtes de toutes les sorties, la plus récente d'abord. Les fichiers illisibles sont ignorés.</summary>
    public List<RideInfo> List()
    {
        var rides = new List<RideInfo>();
        if (!System.IO.Directory.Exists(Directory)) return rides;
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            if (Path.GetFileName(path) == "profile.json") continue;
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(path), RideJson.Default.RideInfo) is { Id.Length: > 0 } info)
                    rides.Add(info);
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
        rides.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return rides;
    }

    public Ride? Load(string id)
    {
        try
        {
            var info = JsonSerializer.Deserialize(File.ReadAllText(InfoPath(id)), RideJson.Default.RideInfo);
            if (info is null) return null;
            if (!File.Exists(SamplesPath(id))) return new Ride(info);
            using var lines = File.ReadLines(SamplesPath(id)).GetEnumerator();
            if (!lines.MoveNext()) return new Ride(info);
            if (lines.Current.StartsWith("time_s"))
            {
                // Ancien format : déjà une ligne par seconde.
                var samples = new List<RideSample>();
                while (lines.MoveNext())
                    if (ParseSample(lines.Current) is { } s) samples.Add(s);
                return new Ride(info, samples);
            }
            var ride = new Ride(info);
            while (lines.MoveNext())
                if (ParseEvent(lines.Current) is { } e) ride.Events.Add(e);
            ride.SavedEvents = ride.Events.Count;
            ride.Rebuild();
            return ride;
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public void Delete(string id)
    {
        File.Delete(Path.Combine(Directory, id + ".raw.csv")); // journal brut d'une version de test
        File.Delete(InfoPath(id));
        File.Delete(SamplesPath(id));
    }

    public RiderProfile LoadProfile()
    {
        try
        {
            if (File.Exists(ProfilePath) &&
                JsonSerializer.Deserialize(File.ReadAllText(ProfilePath), RideJson.Default.RiderProfile) is { } p)
                return p;
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new RiderProfile();
    }

    public void SaveProfile(RiderProfile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        profile.UpdatedAt = DateTimeOffset.Now;
        WriteAtomic(ProfilePath, JsonSerializer.Serialize(profile, RideJson.Default.RiderProfile));
    }

    static RideEvent? ParseEvent(string line)
    {
        var f = line.Split(',', 4);
        if (f.Length < 4 || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            || !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sensor))
            return null; // ligne abîmée (appli coupée en pleine écriture)
        return new RideEvent(t, f[1], sensor, f[3]);
    }

    static RideSample? ParseSample(string line)
    {
        var f = line.Split(',');
        if (f.Length < 8 || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            return null; // en-tête ou ligne abîmée
        static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        // La cadence cible est arrivée après coup : les anciennes sorties ont 8 colonnes.
        return new RideSample(t, I(f[1]), D(f[2]), I(f[3]), D(f[4]), D(f[5]), D(f[6]), I(f[7]), f.Length > 8 ? I(f[8]) : 0);
    }

    /// <summary>Écrit à côté puis remplace : une appli tuée en pleine écriture ne laisse pas de fichier tronqué.</summary>
    static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Sérialisation générée à la compilation : pas de réflexion, sûr sur Android.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RideInfo))]
[JsonSerializable(typeof(RideSensor))]
[JsonSerializable(typeof(RiderProfile))]
[JsonSerializable(typeof(Workout))]
internal partial class RideJson : JsonSerializerContext;
