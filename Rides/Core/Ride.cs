using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Velom3d.Rides;

public enum RideMode
{
    /// <summary>Mode ERG : le trainer impose la puissance choisie.</summary>
    TargetPower,
    /// <summary>Mode simulation : la résistance vient de la pente, des pneus et de l'air.</summary>
    Simulation,
    /// <summary>Entraînement structuré : une suite de blocs (ERG, libre ou rampe).</summary>
    Workout,
}

/// <summary>Une mesure par seconde de sortie (pauses exclues). Les cibles valent 0 quand il n'y en a pas :
/// <paramref name="TargetPowerW"/> est la consigne ERG envoyée au trainer à cet instant.</summary>
public readonly record struct RideSample(
    double TimeS, int PowerW, double CadenceRpm, int HeartRateBpm,
    double SpeedKmh, double DistanceM, double GradePct, int TargetPowerW, int TargetCadenceRpm = 0);

/// <summary>Réglages du cycliste qui servent au calcul de la vitesse, du TSS et de l'IF.</summary>
public sealed class RiderProfile
{
    public int FtpW { get; set; } = 200;
    public double RiderMassKg { get; set; } = 75.0;
    public double BikeMassKg { get; set; } = 8.0;
    public RideMode LastMode { get; set; } = RideMode.Simulation;
    public int LastTargetPowerW { get; set; } = 150;
    /// <summary>Date du dernier test FTP réussi (null si la FTP a été saisie à la main).</summary>
    public DateTimeOffset? FtpTestedAt { get; set; }

    /// <summary>Vitesses virtuelles en mode simulation : on change de plateau et de pignon dans
    /// l'appli, la résistance du trainer suit.</summary>
    public bool VirtualGears { get; set; }
    public int[] Chainrings { get; set; } = Gearing.DefaultChainrings;
    public int[] Cogs { get; set; } = Gearing.DefaultCogs;
    public int WheelCircumferenceMm { get; set; } = 2105;

    /// <summary>Transmission réglée, ou celle par défaut si le fichier contient des dentures invalides.</summary>
    public Drivetrain CreateDrivetrain()
    {
        bool ok = Chainrings is { Length: >= 1 and <= 3 } && Cogs is { Length: >= 5 and <= 14 };
        return new Drivetrain(ok ? Chainrings : Gearing.DefaultChainrings, ok ? Cogs : Gearing.DefaultCogs,
            Math.Clamp(WheelCircumferenceMm, 1500, 2500) / 1000.0);
    }

    [JsonIgnore] public double TotalMassKg => RiderMassKg + BikeMassKg;
}

/// <summary>En-tête d'une sortie : ce qu'il faut pour la liste, sans les mesures.</summary>
public sealed class RideInfo
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public RideMode Mode { get; set; }
    public int TargetPowerW { get; set; }
    /// <summary>FTP au moment de la sortie : le TSS d'hier ne change pas si la FTP évolue.</summary>
    public int FtpW { get; set; }
    public double MassKg { get; set; }
    /// <summary>Transmission virtuelle utilisée (« 52/36 × 11-30 (12 v.) »), vide sinon.</summary>
    public string VirtualGears { get; set; } = "";
    /// <summary>Nom de l'entraînement structuré suivi, vide sinon.</summary>
    public string WorkoutName { get; set; } = "";
    /// <summary>FTP mesurée par cette sortie (test FTP), 0 sinon.</summary>
    public int FtpTestW { get; set; }
    public RideSummary Summary { get; set; } = new();
}

public sealed class Ride
{
    public RideInfo Info { get; }
    public List<RideSample> Samples { get; }

    public Ride(RideInfo info, List<RideSample>? samples = null)
    {
        Info = info;
        Samples = samples ?? new List<RideSample>();
    }

    public static string NewId(DateTimeOffset startedAt) => startedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss");

    /// <summary>Recalcule le résumé à partir des mesures.</summary>
    public RideSummary Summarize() => Info.Summary = RideMetrics.Compute(Samples, Info.FtpW);
}
