using System;
using System.Collections.Generic;

namespace Velom3d.World;

/// <summary>Position sur la route, dans le plan horizontal (x, z) en mètres, avec la direction de la route.</summary>
public readonly record struct CoursePoint(double X, double Z, double DirX, double DirZ)
{
    /// <summary>Point décalé sur la droite de la route (négatif : à gauche).</summary>
    public (double X, double Z) Offset(double rightM) => (X - DirZ * rightM, Z + DirX * rightM);
}

/// <summary>Point de passage d'une route : position horizontale et altitude (m).</summary>
public readonly record struct RoadPoint(double X, double Z, double Altitude = 0.0);

/// <summary>
/// Tracé d'une route, prêt à rouler : les points de la map (lissés si demandé) sont redécoupés en
/// points régulièrement espacés. La distance parcourue dans la sortie donne directement la position :
/// sur une boucle, un tour de plus à chaque longueur ; sur un aller simple, arrêt au bout.
/// Sans Godot : testable.
/// </summary>
public sealed class Course
{
    public double LengthM { get; }
    public bool IsLoop { get; }
    /// <summary>Écart entre deux points du tracé (m).</summary>
    public double StepM { get; }
    /// <summary>Points du tracé : sur une boucle, le dernier rejoint le premier sans le répéter.</summary>
    public int PointCount => _x.Length;

    readonly double[] _x, _z, _alt;

    /// <param name="points">Points de passage, dans l'ordre (une boucle ne répète pas le premier à la fin).</param>
    /// <param name="loop">La route revient à son départ.</param>
    /// <param name="smooth">Courbe lisse passant par les points (Catmull-Rom) : quelques points suffisent pour une map faite à la main.</param>
    public Course(IReadOnlyList<RoadPoint> points, bool loop, bool smooth = false, double stepM = 5.0)
    {
        if (points.Count < 2) throw new ArgumentException("Une route a au moins deux points.", nameof(points));
        IsLoop = loop;
        var path = smooth ? CatmullRom(points, loop, 8) : new List<RoadPoint>(points);
        if (loop) path.Add(path[0]);

        var len = new double[path.Count];
        for (int i = 1; i < path.Count; i++)
            len[i] = len[i - 1] + Math.Sqrt(Sq(path[i].X - path[i - 1].X) + Sq(path[i].Z - path[i - 1].Z));
        LengthM = len[^1];
        if (LengthM <= 0) throw new ArgumentException("Route de longueur nulle.", nameof(points));
        int segments = Math.Max(1, (int)Math.Round(LengthM / stepM));
        StepM = LengthM / segments;

        // Points à abscisse curviligne régulière (le point d'arrivée en plus pour un aller simple).
        int n = loop ? segments : segments + 1;
        _x = new double[n];
        _z = new double[n];
        _alt = new double[n];
        int j = 0;
        for (int k = 0; k < n; k++)
        {
            double s = Math.Min(k * StepM, LengthM);
            while (j < path.Count - 2 && len[j + 1] < s) j++;
            double span = len[j + 1] - len[j];
            double f = span > 0 ? Math.Clamp((s - len[j]) / span, 0, 1) : 0;
            _x[k] = path[j].X + (path[j + 1].X - path[j].X) * f;
            _z[k] = path[j].Z + (path[j + 1].Z - path[j].Z) * f;
            _alt[k] = path[j].Altitude + (path[j + 1].Altitude - path[j].Altitude) * f;
        }
    }

    /// <summary>Position et direction à une distance donnée depuis le départ (plusieurs tours possibles sur une boucle).</summary>
    public CoursePoint At(double distanceM)
    {
        double d = Wrap(distanceM);
        var (x, z, _) = Lerp(d);
        // Direction lissée sur une dizaine de mètres : pas d'à-coups d'un segment à l'autre.
        var (ax, az, _) = Lerp(d - 6.0);
        var (bx, bz, _) = Lerp(d + 6.0);
        double dx = bx - ax, dz = bz - az;
        double l = Math.Sqrt(dx * dx + dz * dz);
        if (l < 1e-9) return new CoursePoint(x, z, 0, -1);
        return new CoursePoint(x, z, dx / l, dz / l);
    }

    /// <summary>Altitude (m) à une distance donnée.</summary>
    public double AltitudeAt(double distanceM) => Lerp(Wrap(distanceM)).Alt;

    /// <summary>Pente (%) à une distance donnée, sur une vingtaine de mètres.</summary>
    public double GradePctAt(double distanceM)
    {
        double d = Wrap(distanceM);
        double a = d - 10, b = d + 10;
        if (!IsLoop) { a = Math.Max(0, a); b = Math.Min(LengthM, b); }
        return b > a ? (Lerp(b).Alt - Lerp(a).Alt) / (b - a) * 100.0 : 0.0;
    }

    /// <summary>Point du tracé numéro i (0 = départ) ; sur une boucle, les numéros tournent.</summary>
    public (double X, double Z) Point(int i)
    {
        int n = _x.Length;
        i = IsLoop ? ((i % n) + n) % n : Math.Clamp(i, 0, n - 1);
        return (_x[i], _z[i]);
    }

    /// <summary>Distance horizontale (m) entre un point et l'axe de la route, la plus courte.</summary>
    public double DistanceToCenterline(double x, double z) => Math.Sqrt(Nearest(x, z).DistSq);

    /// <summary>Distance le long de la route (m) du point de la route le plus proche.</summary>
    public double Project(double x, double z) => Nearest(x, z).Along;

    (double DistSq, double Along) Nearest(double x, double z)
    {
        double best = double.MaxValue, along = 0;
        int n = _x.Length;
        int segments = IsLoop ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            int k = (i + 1) % n;
            double sx = _x[k] - _x[i], sz = _z[k] - _z[i];
            double l2 = sx * sx + sz * sz;
            double t = l2 > 0 ? Math.Clamp(((x - _x[i]) * sx + (z - _z[i]) * sz) / l2, 0.0, 1.0) : 0;
            double d = Sq(x - _x[i] - sx * t) + Sq(z - _z[i] - sz * t);
            if (d < best) { best = d; along = (i + t) * StepM; }
        }
        return (best, along);
    }

    /// <summary>Distance ramenée sur la route : modulo la longueur sur une boucle, bornée au départ et à l'arrivée sinon.</summary>
    public double Wrap(double distanceM) => IsLoop
        ? ((distanceM % LengthM) + LengthM) % LengthM
        : Math.Clamp(distanceM, 0.0, LengthM);

    (double X, double Z, double Alt) Lerp(double distanceM)
    {
        double d = Wrap(distanceM);
        int n = _x.Length;
        double i = d / StepM;
        int a = (int)Math.Floor(i);
        double f = i - a;
        int b;
        if (IsLoop) { a %= n; b = (a + 1) % n; }
        else { a = Math.Min(a, n - 1); b = Math.Min(a + 1, n - 1); }
        return (_x[a] + (_x[b] - _x[a]) * f, _z[a] + (_z[b] - _z[a]) * f, _alt[a] + (_alt[b] - _alt[a]) * f);
    }

    /// <summary>Courbe de Catmull-Rom (centripète simplifiée : uniforme) passant par chaque point.</summary>
    static List<RoadPoint> CatmullRom(IReadOnlyList<RoadPoint> p, bool loop, int subdivisions)
    {
        int n = p.Count;
        RoadPoint P(int i) => loop ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];
        var result = new List<RoadPoint>();
        int segments = loop ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            var (p0, p1, p2, p3) = (P(i - 1), P(i), P(i + 1), P(i + 2));
            for (int s = 0; s < subdivisions; s++)
            {
                double t = (double)s / subdivisions, t2 = t * t, t3 = t2 * t;
                double C(double a, double b, double c, double d) =>
                    0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
                result.Add(new RoadPoint(C(p0.X, p1.X, p2.X, p3.X), C(p0.Z, p1.Z, p2.Z, p3.Z),
                    C(p0.Altitude, p1.Altitude, p2.Altitude, p3.Altitude)));
            }
        }
        if (!loop) result.Add(p[n - 1]);
        return result;
    }

    static double Sq(double v) => v * v;
}
