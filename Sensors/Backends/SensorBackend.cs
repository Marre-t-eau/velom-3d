using System;
using System.Linq;
using Godot;

namespace Velom3d.Sensors;

/// <summary>
/// Interface commune à tous les transports BLE (simulateur, BLE natif mobile).
/// Un backend ne décode rien : il remonte les octets bruts, <see cref="SensorFusion"/> s'en
/// charge. Ainsi le même décodeur sert quel que soit le transport.
/// </summary>
public abstract partial class SensorBackend : Node
{
    /// <summary>Octets reçus d'une caractéristique (notify ou indicate) : adresse, UUID court, données.</summary>
    public event Action<string, string, byte[]>? Notified;
    /// <summary>Appareil trouvé au scan : adresse, nom, services annoncés (UUID courts), RSSI (dBm).</summary>
    public event Action<string, string, string[], int>? DeviceFound;
    /// <summary>Caractéristique lue à la connexion (fiche de l'appareil) : adresse, UUID court, données.
    /// Arrive avant <see cref="DeviceConnected"/>.</summary>
    public event Action<string, string, byte[]>? Read;
    public event Action? ScanFinished;
    public event Action<string, string[]>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;
    public event Action<string>? Error;

    public abstract void StartScan(double timeoutS = 10.0);
    public abstract void StopScan();
    public abstract void ConnectDevice(string address);
    public abstract void DisconnectDevice(string address);
    /// <summary>Écrit sur une caractéristique de l'appareil (ex. FTMS Control Point).</summary>
    public abstract void Write(string address, string charUuid, byte[] data);

    protected void RaiseNotified(string address, string charUuid, byte[] data) =>
        Notified?.Invoke(address, BleUuids.ToShort(charUuid), data);
    protected void RaiseRead(string address, string charUuid, byte[] data) =>
        Read?.Invoke(address, BleUuids.ToShort(charUuid), data);
    protected void RaiseDeviceFound(string address, string name, string[] services, int rssi) =>
        DeviceFound?.Invoke(address, name, services.Select(BleUuids.ToShort).ToArray(), rssi);
    protected void RaiseScanFinished() => ScanFinished?.Invoke();
    protected void RaiseDeviceConnected(string address, string[] services) =>
        DeviceConnected?.Invoke(address, services.Select(BleUuids.ToShort).ToArray());
    protected void RaiseDeviceDisconnected(string address) => DeviceDisconnected?.Invoke(address);
    protected void RaiseError(string message) => Error?.Invoke(message);
}
