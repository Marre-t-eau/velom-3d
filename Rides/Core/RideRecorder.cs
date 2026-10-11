using System;

namespace Velom3d.Rides;

/// <summary>
/// Enregistre une sortie : fait avancer le vélo virtuel avec la puissance mesurée et garde
/// une mesure par seconde de pédalage. Le temps est passé en paramètre (testable sans Godot).
/// </summary>
public sealed class RideRecorder
{
    public Ride Ride { get; }
    public BikePhysics Physics { get; }
    public bool IsPaused { get; private set; }
    /// <summary>Temps de sortie, pauses exclues (s).</summary>
    public double ElapsedS { get; private set; }
    public double DistanceM { get; private set; }
    public double SpeedKmh => Physics.SpeedMps * 3.6;
    public double AveragePowerW => ElapsedS > 0 ? _workJ / ElapsedS : 0.0;
    /// <summary>Consigne ERG courante (0 en mode simulation).</summary>
    public int TargetPowerW { get; set; }

    double _nextSampleAt = 1.0;
    double _workJ;

    public RideRecorder(RideMode mode, RiderProfile profile, DateTimeOffset startedAt, int targetPowerW = 0)
    {
        Physics = new BikePhysics { MassKg = profile.TotalMassKg };
        TargetPowerW = mode == RideMode.TargetPower ? targetPowerW : 0;
        Ride = new Ride(new RideInfo
        {
            Id = Ride.NewId(startedAt),
            StartedAt = startedAt,
            Mode = mode,
            TargetPowerW = TargetPowerW,
            FtpW = profile.FtpW,
            MassKg = profile.TotalMassKg,
            VirtualGears = mode == RideMode.Simulation && profile.VirtualGears ? profile.CreateDrivetrain().Describe() : "",
        });
    }

    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;

    /// <summary>Avance de dt secondes avec les mesures du moment.</summary>
    public void Tick(double dt, int powerW, double cadenceRpm, int heartRateBpm)
    {
        if (IsPaused || dt <= 0) return;
        double end = ElapsedS + dt;
        // Découpe aux secondes rondes pour que chaque mesure tombe pile (même si l'image a sauté).
        while (_nextSampleAt <= end)
        {
            Advance(_nextSampleAt - ElapsedS, powerW);
            Ride.Samples.Add(new RideSample(ElapsedS, Math.Max(0, powerW), cadenceRpm, heartRateBpm,
                SpeedKmh, DistanceM, Physics.GradePct, TargetPowerW));
            _nextSampleAt += 1.0;
        }
        Advance(end - ElapsedS, powerW);
    }

    void Advance(double dt, int powerW)
    {
        if (dt <= 0) return;
        double before = Physics.SpeedMps;
        double after = Physics.Step(powerW, dt);
        DistanceM += (before + after) * 0.5 * dt;
        _workJ += Math.Max(0, powerW) * dt;
        ElapsedS += dt;
    }

    /// <summary>Résumé à jour, à appeler avant d'enregistrer.</summary>
    public Ride Finish()
    {
        Ride.Summarize();
        return Ride;
    }
}
