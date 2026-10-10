using System;
using System.Collections.Generic;

namespace Velom3d.Rides;

public sealed class RideSummary
{
    public double DurationS { get; set; }
    public double DistanceM { get; set; }
    public double AvgPowerW { get; set; }
    public int MaxPowerW { get; set; }
    public double NormalizedPowerW { get; set; }
    /// <summary>IF = NP / FTP.</summary>
    public double IntensityFactor { get; set; }
    /// <summary>TSS = durée (s) × NP × IF / (FTP × 3600) × 100 : 100 = une heure à FTP.</summary>
    public double Tss { get; set; }
    public double WorkKj { get; set; }
    public double Kcal { get; set; }
    public double AvgCadenceRpm { get; set; }
    public double MaxCadenceRpm { get; set; }
    public double AvgHeartRateBpm { get; set; }
    public int MaxHeartRateBpm { get; set; }
    public double AvgSpeedKmh { get; set; }
    public double MaxSpeedKmh { get; set; }
    /// <summary>Secondes passées dans chaque zone de puissance (Coggan, Z1 à Z7).</summary>
    public double[] PowerZonesS { get; set; } = new double[RideMetrics.ZoneCount];
}

/// <summary>Indicateurs d'entraînement calculés sur des mesures à 1 Hz.</summary>
public static class RideMetrics
{
    public const int ZoneCount = 7;
    /// <summary>Bornes hautes des zones Z1 à Z6, en fraction de FTP.</summary>
    static readonly double[] ZoneUpper = [0.55, 0.75, 0.90, 1.05, 1.20, 1.50];
    public static readonly string[] ZoneNames =
        ["Récupération", "Endurance", "Tempo", "Seuil", "VO2max", "Anaérobie", "Neuromusculaire"];

    /// <summary>Rendement musculaire humain moyen : 1 kJ mécanique ≈ 1 kcal dépensée.</summary>
    public const double Efficiency = 0.24;
    public const double KjPerKcal = 4.184;

    public static int ZoneOf(double powerW, int ftpW)
    {
        if (ftpW <= 0) return 0;
        double ratio = powerW / ftpW;
        for (int z = 0; z < ZoneUpper.Length; z++)
            if (ratio < ZoneUpper[z]) return z;
        return ZoneCount - 1;
    }

    public static RideSummary Compute(IReadOnlyList<RideSample> samples, int ftpW)
    {
        var s = new RideSummary();
        int n = samples.Count;
        if (n == 0) return s;

        // Chaque mesure couvre l'écart avec la précédente (1 s en régime normal).
        double prevT = 0.0, work = 0.0, cadSum = 0.0, cadTime = 0.0, hrSum = 0.0, hrTime = 0.0;
        foreach (var x in samples)
        {
            double dt = Math.Max(0.0, x.TimeS - prevT);
            prevT = x.TimeS;
            work += x.PowerW * dt;
            s.MaxPowerW = Math.Max(s.MaxPowerW, x.PowerW);
            // Moyennes de cadence et de cardio sans les zéros (roue libre, pas de ceinture).
            if (x.CadenceRpm > 0) { cadSum += x.CadenceRpm * dt; cadTime += dt; }
            s.MaxCadenceRpm = Math.Max(s.MaxCadenceRpm, x.CadenceRpm);
            if (x.HeartRateBpm > 0) { hrSum += x.HeartRateBpm * dt; hrTime += dt; }
            s.MaxHeartRateBpm = Math.Max(s.MaxHeartRateBpm, x.HeartRateBpm);
            s.MaxSpeedKmh = Math.Max(s.MaxSpeedKmh, x.SpeedKmh);
            s.PowerZonesS[ZoneOf(x.PowerW, ftpW)] += dt;
        }

        s.DurationS = samples[n - 1].TimeS;
        s.DistanceM = samples[n - 1].DistanceM;
        s.AvgPowerW = s.DurationS > 0 ? work / s.DurationS : 0.0;
        s.AvgCadenceRpm = cadTime > 0 ? cadSum / cadTime : 0.0;
        s.AvgHeartRateBpm = hrTime > 0 ? hrSum / hrTime : 0.0;
        s.AvgSpeedKmh = s.DurationS > 0 ? s.DistanceM / s.DurationS * 3.6 : 0.0;
        s.WorkKj = work / 1000.0;
        s.Kcal = s.WorkKj / (KjPerKcal * Efficiency);
        s.NormalizedPowerW = NormalizedPower(samples);
        if (ftpW > 0)
        {
            s.IntensityFactor = s.NormalizedPowerW / ftpW;
            s.Tss = s.DurationS * s.NormalizedPowerW * s.IntensityFactor / (ftpW * 3600.0) * 100.0;
        }
        return s;
    }

    /// <summary>Puissance normalisée : moyenne glissante sur 30 s, puissance 4, moyenne, racine 4.
    /// Sur moins de 30 s, c'est la puissance moyenne.</summary>
    public static double NormalizedPower(IReadOnlyList<RideSample> samples)
    {
        const int window = 30;
        int n = samples.Count;
        if (n == 0) return 0.0;
        if (n < window)
        {
            double sum = 0.0;
            foreach (var x in samples) sum += x.PowerW;
            return sum / n;
        }
        double rolling = 0.0, sum4 = 0.0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            rolling += samples[i].PowerW;
            if (i >= window) rolling -= samples[i - window].PowerW;
            if (i < window - 1) continue;
            double avg = rolling / window;
            sum4 += avg * avg * avg * avg;
            count++;
        }
        return Math.Pow(sum4 / count, 0.25);
    }
}
