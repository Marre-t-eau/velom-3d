using System;
using System.Buffers.Binary;

namespace Velom3d.Sensors;

/// <summary>
/// Modèle d'un home trainer FTMS avec capteur de puissance, et d'une ceinture cardio.
/// Il fabrique les mêmes octets qu'un vrai appareil : le décodeur et le pilotage ERG/pente
/// sont donc exercés exactement comme en conditions réelles. Indépendant de Godot.
/// </summary>
public sealed class SimulatedTrainer
{
    /// <summary>Effort du cycliste virtuel en mode pente.</summary>
    public double RiderPowerW { get; set; } = 180.0;
    public double RiderCadenceRpm { get; set; } = 88.0;
    public double MassKg { get; set; } = 75.0 + 9.0; // cycliste + vélo

    public bool ErgMode { get; private set; }
    public int TargetPowerW { get; private set; }
    public double GradePct { get; private set; }
    public double WindMps { get; private set; }
    public double Crr { get; private set; } = 0.004;
    public double Cw { get; private set; } = 0.51;

    double _time;
    double _power;
    double _hr = 70.0;
    double _crankRevs;
    double _lastCrankEvent;
    readonly Random _rng;

    public SimulatedTrainer(int seed = 1) => _rng = new Random(seed);

    public int HeartRate => (int)Math.Round(_hr);
    public double Cadence => Math.Max(0.0, RiderCadenceRpm + Math.Sin(_time * 0.7) * 2.0);

    public void Step(double dt)
    {
        _time += dt;
        double goal = ErgMode ? TargetPowerW : RiderPowerW;
        // Le trainer met ~1 s à atteindre une consigne ERG ; la FC suit en ~30 s.
        _power += (goal - _power) * Math.Clamp(dt * 2.0, 0.0, 1.0);
        _hr += (60.0 + _power * 0.45 - _hr) * Math.Clamp(dt / 30.0, 0.0, 1.0);
        double prev = _crankRevs;
        _crankRevs += Cadence / 60.0 * dt;
        if (Math.Floor(_crankRevs) > Math.Floor(prev)) _lastCrankEvent = _time;
    }

    /// <summary>Traite une écriture sur le Control Point et renvoie l'indication de réponse.</summary>
    public byte[] HandleControlPoint(ReadOnlySpan<byte> d)
    {
        if (d.IsEmpty) return [FtmsCommands.OpResponse, 0, 0x03];
        byte op = d[0];
        byte result = 0x01;
        switch (op)
        {
            case FtmsCommands.OpRequestControl or FtmsCommands.OpReset
                or FtmsCommands.OpStartOrResume or FtmsCommands.OpStopOrPause:
                break;
            case FtmsCommands.OpSetTargetPower when d.Length >= 3:
                ErgMode = true;
                TargetPowerW = BinaryPrimitives.ReadInt16LittleEndian(d[1..]);
                break;
            case FtmsCommands.OpSetSimulation when d.Length >= 7:
                ErgMode = false;
                WindMps = BinaryPrimitives.ReadInt16LittleEndian(d[1..]) / 1000.0;
                GradePct = BinaryPrimitives.ReadInt16LittleEndian(d[3..]) / 100.0;
                Crr = d[5] / 10000.0;
                Cw = d[6] / 100.0;
                break;
            case FtmsCommands.OpSetTargetPower or FtmsCommands.OpSetSimulation:
                result = 0x03;
                break;
            default:
                result = 0x02;
                break;
        }
        return [FtmsCommands.OpResponse, op, result];
    }

    /// <summary>Vitesse d'équilibre (m/s) pour une puissance donnée, par dichotomie sur
    /// P = v·(m·g·(Crr·cosθ + sinθ) + cw·(v + vent)²).</summary>
    public double SpeedForPower(double watts)
    {
        double theta = Math.Atan(GradePct / 100.0);
        double lo = 0.0, hi = 30.0;
        for (int i = 0; i < 40; i++)
        {
            double v = (lo + hi) * 0.5;
            double air = v + WindMps;
            double p = v * (MassKg * 9.81 * (Crr * Math.Cos(theta) + Math.Sin(theta)) + Cw * air * Math.Abs(air));
            if (p > watts) hi = v; else lo = v;
        }
        return lo;
    }

    int NoisyPower() => Math.Max(0, (int)Math.Round(_power + (_rng.NextDouble() * 8.0 - 4.0)));

    /// <summary>Flags : vitesse (bit0 = 0), cadence (bit2), puissance (bit6), FC (bit9).</summary>
    public byte[] IndoorBikeFrame(bool includeHeartRate)
    {
        var b = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 0x0244);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Math.Round(SpeedForPower(_power) * 3.6 * 100.0));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)Math.Round(Cadence * 2.0));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), (short)NoisyPower());
        b[8] = includeHeartRate ? (byte)Math.Clamp(HeartRate, 0, 255) : (byte)0;
        return b;
    }

    /// <summary>Flags : données de manivelle présentes (bit5).</summary>
    public byte[] CyclingPowerFrame()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 0x0020);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), (short)NoisyPower());
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)((long)_crankRevs & 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)((long)Math.Round(_lastCrankEvent * 1024.0) & 0xFFFF));
        return b;
    }

    public byte[] HeartRateFrame() => [0x00, (byte)Math.Clamp(HeartRate, 0, 255)];
}
