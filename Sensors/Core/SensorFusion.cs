using System;
using System.Collections.Generic;

namespace Velom3d.Sensors;

public enum Metric { Power, Cadence, HeartRate, Speed }

public enum Source { Ftms, CyclingPower, HeartRate }

/// <summary>
/// Transforme les trames brutes en mesures. Quand un même chiffre arrive de plusieurs
/// capteurs (puissance du trainer ET des pédales), le capteur dédié l'emporte tant qu'il
/// émet ; l'autre reprend la main s'il se tait plus de <see cref="StaleAfterS"/>.
/// Indépendant de Godot : le temps est passé en paramètre, ce qui rend tout testable.
/// </summary>
public sealed class SensorFusion
{
    public const double StaleAfterS = 3.0;
    /// <summary>Sans nouveau tour de manivelle pendant ce délai, la cadence retombe à 0.</summary>
    public const double CadenceTimeoutS = 2.5;

    static readonly Dictionary<(Metric, Source), int> Priority = new()
    {
        [(Metric.Power, Source.Ftms)] = 1, [(Metric.Power, Source.CyclingPower)] = 2,
        [(Metric.Cadence, Source.Ftms)] = 1, [(Metric.Cadence, Source.CyclingPower)] = 2,
        [(Metric.HeartRate, Source.Ftms)] = 1, [(Metric.HeartRate, Source.HeartRate)] = 2,
        [(Metric.Speed, Source.Ftms)] = 1,
    };

    public event Action<Metric, double>? MetricChanged;
    public event Action<ControlPointResponse>? ControlResponse;

    public int Power { get; private set; }
    public double Cadence { get; private set; }
    public int HeartRate { get; private set; }
    public double Speed { get; private set; }

    readonly Dictionary<Metric, (Source Source, double T)> _last = new();
    readonly Dictionary<string, (int Revs, int Time, double T)> _crank = new();

    /// <summary>Point d'entrée de toutes les trames. charUuid court ou long.</summary>
    public void Feed(string address, string charUuid, ReadOnlySpan<byte> data, double now)
    {
        switch (BleUuids.ToShort(charUuid))
        {
            case BleUuids.IndoorBikeData when BleDecoders.ParseIndoorBikeData(data) is { } m:
                if (m.PowerW is { } p) Update(Metric.Power, Source.Ftms, p, now);
                if (m.CadenceRpm is { } c) Update(Metric.Cadence, Source.Ftms, c, now);
                if (m.HeartRateBpm is > 0) Update(Metric.HeartRate, Source.Ftms, m.HeartRateBpm.Value, now);
                if (m.SpeedKmh is { } s) Update(Metric.Speed, Source.Ftms, s, now);
                break;
            case BleUuids.CyclingPowerMeasurement when BleDecoders.ParseCyclingPower(data) is { } m:
                Update(Metric.Power, Source.CyclingPower, m.PowerW, now);
                if (m.CrankRevs is { } revs) HandleCrank(address, revs, m.CrankEventTime!.Value, now);
                break;
            case BleUuids.HeartRateMeasurement when BleDecoders.ParseHeartRate(data) is { } m:
                Update(Metric.HeartRate, Source.HeartRate, m.HeartRateBpm, now);
                break;
            case BleUuids.FitnessMachineControlPoint when BleDecoders.ParseControlPointResponse(data) is { } r:
                ControlResponse?.Invoke(r);
                break;
        }
    }

    public void Forget(string address) => _crank.Remove(address);

    void HandleCrank(string address, int revs, int eventTime, double now)
    {
        if (!_crank.TryGetValue(address, out var prev))
        {
            _crank[address] = (revs, eventTime, now);
            return;
        }
        if (BleDecoders.CrankCadence(prev.Revs, prev.Time, revs, eventTime) is { } rpm)
        {
            _crank[address] = (revs, eventTime, now);
            if (rpm < 250.0) // filtre les valeurs aberrantes après une reconnexion
                Update(Metric.Cadence, Source.CyclingPower, rpm, now);
        }
        else if (now - prev.T > CadenceTimeoutS)
        {
            Update(Metric.Cadence, Source.CyclingPower, 0.0, now);
        }
    }

    void Update(Metric metric, Source source, double value, double now)
    {
        if (_last.TryGetValue(metric, out var last) && last.Source != source)
        {
            bool fresh = now - last.T < StaleAfterS;
            if (fresh && Priority[(metric, source)] < Priority[(metric, last.Source)])
                return;
        }
        _last[metric] = (source, now);
        switch (metric)
        {
            case Metric.Power: Power = (int)Math.Round(value); break;
            case Metric.Cadence: Cadence = value; break;
            case Metric.HeartRate: HeartRate = (int)Math.Round(value); break;
            case Metric.Speed: Speed = value; break;
        }
        MetricChanged?.Invoke(metric, value);
    }
}
