using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Velom3d.Rides;

/// <summary>Entraînements enregistrés sur l'appareil, un fichier <c>{id}.json</c> chacun.</summary>
public sealed class WorkoutStore
{
    public string Directory { get; }

    public WorkoutStore(string directory) => Directory = directory;

    string PathOf(string id) => Path.Combine(Directory, id + ".json");

    /// <summary>Tous les entraînements, du plus récemment modifié au plus ancien. Au tout premier
    /// appel (dossier absent), des exemples sont créés pour montrer à quoi ça ressemble.</summary>
    public List<Workout> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var w in Workout.Examples(Workout.ExamplesDate)) Save(w);
            foreach (var w in Workout.LaterExamples(Workout.ExamplesDate)) MarkAdded(w.Id);
        }
        else AddLaterExamples();
        var workouts = new List<Workout>();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
            if (Read(path) is { } w) workouts.Add(w);
        workouts.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return workouts;
    }

    /// <summary>Marque (non synchronisée : pas un .json) qu'un exemple ajouté après coup a été proposé,
    /// par exemple <c>.exemple-endurance</c>.</summary>
    string MarkerOf(string id) => Path.Combine(Directory, "." + id);

    void MarkAdded(string id) => File.WriteAllText(MarkerOf(id), "");

    /// <summary>Exemples ajoutés après la première version : chacun est proposé une seule fois aux appareils
    /// qui ont déjà leurs entraînements (pas recréé s'il est supprimé ensuite).</summary>
    void AddLaterExamples()
    {
        foreach (var w in Workout.LaterExamples(Workout.ExamplesDate))
        {
            if (File.Exists(MarkerOf(w.Id))) continue;
            if (!File.Exists(PathOf(w.Id))) Save(w);
            MarkAdded(w.Id);
        }
    }

    public Workout? Load(string id) => File.Exists(PathOf(id)) ? Read(PathOf(id)) : null;

    public void Save(Workout workout)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(workout.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(workout, RideJson.Default.Workout));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string id) => File.Delete(PathOf(id));

    static Workout? Read(string path)
    {
        try
        {
            if (JsonSerializer.Deserialize(File.ReadAllText(path), RideJson.Default.Workout) is { Id.Length: > 0 } w)
            {
                w.Clamp();
                return w;
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return null;
    }
}
