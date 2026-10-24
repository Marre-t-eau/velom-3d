using System;
using Godot;

namespace Velom3d.Sensors;

/// <summary>
/// Bluetooth natif du téléphone ou de la tablette, via le singleton « Velom3dBle » exposé par
/// le plugin Android (addons/velom3d_ble, sources dans android-plugin/). Un plugin iOS qui
/// expose le même singleton (mêmes méthodes, mêmes signaux) fonctionnera sans changer ce fichier.
/// Le plugin ne fait que transporter des octets : tout le décodage reste côté C#.
/// </summary>
public partial class NativeBleBackend : SensorBackend
{
    public const string PluginName = "Velom3dBle";

    GodotObject _plugin = null!;
    double _pendingScanTimeoutS = -1.0;

    /// <summary>Vrai si le plugin natif est présent (export Android avec le plugin activé).</summary>
    public static bool IsAvailable => Engine.HasSingleton(PluginName);

    public override void _Ready()
    {
        _plugin = Engine.GetSingleton(PluginName);
        _plugin.Connect("scan_result", Callable.From<string, string, string, int>((address, name, services, rssi) =>
            RaiseDeviceFound(address, name, SplitServices(services), rssi)));
        _plugin.Connect("scan_finished", Callable.From(RaiseScanFinished));
        _plugin.Connect("connected", Callable.From<string, string>((address, services) =>
            RaiseDeviceConnected(address, SplitServices(services))));
        _plugin.Connect("disconnected", Callable.From<string>(RaiseDeviceDisconnected));
        _plugin.Connect("notified", Callable.From<string, string, byte[]>(RaiseNotified));
        if (_plugin.HasSignal("read_result")) // plugin plus ancien : pas de fiche, rien d'autre ne change
            _plugin.Connect("read_result", Callable.From<string, string, byte[]>(RaiseRead));
        _plugin.Connect("error", Callable.From<string>(RaiseError));
        _plugin.Connect("permissions_result", Callable.From<bool>(OnPermissionsResult));
    }

    public override void StartScan(double timeoutS = 10.0)
    {
        // Android 12+ : BLUETOOTH_SCAN / BLUETOOTH_CONNECT ; avant : localisation. Demandées au premier scan.
        if (!_plugin.Call("hasPermissions").AsBool())
        {
            _pendingScanTimeoutS = timeoutS;
            _plugin.Call("requestPermissions");
            return;
        }
        _plugin.Call("startScan", (int)(timeoutS * 1000.0));
    }

    void OnPermissionsResult(bool granted)
    {
        double timeout = _pendingScanTimeoutS;
        _pendingScanTimeoutS = -1.0;
        if (!granted)
        {
            RaiseError("Autorisation Bluetooth refusée. Autorisez « Appareils à proximité » dans les réglages de l'appli.");
            RaiseScanFinished();
            return;
        }
        if (timeout >= 0.0) StartScan(timeout);
    }

    public override void StopScan() => _plugin.Call("stopScan");
    public override void ConnectDevice(string address) => _plugin.Call("connectDevice", address);
    public override void DisconnectDevice(string address) => _plugin.Call("disconnectDevice", address);

    public override void Write(string address, string charUuid, byte[] data) =>
        _plugin.Call("write", address, BleUuids.ToLong(BleUuids.ToShort(charUuid)), data);

    static string[] SplitServices(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
