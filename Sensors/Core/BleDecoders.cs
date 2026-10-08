using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Velom3d.Sensors;

/// <summary>Indoor Bike Data (0x2AD2). Un champ vaut null s'il est absent de la trame.</summary>
public sealed record IndoorBikeData
{
    public double? SpeedKmh { get; init; }
    public double? AverageSpeedKmh { get; init; }
    public double? CadenceRpm { get; init; }
    public double? AverageCadenceRpm { get; init; }
    public int? DistanceM { get; init; }
    public int? ResistanceLevel { get; init; }
    public int? PowerW { get; init; }
    public int? AveragePowerW { get; init; }
    public int? EnergyKcal { get; init; }
    public int? HeartRateBpm { get; init; }
    public int? ElapsedS { get; init; }
}

/// <summary>Cycling Power Measurement (0x2A63). La cadence se déduit des tours de manivelle.</summary>
public sealed record CyclingPowerData
{
    public int PowerW { get; init; }
    public double? PedalBalancePct { get; init; }
    public uint? WheelRevs { get; init; }
    public int? WheelEventTime { get; init; }  // 1/2048 s
    public int? CrankRevs { get; init; }
    public int? CrankEventTime { get; init; }  // 1/1024 s
}

public sealed record HeartRateData(int HeartRateBpm, IReadOnlyList<double> RrIntervalsS);

/// <summary>Réponse du Control Point : 1 = succès, 2 = non supporté, 3 = paramètre invalide,
/// 4 = échec, 5 = contrôle non accordé.</summary>
public sealed record ControlPointResponse(int RequestOpcode, int Result)
{
    public bool Ok => Result == 0x01;
}

/// <summary>Décodage des trames BLE standard (little endian). Fonctions pures, sans état.</summary>
public static class BleDecoders
{
    /// <summary>Spécification FTMS 4.9. Une trame tronquée renvoie les champs lus jusque-là.</summary>
    public static IndoorBikeData? ParseIndoorBikeData(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        var r = new FrameReader(d, 2);
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var o = new IndoorBikeData();
        // Bit 0 "More Data" est inversé : 0 => la vitesse instantanée est présente.
        if ((flags & 0x0001) == 0) { if (!r.U16(out var v)) return o; o = o with { SpeedKmh = v * 0.01 }; }
        if ((flags & 0x0002) != 0) { if (!r.U16(out var v)) return o; o = o with { AverageSpeedKmh = v * 0.01 }; }
        if ((flags & 0x0004) != 0) { if (!r.U16(out var v)) return o; o = o with { CadenceRpm = v * 0.5 }; }
        if ((flags & 0x0008) != 0) { if (!r.U16(out var v)) return o; o = o with { AverageCadenceRpm = v * 0.5 }; }
        if ((flags & 0x0010) != 0) { if (!r.U24(out var v)) return o; o = o with { DistanceM = v }; }
        if ((flags & 0x0020) != 0) { if (!r.S16(out var v)) return o; o = o with { ResistanceLevel = v }; }
        if ((flags & 0x0040) != 0) { if (!r.S16(out var v)) return o; o = o with { PowerW = v }; }
        if ((flags & 0x0080) != 0) { if (!r.S16(out var v)) return o; o = o with { AveragePowerW = v }; }
        if ((flags & 0x0100) != 0)
        {
            // total (u16) + par heure (u16) + par minute (u8)
            if (!r.U16(out var v) || !r.Skip(3)) return o;
            o = o with { EnergyKcal = v };
        }
        if ((flags & 0x0200) != 0) { if (!r.U8(out var v)) return o; o = o with { HeartRateBpm = v }; }
        if ((flags & 0x0400) != 0) { if (!r.Skip(1)) return o; } // MET
        if ((flags & 0x0800) != 0) { if (!r.U16(out var v)) return o; o = o with { ElapsedS = v }; }
        return o;
    }

    public static CyclingPowerData? ParseCyclingPower(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4) return null;
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var o = new CyclingPowerData { PowerW = BinaryPrimitives.ReadInt16LittleEndian(d[2..]) };
        var r = new FrameReader(d, 4);
        if ((flags & 0x0001) != 0) { if (!r.U8(out var v)) return o; o = o with { PedalBalancePct = v * 0.5 }; }
        if ((flags & 0x0004) != 0) { if (!r.Skip(2)) return o; } // couple accumulé
        if ((flags & 0x0010) != 0)
        {
            if (!r.U32(out var revs) || !r.U16(out var t)) return o;
            o = o with { WheelRevs = revs, WheelEventTime = t };
        }
        if ((flags & 0x0020) != 0)
        {
            if (!r.U16(out var revs) || !r.U16(out var t)) return o;
            o = o with { CrankRevs = revs, CrankEventTime = t };
        }
        return o;
    }

    public static HeartRateData? ParseHeartRate(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        int flags = d[0];
        var r = new FrameReader(d, 1);
        int bpm;
        if ((flags & 0x01) != 0) { if (!r.U16(out bpm)) return null; }
        else { r.U8(out bpm); }
        if ((flags & 0x08) != 0) r.Skip(2); // énergie dépensée
        var rr = new List<double>();
        if ((flags & 0x10) != 0)
            while (r.U16(out var v)) rr.Add(v / 1024.0);
        return new HeartRateData(bpm, rr);
    }

    public static ControlPointResponse? ParseControlPointResponse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3 || d[0] != FtmsCommands.OpResponse) return null;
        return new ControlPointResponse(d[1], d[2]);
    }

    /// <summary>Tr/min à partir de deux mesures de manivelle successives (compteurs 16 bits
    /// qui rebouclent). Renvoie null s'il n'y a pas eu de nouveau tour.</summary>
    public static double? CrankCadence(int prevRevs, int prevTime, int revs, int time)
    {
        int dRevs = (revs - prevRevs) & 0xFFFF;
        int dTime = (time - prevTime) & 0xFFFF;
        if (dRevs == 0 || dTime == 0) return null;
        return dRevs * 60.0 * 1024.0 / dTime;
    }

    /// <summary>Lecture séquentielle qui échoue proprement si la trame est trop courte.</summary>
    ref struct FrameReader
    {
        readonly ReadOnlySpan<byte> _d;
        int _i;

        public FrameReader(ReadOnlySpan<byte> d, int start) { _d = d; _i = start; }

        bool Take(int n, out ReadOnlySpan<byte> s)
        {
            if (_i + n > _d.Length) { s = default; return false; }
            s = _d.Slice(_i, n);
            _i += n;
            return true;
        }

        public bool Skip(int n) => Take(n, out _);
        public bool U8(out int v) { var ok = Take(1, out var s); v = ok ? s[0] : 0; return ok; }
        public bool U16(out int v) { var ok = Take(2, out var s); v = ok ? BinaryPrimitives.ReadUInt16LittleEndian(s) : 0; return ok; }
        public bool S16(out int v) { var ok = Take(2, out var s); v = ok ? BinaryPrimitives.ReadInt16LittleEndian(s) : 0; return ok; }
        public bool U24(out int v) { var ok = Take(3, out var s); v = ok ? s[0] | s[1] << 8 | s[2] << 16 : 0; return ok; }
        public bool U32(out uint v) { var ok = Take(4, out var s); v = ok ? BinaryPrimitives.ReadUInt32LittleEndian(s) : 0; return ok; }
    }
}
