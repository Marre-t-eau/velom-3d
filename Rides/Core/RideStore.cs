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
/// <c>{id}.json</c> (en-tête et résumé, lu pour la liste) et <c>{id}.csv</c> (une ligne par seconde,
/// lu seulement à l'ouverture d'une sortie ; s'ouvre aussi dans un tableur).
/// </summary>
public sealed class RideStore
{
    const string CsvHeader = "time_s,power_w,cadence_rpm,heart_rate_bpm,speed_kmh,distance_m,grade_pct,target_power_w,target_cadence_rpm";

    public string Directory { get; }

    public RideStore(string directory) => Directory = directory;

    string InfoPath(string id) => Path.Combine(Directory, id + ".json");
    string SamplesPath(string id) => Path.Combine(Directory, id + ".csv");
    string ProfilePath => Path.Combine(Directory, "profile.json");

    public void Save(Ride ride)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var csv = new StringBuilder(CsvHeader).Append('\n');
        foreach (var s in ride.Samples)
            csv.Append(string.Create(CultureInfo.InvariantCulture,
                $"{s.TimeS:0.##},{s.PowerW},{s.CadenceRpm:0.#},{s.HeartRateBpm},{s.SpeedKmh:0.##},{s.DistanceM:0.#},{s.GradePct:0.##},{s.TargetPowerW},{s.TargetCadenceRpm}\n"));
        // Mesures d'abord : un en-tête présent garantit des mesures complètes.
        WriteAtomic(SamplesPath(ride.Info.Id), csv.ToString());
        WriteAtomic(InfoPath(ride.Info.Id), JsonSerializer.Serialize(ride.Info, RideJson.Default.RideInfo));
    }

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
            var samples = new List<RideSample>();
            if (File.Exists(SamplesPath(id)))
                foreach (var line in File.ReadLines(SamplesPath(id)))
                    if (ParseSample(line) is { } s) samples.Add(s);
            return new Ride(info, samples);
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public void Delete(string id)
    {
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
        WriteAtomic(ProfilePath, JsonSerializer.Serialize(profile, RideJson.Default.RiderProfile));
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
[JsonSerializable(typeof(RiderProfile))]
[JsonSerializable(typeof(Workout))]
internal partial class RideJson : JsonSerializerContext;
