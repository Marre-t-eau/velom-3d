using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace Velom3d.Sensors;

/// <summary>
/// Toutes les valeurs contenues dans une trame, sans calcul ni filtre : une trame Indoor Bike Data
/// donne la vitesse, la cadence, la puissance, la résistance… telles que l'appareil les envoie.
/// Sert au fichier de données des sorties (une ligne par valeur). Une trame qu'on ne sait pas
/// lire est gardée en hexadécimal (<c>raw_{uuid}</c>) pour ne rien perdre.
/// </summary>
public static class SensorValues
{
    public static List<(string Type, string Value)> Decode(string charUuid, ReadOnlySpan<byte> d)
    {
        var v = new List<(string, string)>();
        switch (BleUuids.ToShort(charUuid))
        {
            case BleUuids.IndoorBikeData when BleDecoders.ParseIndoorBikeData(d) is { } m:
                Add(v, "speed", m.SpeedKmh);
                Add(v, "avg_speed", m.AverageSpeedKmh);
                Add(v, "cadence", m.CadenceRpm);
                Add(v, "avg_cadence", m.AverageCadenceRpm);
                Add(v, "distance", m.DistanceM);
                Add(v, "resistance", m.ResistanceLevel);
                Add(v, "power", m.PowerW);
                Add(v, "avg_power", m.AveragePowerW);
                Add(v, "energy", m.EnergyKcal);
                Add(v, "hr", m.HeartRateBpm);
                Add(v, "elapsed", m.ElapsedS);
                break;
            case BleUuids.CyclingPowerMeasurement when BleDecoders.ParseCyclingPower(d) is { } m:
                Add(v, "power", m.PowerW);
                Add(v, "balance", m.PedalBalancePct);
                Add(v, "wheel_revs", m.WheelRevs);
                Add(v, "wheel_time", m.WheelEventTime);
                Add(v, "crank_revs", m.CrankRevs);
                Add(v, "crank_time", m.CrankEventTime);
                break;
            case BleUuids.HeartRateMeasurement when BleDecoders.ParseHeartRate(d) is { } m:
                Add(v, "hr", m.HeartRateBpm);
                foreach (var rr in m.RrIntervalsS) Add(v, "rr", rr);
                break;
            case BleUuids.FitnessMachineControlPoint when BleDecoders.ParseControlPointResponse(d) is { } r:
                v.Add(("response", $"{Opcode(r.RequestOpcode)} {(r.Ok ? "ok" : "refus " + r.Result)}"));
                break;
            case BleUuids.FitnessMachineStatus when d.Length >= 1:
                Status(v, d);
                break;
            default:
                v.Add(("raw_" + BleUuids.ToShort(charUuid), Convert.ToHexString(d)));
                break;
        }
        return v;
    }

    /// <summary>Commande envoyée au trainer (FTMS Control Point), en clair.</summary>
    public static List<(string Type, string Value)> Command(ReadOnlySpan<byte> d)
    {
        var v = new List<(string, string)>();
        if (d.Length == 0) return v;
        switch (d[0])
        {
            case FtmsCommands.OpSetTargetPower when d.Length >= 3:
                Add(v, "set_power", BinaryPrimitives.ReadInt16LittleEndian(d[1..]));
                break;
            case FtmsCommands.OpSetTargetResistance when d.Length >= 2:
                Add(v, "set_resistance", d[1] * 0.1);
                break;
            case FtmsCommands.OpSetSimulation when d.Length >= 7:
                Simulation(v, "set_", d[1..]);
                break;
            default:
                v.Add(("command", d.Length > 1 ? $"{Opcode(d[0])} {Convert.ToHexString(d[1..])}" : Opcode(d[0])));
                break;
        }
        return v;
    }

    static void Status(List<(string, string)> v, ReadOnlySpan<byte> d)
    {
        var p = d[1..];
        switch (d[0])
        {
            case 0x08 when p.Length >= 2: Add(v, "status_power", BinaryPrimitives.ReadInt16LittleEndian(p)); break;
            case 0x07 when p.Length >= 1: Add(v, "status_resistance", p[0] * 0.1); break;
            case 0x12 when p.Length >= 6: Simulation(v, "status_", p); break;
            case 0x01: v.Add(("status", "reset")); break;
            case 0x02: v.Add(("status", "stop")); break;
            case 0x04: v.Add(("status", "start")); break;
            case 0xFF: v.Add(("status", "control_lost")); break;
            default: v.Add(("status", $"0x{d[0]:X2} {Convert.ToHexString(p)}")); break;
        }
    }

    /// <summary>Paramètres de simulation : vent (m/s), pente (%), pneus (Crr), traînée (kg/m).</summary>
    static void Simulation(List<(string, string)> v, string prefix, ReadOnlySpan<byte> p)
    {
        Add(v, prefix + "wind", BinaryPrimitives.ReadInt16LittleEndian(p) * 0.001);
        Add(v, prefix + "grade", BinaryPrimitives.ReadInt16LittleEndian(p[2..]) * 0.01);
        Add(v, prefix + "crr", p[4] * 0.0001);
        Add(v, prefix + "cw", p[5] * 0.01);
    }

    static string Opcode(int op) => op switch
    {
        FtmsCommands.OpRequestControl => "request_control",
        FtmsCommands.OpReset => "reset",
        FtmsCommands.OpSetTargetResistance => "set_resistance",
        FtmsCommands.OpSetTargetPower => "set_power",
        FtmsCommands.OpStartOrResume => "start",
        FtmsCommands.OpStopOrPause => "stop",
        FtmsCommands.OpSetSimulation => "set_simulation",
        _ => $"0x{op:X2}",
    };

    static void Add(List<(string, string)> v, string type, double? value)
    {
        if (value is { } x) v.Add((type, Number(x)));
    }

    public static string Number(double x) => x.ToString("0.#####", CultureInfo.InvariantCulture);
}
