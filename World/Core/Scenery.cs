using System;
using System.Collections.Generic;

namespace Velom3d.World;

public enum PropKind { Bush, LeafyTree, Conifer, House, Farmhouse }

/// <summary>Un élément du décor posé à côté de la route. <see cref="CourseDistanceM"/> : point de
/// la route le plus proche, pour regrouper le décor par tronçon (seuls les tronçons visibles sont dessinés).</summary>
public readonly record struct Prop(PropKind Kind, double X, double Z, double RotationY, double Scale, double CourseDistanceM);

/// <summary>
/// Place le décor le long du circuit : haies d'arbustes, arbres isolés, deux bois et quatre
/// hameaux. Toujours le même décor pour une graine donnée, rien sur la route.
/// </summary>
public static class Scenery
{
    /// <summary>Demi-largeur de la route (m).</summary>
    public const double RoadHalfWidthM = 3.5;

    static readonly (double From, double To)[] Hamlets = [(550, 800), (1750, 1950), (3050, 3300), (4250, 4450)];
    static readonly (double From, double To)[] Woods = [(2250, 2700), (3600, 3950)];

    public static List<Prop> Generate(LoopCourse course, int seed = 7)
    {
        var rng = new Random(seed);
        var props = new List<Prop>();
        var houses = new List<Prop>();
        double L = course.LengthM;
        double Rand(double a, double b) => a + rng.NextDouble() * (b - a);

        bool Clear(PropKind kind, double x, double z)
        {
            double clearance = kind switch
            {
                PropKind.Bush => 2.0,
                PropKind.House or PropKind.Farmhouse => 8.0,
                _ => 3.0,
            };
            if (course.DistanceToCenterline(x, z) < RoadHalfWidthM + clearance) return false;
            double room = kind is PropKind.House or PropKind.Farmhouse ? 16.0 : 7.0;
            foreach (var h in houses)
                if ((h.X - x) * (h.X - x) + (h.Z - z) * (h.Z - z) < room * room) return false;
            return true;
        }

        void Place(PropKind kind, double d, double right, double scale, double? rotation = null)
        {
            var p = course.At(d);
            var (x, z) = p.Offset(right);
            if (!Clear(kind, x, z)) return;
            var prop = new Prop(kind, x, z, rotation ?? Rand(0, 2 * Math.PI), scale, course.Wrap(d));
            props.Add(prop);
            if (kind is PropKind.House or PropKind.Farmhouse) houses.Add(prop);
        }

        // Hameaux : maisons des deux côtés, façade tournée vers la route.
        foreach (var (from, to) in Hamlets)
            for (double d = from; d < to; d += Rand(22, 38))
                foreach (int side in new[] { -1, 1 })
                {
                    if (rng.NextDouble() < 0.2) continue;
                    var p = course.At(d);
                    // La façade (côté -Z du modèle) regarde la route.
                    double facing = Math.Atan2(-side * p.DirZ, side * p.DirX);
                    var kind = rng.NextDouble() < 0.25 ? PropKind.Farmhouse : PropKind.House;
                    Place(kind, d, side * Rand(15, 22), Rand(0.9, 1.15), facing);
                }

        // Quelques fermes isolées, plus loin de la route.
        for (int i = 0; i < 10; i++)
        {
            double d = Rand(0, L);
            if (In(Hamlets, d) || In(Woods, d)) continue;
            Place(PropKind.Farmhouse, d, (rng.NextDouble() < 0.5 ? -1 : 1) * Rand(35, 70), Rand(1.0, 1.3));
        }

        // Arbres : clairsemés en campagne, serrés dans les bois, rares dans les hameaux.
        foreach (int side in new[] { -1, 1 })
            for (double d = 0; d < L;)
            {
                bool wood = In(Woods, d), hamlet = In(Hamlets, d);
                d += wood ? Rand(3, 6) : Rand(7, 16);
                if (hamlet && rng.NextDouble() < 0.7) continue;
                if (!wood && rng.NextDouble() < 0.25) continue;
                double right = side * (wood ? Rand(6, 45) : 7 + 30 * Math.Pow(rng.NextDouble(), 2));
                var kind = rng.NextDouble() < (wood ? 0.65 : 0.3) ? PropKind.Conifer : PropKind.LeafyTree;
                Place(kind, d, right, Rand(0.75, 1.35));
            }

        // Arbustes en bord de route.
        foreach (int side in new[] { -1, 1 })
            for (double d = 0; d < L; d += Rand(5, 13))
            {
                if (rng.NextDouble() < 0.3) continue;
                Place(PropKind.Bush, d, side * Rand(6, 10), Rand(0.6, 1.3));
            }

        return props;
    }

    static bool In((double From, double To)[] ranges, double d)
    {
        foreach (var (from, to) in ranges)
            if (d >= from && d < to) return true;
        return false;
    }
}
