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
    /// <summary>Consigne ERG courante (0 sans consigne : simulation, bloc libre).</summary>
    public int TargetPowerW { get; set; }
    /// <summary>Cadence demandée (0 si libre).</summary>
    public int TargetCadenceRpm { get; set; }

    double _nextSampleAt = 1.0;
    double _workJ;

    public RideRecorder(RideMode mode, RiderProfile profile, DateTimeOffset startedAt, int targetPowerW = 0, string workoutName = "")
    {
        Physics = new BikePhysics { MassKg = profile.TotalMassKg };
        TargetPowerW = mode == RideMode.Simulation ? 0 : targetPowerW;
        Ride = new Ride(new RideInfo
        {
            Id = Ride.NewId(startedAt),
            StartedAt = startedAt,
            Mode = mode,
            TargetPowerW = TargetPowerW,
            FtpW = profile.FtpW,
            MassKg = profile.TotalMassKg,
            RiderMassKg = profile.RiderMassKg,
            BikeMassKg = profile.BikeMassKg,
            Crr = Physics.Crr,
            CdA = Physics.CdA,
            WheelCircumferenceMm = profile.WheelCircumferenceMm,
            VirtualGears = mode == RideMode.Simulation && profile.VirtualGears ? profile.CreateDrivetrain().Describe() : "",
            WorkoutName = mode == RideMode.Workout ? workoutName : "",
        });
    }

    /// <summary>Ajoute une ligne au journal brut, datée du temps de sortie courant.</summary>
    public void LogRaw(DateTimeOffset at, RawDirection direction, string device, string address, string channel, string data) =>
        Ride.Raw.Add(new RawEntry(at, ElapsedS, direction, device, address, channel, data));

    /// <summary>Événement de l'appli dans le journal brut.</summary>
    public void LogEvent(string name, string value = "", DateTimeOffset? now = null) =>
        LogRaw(now ?? DateTimeOffset.Now, RawDirection.App, "", "", name, value);

    /// <summary>Met en pause et note l'heure (une seule entrée si déjà en pause).</summary>
    public void Pause(DateTimeOffset? now = null)
    {
        if (IsPaused) return;
        IsPaused = true;
        Ride.Info.Pauses.Add(new RidePause { PausedAt = now ?? DateTimeOffset.Now, AtElapsedS = ElapsedS });
        LogEvent("pause", now: Ride.Info.Pauses[^1].PausedAt);
    }

    public void Resume(DateTimeOffset? now = null)
    {
        if (!IsPaused) return;
        IsPaused = false;
        var at = now ?? DateTimeOffset.Now;
        if (Ride.Info.Pauses.Count > 0) Ride.Info.Pauses[^1].ResumedAt = at;
        LogEvent("resume", now: at);
    }

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
                SpeedKmh, DistanceM, Physics.GradePct, TargetPowerW, TargetCadenceRpm));
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
    public Ride Finish(DateTimeOffset? now = null)
    {
        Ride.Info.EndedAt = now ?? DateTimeOffset.Now;
        LogEvent("finish", now: Ride.Info.EndedAt);
        Ride.Summarize();
        return Ride;
    }
}
