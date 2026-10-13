using System;
using System.Collections.Generic;

namespace Velom3d.Rides;

/// <summary>Sens d'une ligne du journal brut.</summary>
public enum RawDirection
{
    /// <summary>Octets reçus d'un capteur (notification BLE).</summary>
    Received,
    /// <summary>Octets envoyés au home trainer (FTMS Control Point).</summary>
    Sent,
    /// <summary>Événement de l'appli : pause, reprise, changement de vitesse…</summary>
    App,
}

/// <summary>
/// Une ligne du journal brut, telle qu'elle est arrivée ou partie : rien n'est décodé ni moyenné,
/// pour pouvoir tout retraiter plus tard (intervalles RR, équilibre gauche/droite, résistance…).
/// <paramref name="RideS"/> est le temps de sortie à cet instant (pauses exclues, il reste figé pendant une pause).
/// </summary>
public readonly record struct RawEntry(
    DateTimeOffset At, double RideS, RawDirection Direction, string Device, string Address, string Channel, string Data)
{
    public static string Hex(byte[] data) => Convert.ToHexString(data);
}

/// <summary>Journal brut d'une sortie, ajouté au fil de l'eau et écrit par morceaux.</summary>
public sealed class RawLog
{
    public List<RawEntry> Entries { get; } = new();
    /// <summary>Nombre de lignes déjà écrites sur l'appareil.</summary>
    public int Saved { get; set; }

    public void Add(RawEntry entry) => Entries.Add(entry);
}
