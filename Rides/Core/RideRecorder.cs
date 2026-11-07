using System;
using System.Collections.Generic;
using System.Globalization;
using Velom3d.Sensors;

namespace Velom3d.Rides;

/// <summary>
/// Enregistre une sortie : fait avancer le vélo virtuel avec la puissance mesurée et garde
/// une mesure par seconde de pédalage pour l'affichage. Tient aussi le fichier de données
/// (<see cref="Ride.Events"/>) : chaque valeur des capteurs et chaque événement, horodatés.
/// Le temps est passé en paramètre (testable sans Godot).
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
    /// <summary>Consigne ERG courante (0 sans consigne : simulation, bloc libre). Chaque changement
    /// est noté dans le fichier de données.</summary>
    public int TargetPowerW
    {
        get => _targetPowerW;
        set { if (value != _targetPowerW) { _targetPowerW = value; LogApp("target_power", value); } }
    }
    /// <summary>Cadence demandée (0 si libre).</summary>
    public int TargetCadenceRpm
    {
        get => _targetCadenceRpm;
        set { if (value != _targetCadenceRpm) { _targetCadenceRpm = value; LogApp("target_cadence", value); } }
    }
    /// <summary>Heure courante, remplaçable dans les tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    double _nextSampleAt = 1.0;
    double _workJ;
    int _targetPowerW, _targetCadenceRpm;
    readonly Dictionary<string, int> _sensorIndex = new();

    public RideRecorder(RideMode mode, RiderProfile profile, DateTimeOffset startedAt, int targetPowerW = 0, string workoutName = "")
    {
        Physics = new BikePhysics { MassKg = profile.TotalMassKg };
        _targetPowerW = mode == RideMode.Simulation ? 0 : targetPowerW;
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
        Log(startedAt, "start", RideEvent.App, mode.ToString());
        Log(startedAt, "grade", RideEvent.App, SensorValues.Number(Physics.GradePct));
        if (TargetPowerW > 0) Log(startedAt, "target_power", RideEvent.App, TargetPowerW.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Ajoute une ligne au fichier de données.</summary>
    public void Log(DateTimeOffset at, string type, int sensor, string value) =>
        Ride.Events.Add(new RideEvent(Math.Max(0, Math.Round((at - Ride.Info.StartedAt).TotalSeconds, 3)), type, sensor, value));

    /// <summary>Événement de l'appli (capteur 0) : vitesse passée, consigne…</summary>
    public void LogEvent(string type, string value = "", DateTimeOffset? now = null) =>
        Log(now ?? Clock(), type, RideEvent.App, value);

    void LogApp(string type, double value) => LogEvent(type, SensorValues.Number(value));

    /// <summary>Numéro du capteur dans l'en-tête (1, 2, 3…), ajouté à sa première apparition.</summary>
    public int SensorIndex(string address, string name, DeviceKind kind, string[] services, DeviceSpecs specs)
    {
        if (_sensorIndex.TryGetValue(address, out var index)) return index;
        index = Ride.Info.Sensors.Count + 1;
        _sensorIndex[address] = index;
        Ride.Info.Sensors.Add(new RideSensor { Index = index, Name = name, Address = address, Kind = kind, Services = services, Specs = specs });
        return index;
    }

    /// <summary>Met en pause et note l'heure (une seule entrée si déjà en pause).</summary>
    public void Pause(DateTimeOffset? now = null)
    {
        if (IsPaused) return;
        IsPaused = true;
        var at = now ?? Clock();
        Ride.Pauses.Add(new RidePause { PausedAt = at, AtElapsedS = ElapsedS });
        LogEvent("pause", now: at);
    }

    public void Resume(DateTimeOffset? now = null)
    {
        if (!IsPaused) return;
        IsPaused = false;
        var at = now ?? Clock();
        if (Ride.Pauses.Count > 0) Ride.Pauses[^1].ResumedAt = at;
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
        Ride.Info.EndedAt = now ?? Clock();
        LogEvent("finish", now: Ride.Info.EndedAt);
        Ride.Summarize();
        return Ride;
    }
}
