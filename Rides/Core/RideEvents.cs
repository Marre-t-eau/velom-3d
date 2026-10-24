using System;
using System.Collections.Generic;
using System.Globalization;
using Velom3d.Sensors;

namespace Velom3d.Rides;

/// <summary>
/// Une ligne du fichier de données d'une sortie : à <paramref name="T"/> secondes du départ
/// (heure réelle, pauses comprises), le capteur n° <paramref name="Sensor"/> de l'en-tête a envoyé
/// <paramref name="Value"/> pour <paramref name="Type"/> (power, cadence, hr, resistance…).
/// Le capteur 0 est l'appli elle-même : départ, pause, reprise, consignes, vitesses, fin.
/// </summary>
public readonly record struct RideEvent(double T, string Type, int Sensor, string Value)
{
    public const int App = 0;

    public double Number => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
}

/// <summary>Un capteur utilisé pendant la sortie, avec sa fiche (en-tête du fichier).</summary>
public sealed class RideSensor
{
    /// <summary>Numéro repris dans la colonne <c>sensor</c> du fichier de données (1, 2, 3…).</summary>
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public DeviceKind Kind { get; set; }
    /// <summary>Services Bluetooth annoncés (UUID courts : 1826 FTMS, 1818 puissance, 180d cardio…).</summary>
    public string[] Services { get; set; } = [];
    public DeviceSpecs Specs { get; set; } = new();
}

/// <summary>Une pause, déduite des lignes pause / resume du fichier de données.
/// <see cref="ResumedAt"/> reste vide pour une sortie terminée pendant la pause.</summary>
public sealed class RidePause
{
    public DateTimeOffset PausedAt { get; set; }
    public DateTimeOffset? ResumedAt { get; set; }
    /// <summary>Temps de sortie (pauses exclues) au moment de la pause.</summary>
    public double AtElapsedS { get; set; }

    public double DurationS => ResumedAt is { } r ? Math.Max(0, (r - PausedAt).TotalSeconds) : 0;
}

/// <summary>
/// Reconstruit les mesures seconde par seconde (graphiques, bilan) à partir du fichier de données :
/// on rejoue la sortie avec les valeurs reçues, dans l'ordre, et la même physique qu'en direct.
/// Quand deux capteurs envoient la même chose (puissance du trainer et des pédales), le capteur
/// dédié l'emporte tant qu'il émet, comme pendant la sortie.
/// </summary>
public static class RideReplay
{
    public static (List<RideSample> Samples, List<RidePause> Pauses) Run(RideInfo info, IReadOnlyList<RideEvent> events)
    {
        bool split = info.RiderMassKg > 0;
        var profile = new RiderProfile
        {
            FtpW = info.FtpW,
            RiderMassKg = split ? info.RiderMassKg : info.MassKg,
            BikeMassKg = split ? info.BikeMassKg : 0,
        };
        var rec = new RideRecorder(info.Mode, profile, info.StartedAt) { TargetPowerW = 0 };
        if (info.Crr > 0) rec.Physics.Crr = info.Crr;
        if (info.CdA > 0) rec.Physics.CdA = info.CdA;

        var dedicated = new HashSet<int>();
        foreach (var s in info.Sensors)
            if (s.Kind != DeviceKind.Trainer) dedicated.Add(s.Index);
        var fusion = new Fusion(dedicated);

        double prevT = 0;
        foreach (var e in events)
        {
            if (e.T > prevT)
            {
                rec.Tick(e.T - prevT, (int)Math.Round(fusion.Power), fusion.Cadence, (int)Math.Round(fusion.HeartRate));
                prevT = e.T;
            }
            var at = info.StartedAt.AddSeconds(e.T);
            if (e.Sensor == RideEvent.App)
            {
                switch (e.Type)
                {
                    case "pause": rec.Pause(at); break;
                    case "resume": rec.Resume(at); break;
                    case "target_power": rec.TargetPowerW = (int)e.Number; break;
                    case "target_cadence": rec.TargetCadenceRpm = (int)e.Number; break;
                    case "grade": rec.Physics.GradePct = e.Number; break;
                }
            }
            else fusion.Feed(e.Sensor, e.Type, e.Number, e.T);
        }
        return (rec.Ride.Samples, rec.Ride.Pauses);
    }

    /// <summary>Même règle que <see cref="SensorFusion"/>, sur des valeurs déjà décodées.</summary>
    sealed class Fusion(HashSet<int> dedicated)
    {
        public double Power, Cadence, HeartRate;
        readonly Dictionary<string, (int Sensor, double T)> _last = new();
        readonly Dictionary<int, (int Revs, int Time, double T)> _crank = new();
        readonly Dictionary<int, int> _pendingRevs = new();

        public void Feed(int sensor, string type, double value, double t)
        {
            switch (type)
            {
                case "power": Update("power", sensor, value, t, ref Power); break;
                case "cadence": Update("cadence", sensor, value, t, ref Cadence); break;
                case "hr" when value > 0: Update("hr", sensor, value, t, ref HeartRate); break;
                case "crank_revs": _pendingRevs[sensor] = (int)value; break;
                case "crank_time" when _pendingRevs.Remove(sensor, out var revs): Crank(sensor, revs, (int)value, t); break;
            }
        }

        void Crank(int sensor, int revs, int time, double t)
        {
            if (!_crank.TryGetValue(sensor, out var prev))
            {
                _crank[sensor] = (revs, time, t);
                return;
            }
            if (BleDecoders.CrankCadence(prev.Revs, prev.Time, revs, time) is { } rpm)
            {
                _crank[sensor] = (revs, time, t);
                if (rpm < 250.0) Update("cadence", sensor, rpm, t, ref Cadence);
            }
            else if (t - prev.T > SensorFusion.CadenceTimeoutS)
                Update("cadence", sensor, 0.0, t, ref Cadence);
        }

        void Update(string metric, int sensor, double value, double t, ref double field)
        {
            if (_last.TryGetValue(metric, out var last) && last.Sensor != sensor
                && t - last.T < SensorFusion.StaleAfterS && Rank(sensor) < Rank(last.Sensor))
                return;
            _last[metric] = (sensor, t);
            field = value;
        }

        int Rank(int sensor) => dedicated.Contains(sensor) ? 2 : 1;
    }
}
