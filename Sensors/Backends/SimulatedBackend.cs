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
        if (_connected.Add(address)) RaiseDeviceConnected(address, services);
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
