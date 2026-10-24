using System.Collections.Generic;
using Godot;

namespace Velom3d.Sensors;

/// <summary>Faux home trainer, fausses pédales et fausse ceinture cardio : mode debug
/// (voir <see cref="SensorBackends"/>) et tests automatiques (Tests/GodotSmokeTest).
/// Ils produisent les mêmes octets que de vrais appareils.</summary>
public partial class SimulatedBackend : SensorBackend
{
    public const string Trainer = "SIM:TRAINER";
    public const string Pedals = "SIM:POWER";
    public const string Hrm = "SIM:HRM";
    const double RateHz = 4.0;

    /// <summary>Délais imitant un vrai appareil (secondes). À 0 dans les tests pour aller vite.</summary>
    [Export] public double ScanDelayS { get; set; } = 0.8;
    [Export] public double ConnectDelayS { get; set; } = 0.6;

    public SimulatedTrainer Model { get; } = new();

    /// <summary>Effort variable du cycliste virtuel (mode debug), pour des courbes réalistes.</summary>
    public bool VaryEffort
    {
        get => Model.VaryEffort;
        set => Model.VaryEffort = value;
    }

    readonly HashSet<string> _connected = new();
    double _accum;
    int _scanId;

    static string[]? ServicesOf(string address) => address switch
    {
        Trainer => [BleUuids.FitnessMachine, BleUuids.CyclingPower],
        Pedals => [BleUuids.CyclingPower],
        Hrm => [BleUuids.HeartRate],
        _ => null,
    };

    public override async void StartScan(double timeoutS = 10.0)
    {
        int id = ++_scanId;
        (string Address, string Name, int Rssi)[] devices =
        [
            (Trainer, "Home trainer (démo)", -48),
            (Hrm, "Ceinture cardio (démo)", -62),
            (Pedals, "Pédales capteur de puissance (démo)", -71),
        ];
        foreach (var (address, name, rssi) in devices)
        {
            await Wait(ScanDelayS);
            if (id != _scanId) return; // scan arrêté ou relancé entre-temps
            RaiseDeviceFound(address, name, ServicesOf(address)!, rssi);
        }
        RaiseScanFinished();
    }

    public override void StopScan()
    {
        _scanId++;
        RaiseScanFinished();
    }

    public override async void ConnectDevice(string address)
    {
        var services = ServicesOf(address);
        if (services is null)
        {
            RaiseError("Appareil simulé inconnu : " + address);
            return;
        }
        await Wait(ConnectDelayS);
        if (!_connected.Add(address)) return;
        // Fiche de l'appareil, lue avant d'annoncer la connexion comme le fait le plugin Android.
        foreach (var (uuid, data) in SpecsOf(address)) RaiseRead(address, uuid, data);
        RaiseDeviceConnected(address, services);
    }

    static (string, byte[])[] SpecsOf(string address)
    {
        static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        (string, byte[])[] common = [(BleUuids.ManufacturerName, Text("Velom3d")), (BleUuids.FirmwareRevision, Text("1.0-demo"))];
        return address switch
        {
            Trainer =>
            [
                .. common, (BleUuids.ModelNumber, Text("Home trainer démo")),
                // Cadence, résistance, puissance ; consignes de résistance, de puissance, simulation.
                (BleUuids.FitnessMachineFeature, [0x82, 0x40, 0x00, 0x00, 0x0C, 0x20, 0x00, 0x00]),
                (BleUuids.SupportedPowerRange, [0x00, 0x00, 0xD0, 0x07, 0x01, 0x00]),       // 0 à 2000 W par 1 W
                (BleUuids.SupportedResistanceLevelRange, [0x00, 0x00, 0xE8, 0x03, 0x0A, 0x00]), // 0 à 100 par 1
                (BleUuids.SupportedInclinationRange, [0x9C, 0xFF, 0xC8, 0x00, 0x01, 0x00]),  // -10 à 20 % par 0,1
            ],
            Pedals => [.. common, (BleUuids.ModelNumber, Text("Pédales démo")), (BleUuids.CyclingPowerFeature, [0x09, 0x00, 0x00, 0x00]), (BleUuids.SensorLocation, [0x07])],
            Hrm => [.. common, (BleUuids.ModelNumber, Text("Ceinture démo")), (BleUuids.BodySensorLocation, [0x01])],
            _ => [],
        };
    }

    public override void DisconnectDevice(string address)
    {
        if (_connected.Remove(address)) RaiseDeviceDisconnected(address);
    }

    public override void Write(string address, string charUuid, byte[] data)
    {
        if (address != Trainer || BleUuids.ToShort(charUuid) != BleUuids.FitnessMachineControlPoint) return;
        var response = Model.HandleControlPoint(data);
        // Réponse "indicate" comme le ferait le trainer, après le retour de Write.
        Callable.From(() => RaiseNotified(Trainer, BleUuids.FitnessMachineControlPoint, response)).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (_connected.Count == 0) return;
        Model.Step(delta);
        _accum += delta;
        if (_accum < 1.0 / RateHz) return;
        _accum = 0.0;
        if (_connected.Contains(Trainer))
        {
            RaiseNotified(Trainer, BleUuids.IndoorBikeData, Model.IndoorBikeFrame(_connected.Contains(Hrm)));
            RaiseNotified(Trainer, BleUuids.CyclingPowerMeasurement, Model.CyclingPowerFrame());
        }
        if (_connected.Contains(Pedals))
            RaiseNotified(Pedals, BleUuids.CyclingPowerMeasurement, Model.CyclingPowerFrame());
        if (_connected.Contains(Hrm))
            RaiseNotified(Hrm, BleUuids.HeartRateMeasurement, Model.HeartRateFrame());
    }

    SignalAwaiter Wait(double s) => ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
}
