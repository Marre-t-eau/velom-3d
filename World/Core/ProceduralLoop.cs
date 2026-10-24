using System;
using System.Collections.Generic;

namespace Velom3d.World;

/// <summary>
/// Générateur de boucles plates et sinueuses : une courbe fermée dont le rayon ondule, ramenée à la
/// longueur voulue, puis le décor le long de la route. Même graine, même map. A produit
/// <c>Maps/boucle-5km.json</c>.
/// </summary>
public static class ProceduralLoop
{
    /// <param name="spacingM">Écart entre deux points enregistrés ; la map est lissée à la lecture.</param>
    public static CourseMap Generate(string id, string name, double lengthM = 5000.0, int seed = 7, double spacingM = 10.0)
    {
        // Courbe fermée en coordonnées polaires : un rayon qui ondule donne des virages variés,
        // assez doux pour un vélo (rayon de courbure de plusieurs centaines de mètres).
        const int raw = 8192;
        var dense = new List<RoadPoint>(raw);
        for (int i = 0; i < raw; i++)
        {
            double t = 2.0 * Math.PI * i / raw;
            double r = 1.0 + 0.22 * Math.Sin(2 * t + 0.6) + 0.10 * Math.Cos(3 * t) + 0.04 * Math.Sin(5 * t + 1.3);
            dense.Add(new RoadPoint(r * Math.Cos(t), r * Math.Sin(t)));
        }
        double unitLength = new Course(dense, loop: true, stepM: 1e-3).LengthM;
        double scale = lengthM / unitLength;
        int count = (int)Math.Round(lengthM / spacingM);
        // Points régulièrement espacés le long de la courbe.
        var even = new Course(dense, loop: true, stepM: unitLength / count);

        var map = new CourseMap
        {
            Id = id,
            Name = name,
            Description = $"Boucle plate de {lengthM / 1000:0.#} km : arbustes, arbres, deux bois et quatre hameaux.",
            Source = "procedural",
            Loop = true,
            Smooth = true,
            Landmarks = [new MapLandmark { Kind = CourseMap.StartArch, DistanceM = 0 }],
        };
        for (int k = 0; k < even.PointCount; k++)
        {
            var (x, z) = even.Point(k);
            map.Road.Add([x * scale, z * scale, 0.0]);
        }
        map.GenerateScenery(seed);
        return map;
    }
}
