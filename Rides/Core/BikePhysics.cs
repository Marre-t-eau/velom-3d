using System;

namespace Velom3d.Rides;

/// <summary>
/// Vitesse virtuelle du vélo à partir de la puissance, comme sur la route :
/// m·dv/dt = P/v − m·g·(Crr·cosθ + sinθ) − ½·ρ·CdA·v².
/// Les mêmes coefficients sont envoyés au home trainer en mode simulation, pour que la
/// résistance ressentie et la vitesse affichée racontent la même histoire.
/// </summary>
public sealed class BikePhysics
{
    public const double Gravity = 9.81;
    public const double AirDensity = 1.225;
    /// <summary>Pneus route de bonne qualité sur bitume.</summary>
    public const double DefaultCrr = 0.004;
    /// <summary>Cycliste amateur, mains sur les cocottes.</summary>
    public const double DefaultCdA = 0.40;

    public double MassKg { get; set; } = 83.0;
    public double Crr { get; set; } = DefaultCrr;
    public double CdA { get; set; } = DefaultCdA;
    public double GradePct { get; set; }
    public double WindMps { get; set; }

    /// <summary>Vitesse courante (m/s).</summary>
    public double SpeedMps { get; private set; }

    /// <summary>Coefficient de traînée attendu par FTMS (kg/m) : ½·ρ·CdA.</summary>
    public double WindResistanceCoefficient => 0.5 * AirDensity * CdA;

    public void Reset(double speedMps = 0.0) => SpeedMps = Math.Max(0.0, speedMps);

    /// <summary>Force résistante (N) à la vitesse donnée : pneus, pente, air.</summary>
    public double ResistanceForce(double speedMps)
    {
        double theta = Math.Atan(GradePct / 100.0);
        double air = speedMps + WindMps;
        return MassKg * Gravity * (Crr * Math.Cos(theta) + Math.Sin(theta))
               + WindResistanceCoefficient * air * Math.Abs(air);
    }

    /// <summary>Avance de dt secondes avec la puissance donnée et renvoie la nouvelle vitesse.</summary>
    public double Step(double powerW, double dt)
    {
        // Pas fixes courts : stable même si l'image a pris du retard.
        const double maxStep = 0.05;
        while (dt > 1e-9)
        {
            double h = Math.Min(dt, maxStep);
            // Au démarrage, P/v exploserait : on borne la force motrice comme le ferait l'adhérence.
            double drive = Math.Max(0.0, powerW) / Math.Max(SpeedMps, 1.0);
            double accel = (drive - ResistanceForce(SpeedMps)) / MassKg;
            SpeedMps = Math.Max(0.0, SpeedMps + accel * h);
            dt -= h;
        }
        return SpeedMps;
    }

    /// <summary>Vitesse stabilisée (m/s) pour une puissance constante.</summary>
    public double EquilibriumSpeed(double powerW)
    {
        double lo = 0.0, hi = 40.0;
        for (int i = 0; i < 50; i++)
        {
            double v = (lo + hi) * 0.5;
            if (v * ResistanceForce(v) > powerW) hi = v; else lo = v;
        }
        return lo;
    }
}
