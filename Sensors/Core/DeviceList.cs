using System;
using System.Collections.Generic;
using System.Linq;

namespace Velom3d.Sensors;

/// <summary>Rôle d'un appareil dans le jeu, déduit des services qu'il annonce.</summary>
public enum DeviceKind { Trainer, PowerMeter, HeartRate, SpeedCadence, Unknown }

public enum LinkState { Available, Connecting, Connected }

public sealed class DeviceEntry
{
    public required string Address { get; init; }
    public string Name { get; internal set; } = "";
    public string[] Services { get; internal set; } = [];
    public DeviceKind Kind { get; internal set; } = DeviceKind.Unknown;
    public int Rssi { get; internal set; }
    public LinkState State { get; internal set; } = LinkState.Available;
    public double LastSeen { get; internal set; }
    internal double ConnectingSince;
    /// <summary>Fiche lue à la connexion (fabricant, plages de puissance…).</summary>
    public DeviceSpecs Specs { get; } = new();

    public string DisplayName => Name.Length > 0 ? Name : Address;
}

/// <summary>
/// Liste des appareils vus au scan et état de leur connexion, pour l'écran de connexion.
/// Indépendant de Godot : le temps est passé en paramètre, ce qui rend tout testable.
/// </summary>
public sealed class DeviceList
{
    /// <summary>Un appareil qui n'a pas répondu après ce délai repasse « disponible ».</summary>
    public const double ConnectTimeoutS = 15.0;

    readonly Dictionary<string, DeviceEntry> _devices = new();

    public event Action? Changed;

    public static DeviceKind Classify(IEnumerable<string> services)
    {
        var s = services.Select(BleUuids.ToShort).ToHashSet();
        if (s.Contains(BleUuids.FitnessMachine)) return DeviceKind.Trainer;
        if (s.Contains(BleUuids.CyclingPower)) return DeviceKind.PowerMeter;
        if (s.Contains(BleUuids.HeartRate)) return DeviceKind.HeartRate;
        if (s.Contains(BleUuids.CyclingSpeedCadence)) return DeviceKind.SpeedCadence;
        return DeviceKind.Unknown;
    }

    public static string KindLabel(DeviceKind kind) => kind switch
    {
        DeviceKind.Trainer => "Home trainer",
        DeviceKind.PowerMeter => "Capteur de puissance",
        DeviceKind.HeartRate => "Ceinture cardio",
        DeviceKind.SpeedCadence => "Capteur vitesse / cadence",
        _ => "Appareil Bluetooth",
    };

    /// <summary>Appareils triés : connectés d'abord, puis par rôle, puis par nom. Pas par signal :
    /// le RSSI varie à chaque annonce et la liste sauterait sous le doigt.</summary>
    public IReadOnlyList<DeviceEntry> Sorted =>
        _devices.Values
            .OrderBy(d => d.State == LinkState.Available ? 1 : 0)
            .ThenBy(d => d.Kind)
            .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Address, StringComparer.Ordinal)
            .ToList();

    public DeviceEntry? Get(string address) => _devices.GetValueOrDefault(address);

    /// <summary>Appareil connecté qui remplit ce rôle. Un capteur dédié passe avant le home trainer,
    /// qui mesure aussi la puissance et la cadence (comme <see cref="SensorFusion"/>).</summary>
    public DeviceEntry? ConnectedFor(DeviceKind kind) =>
        Sorted.Where(d => d.State == LinkState.Connected
                && (d.Kind == kind || kind == DeviceKind.PowerMeter && d.Kind == DeviceKind.Trainer))
            .OrderBy(d => d.Kind == kind ? 0 : 1)
            .FirstOrDefault();

    public void Found(string address, string name, string[] services, int rssi, double now)
    {
        if (!_devices.TryGetValue(address, out var d))
            _devices[address] = d = new DeviceEntry { Address = address };
        // Une annonce peut arriver sans nom ni services (réponse de scan séparée) : on garde ce qu'on sait.
        if (name.Length > 0) d.Name = name;
        if (services.Length > 0) SetServices(d, services);
        d.Rssi = rssi;
        d.LastSeen = now;
        Changed?.Invoke();
    }

    /// <summary>Renvoie false si l'appareil est inconnu ou déjà en cours de connexion.</summary>
    public bool MarkConnecting(string address, double now)
    {
        if (!_devices.TryGetValue(address, out var d) || d.State != LinkState.Available) return false;
        d.State = LinkState.Connecting;
        d.ConnectingSince = now;
        Changed?.Invoke();
        return true;
    }

    public void MarkConnected(string address, string[] services)
    {
        if (!_devices.TryGetValue(address, out var d))
            _devices[address] = d = new DeviceEntry { Address = address };
        if (services.Length > 0) SetServices(d, services);
        d.State = LinkState.Connected;
        Changed?.Invoke();
    }

    public void MarkDisconnected(string address)
    {
        if (!_devices.TryGetValue(address, out var d) || d.State == LinkState.Available) return;
        d.State = LinkState.Available;
        Changed?.Invoke();
    }

    /// <summary>Nouveau scan : oublie les appareils non connectés, ils réapparaîtront s'ils sont à portée.</summary>
    public void ClearAvailable()
    {
        var gone = _devices.Values.Where(d => d.State == LinkState.Available).Select(d => d.Address).ToList();
        foreach (var a in gone) _devices.Remove(a);
        if (gone.Count > 0) Changed?.Invoke();
    }

    /// <summary>Libère les connexions restées sans réponse. Renvoie les adresses abandonnées.</summary>
    public IReadOnlyList<string> ExpireConnecting(double now)
    {
        var expired = _devices.Values
            .Where(d => d.State == LinkState.Connecting && now - d.ConnectingSince > ConnectTimeoutS)
            .ToList();
        foreach (var d in expired) d.State = LinkState.Available;
        if (expired.Count > 0) Changed?.Invoke();
        return expired.Select(d => d.Address).ToList();
    }

    static void SetServices(DeviceEntry d, string[] services)
    {
        d.Services = services.Select(BleUuids.ToShort).ToArray();
        d.Kind = Classify(d.Services);
    }
}
