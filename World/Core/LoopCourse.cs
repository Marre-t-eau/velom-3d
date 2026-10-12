using System;

namespace Velom3d.World;

/// <summary>Position sur le circuit, dans le plan horizontal (x, z) en mètres, avec la direction de la route.</summary>
public readonly record struct CoursePoint(double X, double Z, double DirX, double DirZ)
{
    /// <summary>Point décalé sur la droite de la route (négatif : à gauche).</summary>
    public (double X, double Z) Offset(double rightM) => (X - DirZ * rightM, Z + DirX * rightM);
}

/// <summary>
/// Circuit plat qui tourne en rond : une boucle fermée, sinueuse, de longueur donnée (5 km par
/// défaut), découpée en points régulièrement espacés. La distance parcourue dans la sortie donne
/// directement la position sur la boucle (un tour de plus tous les 5 km). Sans Godot : testable.
/// </summary>
public sealed class LoopCourse
{
    public const double DefaultLengthM = 5000.0;

    public double LengthM { get; }
    /// <summary>Écart entre deux points du tracé (m).</summary>
    public double StepM { get; }
    public int PointCount => _x.Length;

    readonly double[] _x, _z;

    public LoopCourse(double lengthM = DefaultLengthM, int points = 1000)
    {
        LengthM = lengthM;
        StepM = lengthM / points;

        // Courbe fermée en coordonnées polaires : un rayon qui ondule donne des virages variés,
        // assez doux pour un vélo (rayon de courbure de plusieurs centaines de mètres).
        const int raw = 8192;
        var rx = new double[raw + 1];
        var rz = new double[raw + 1];
        var len = new double[raw + 1];
        for (int i = 0; i <= raw; i++)
        {
            double t = 2.0 * Math.PI * i / raw;
            double r = 1.0 + 0.22 * Math.Sin(2 * t + 0.6) + 0.10 * Math.Cos(3 * t) + 0.04 * Math.Sin(5 * t + 1.3);
            rx[i] = r * Math.Cos(t);
            rz[i] = r * Math.Sin(t);
            if (i > 0) len[i] = len[i - 1] + Math.Sqrt(Sq(rx[i] - rx[i - 1]) + Sq(rz[i] - rz[i - 1]));
        }
        double scale = lengthM / len[raw];

        // Points à abscisse curviligne régulière.
        _x = new double[points];
        _z = new double[points];
        int j = 0;
        for (int k = 0; k < points; k++)
        {
            double s = k * StepM / scale;
            while (j < raw - 1 && len[j + 1] < s) j++;
            double f = (s - len[j]) / (len[j + 1] - len[j]);
            _x[k] = (rx[j] + (rx[j + 1] - rx[j]) * f) * scale;
            _z[k] = (rz[j] + (rz[j + 1] - rz[j]) * f) * scale;
        }
    }

    /// <summary>Position et direction à une distance donnée depuis la ligne de départ (plusieurs tours possibles).</summary>
    public CoursePoint At(double distanceM)
    {
        double d = Wrap(distanceM);
        var (x, z) = Lerp(d);
        // Direction lissée sur une dizaine de mètres : pas d'à-coups d'un segment à l'autre.
        var (ax, az) = Lerp(d - 6.0);
        var (bx, bz) = Lerp(d + 6.0);
        double dx = bx - ax, dz = bz - az;
        double l = Math.Sqrt(dx * dx + dz * dz);
        return new CoursePoint(x, z, dx / l, dz / l);
    }

    /// <summary>Point du tracé numéro i (0 = ligne de départ).</summary>
    public (double X, double Z) Point(int i)
    {
        int n = _x.Length;
        i = ((i % n) + n) % n;
        return (_x[i], _z[i]);
    }

    /// <summary>Distance horizontale (m) entre un point et l'axe de la route, la plus courte.</summary>
    public double DistanceToCenterline(double x, double z)
    {
        double best = double.MaxValue;
        int n = _x.Length;
        for (int i = 0; i < n; i++)
        {
            int k = (i + 1) % n;
            double sx = _x[k] - _x[i], sz = _z[k] - _z[i];
            double t = Math.Clamp(((x - _x[i]) * sx + (z - _z[i]) * sz) / (sx * sx + sz * sz), 0.0, 1.0);
            double d = Sq(x - _x[i] - sx * t) + Sq(z - _z[i] - sz * t);
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }

    public double Wrap(double distanceM) => ((distanceM % LengthM) + LengthM) % LengthM;

    (double X, double Z) Lerp(double distanceM)
    {
        double d = Wrap(distanceM);
        int n = _x.Length;
        double i = d / StepM;
        int a = (int)Math.Floor(i) % n;
        int b = (a + 1) % n;
        double f = i - Math.Floor(i);
        return (_x[a] + (_x[b] - _x[a]) * f, _z[a] + (_z[b] - _z[a]) * f);
    }

    static double Sq(double v) => v * v;
}
