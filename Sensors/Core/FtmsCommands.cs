using System;
using System.Buffers.Binary;

namespace Velom3d.Sensors;

/// <summary>
/// Trames à écrire sur le Fitness Machine Control Point (0x2AD9), spécification FTMS 4.16.
/// Séquence type : RequestControl() puis Start(), puis SetTargetPower() (mode ERG)
/// ou SetSimulation() (mode pente, comme Zwift en balade libre).
/// </summary>
public static class FtmsCommands
{
    public const byte OpRequestControl = 0x00;
    public const byte OpReset = 0x01;
    public const byte OpSetTargetResistance = 0x04;
    public const byte OpSetTargetPower = 0x05;
    public const byte OpStartOrResume = 0x07;
    public const byte OpStopOrPause = 0x08;
    public const byte OpSetSimulation = 0x11;
    public const byte OpResponse = 0x80;

    public static byte[] RequestControl() => [OpRequestControl];
    public static byte[] Reset() => [OpReset];
    public static byte[] Start() => [OpStartOrResume];
    public static byte[] Stop(bool pause = false) => [OpStopOrPause, (byte)(pause ? 0x02 : 0x01)];

    /// <summary>Mode ERG : le trainer impose la puissance quelle que soit la cadence.</summary>
    public static byte[] SetTargetPower(int watts)
    {
        var b = new byte[3];
        b[0] = OpSetTargetPower;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), ClampS16(watts));
        return b;
    }

    /// <summary>Niveau de résistance brut, sans unité (uint8, résolution 0,1 donc 0 à 25,5).
    /// L'échelle réelle dépend du trainer (Supported Resistance Level Range, 0x2AD6).</summary>
    public static byte[] SetTargetResistance(double level) =>
        [OpSetTargetResistance, ClampU8(Math.Round(level * 10.0))];

    /// <summary>Mode simulation : le trainer calcule la résistance à partir de la pente (%),
    /// du vent (m/s), du coefficient de roulement crr et du coefficient de traînée cw (kg/m).</summary>
    public static byte[] SetSimulation(double gradePct, double windMps = 0.0, double crr = 0.004, double cw = 0.51)
    {
        var b = new byte[7];
        b[0] = OpSetSimulation;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), ClampS16(Math.Round(windMps * 1000.0)));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), ClampS16(Math.Round(gradePct * 100.0)));
        b[5] = ClampU8(Math.Round(crr * 10000.0));
        b[6] = ClampU8(Math.Round(cw * 100.0));
        return b;
    }

    /// <summary>Passage en mode ERG : on reprend le contrôle avant la consigne (certains trainers
    /// le perdent après un moment d'inactivité).</summary>
    public static byte[][] EnterErg(int watts) => [RequestControl(), Start(), SetTargetPower(watts)];

    /// <summary>Passage en mode simulation. Reset d'abord : beaucoup de trainers gardent la consigne
    /// ERG précédente et ignorent les paramètres de simulation tant qu'elle est active. Reset fait
    /// perdre le contrôle, d'où la nouvelle demande avant de redémarrer.</summary>
    public static byte[][] EnterSimulation(double gradePct, double windMps = 0.0, double crr = 0.004, double cw = 0.51) =>
        [Reset(), RequestControl(), Start(), SetSimulation(gradePct, windMps, crr, cw)];

    /// <summary>Nom lisible d'une commande, pour les messages d'erreur.</summary>
    public static string Describe(int opcode) => opcode switch
    {
        OpRequestControl => "prise de contrôle",
        OpReset => "remise à zéro",
        OpSetTargetResistance => "niveau de résistance",
        OpSetTargetPower => "puissance imposée (ERG)",
        OpStartOrResume => "démarrage",
        OpStopOrPause => "arrêt",
        OpSetSimulation => "mode simulation",
        _ => $"commande 0x{opcode:x2}",
    };

    /// <summary>Code de résultat FTMS (0x02 à 0x05) en clair.</summary>
    public static string DescribeResult(int result) => result switch
    {
        0x02 => "non prise en charge par ce trainer",
        0x03 => "paramètre refusé",
        0x04 => "échec",
        0x05 => "contrôle non accordé",
        _ => $"code {result}",
    };

    static short ClampS16(double v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    static byte ClampU8(double v) => (byte)Math.Clamp(v, 0, 255);
}
