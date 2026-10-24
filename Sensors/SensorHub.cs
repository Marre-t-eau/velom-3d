using Godot;

namespace Velom3d.Sensors;

/// <summary>
/// Autoload "Sensors" : point d'entrée unique pour le reste du jeu.
/// <code>
/// SensorHub.Instance.PowerChanged += w => ...;   // ou SensorHub.Instance.Power
/// SensorHub.Instance.EnterSimulation(0.0);        // passe en mode simulation (pente), quitte l'ERG
/// SensorHub.Instance.SetGrade(4.5);               // change la pente en cours de simulation
/// SensorHub.Instance.EnterErg(250);               // passe en mode ERG
/// SensorHub.Instance.SetTargetPower(260);         // change la consigne ERG
/// </code>
/// </summary>
public partial class SensorHub : Node
{
    public static SensorHub Instance { get; private set; } = null!;

    [Signal] public delegate void PowerChangedEventHandler(int watts);
    [Signal] public delegate void CadenceChangedEventHandler(double rpm);
    [Signal] public delegate void HeartRateChangedEventHandler(int bpm);
    [Signal] public delegate void SpeedChangedEventHandler(double kmh);
    [Signal] public delegate void DeviceConnectedEventHandler(string address, string[] services);
    [Signal] public delegate void DeviceDisconnectedEventHandler(string address);
    /// <summary>La liste <see cref="Devices"/> ou l'état du scan a changé : l'écran se redessine.</summary>
    [Signal] public delegate void DevicesChangedEventHandler();
    [Signal] public delegate void ControlResultEventHandler(int requestOpcode, bool ok);
    [Signal] public delegate void StatusMessageEventHandler(string text);

    /// <summary>Chaque valeur reçue d'un capteur, telle qu'il l'envoie : adresse, type, valeur
    /// (voir <see cref="SensorValues"/>). Sert au fichier de données des sorties.</summary>
    public event System.Action<string, string, string>? Measured;
    /// <summary>Chaque commande envoyée au trainer : adresse, type, valeur.</summary>
    public event System.Action<string, string, string>? Commanded;

    public SensorFusion Fusion { get; } = new();
    /// <summary>Appareils vus au scan et état de leur connexion.</summary>
    public DeviceList Devices { get; } = new();
    public SensorBackend? Backend { get; private set; }
    public bool IsScanning { get; private set; }
    /// <summary>Appareil qui expose FTMS : c'est lui qu'on pilote.</summary>
    public string TrainerAddress { get; private set; } = "";

    public int Power => Fusion.Power;
    public double Cadence => Fusion.Cadence;
    public int HeartRate => Fusion.HeartRate;
    public double Speed => Fusion.Speed;

    static double Now => Time.GetTicksMsec() / 1000.0;

    public override void _EnterTree()
    {
        Instance = this;
        Fusion.MetricChanged += (metric, value) =>
        {
            switch (metric)
            {
                case Metric.Power: EmitSignal(SignalName.PowerChanged, Fusion.Power); break;
                case Metric.Cadence: EmitSignal(SignalName.CadenceChanged, value); break;
                case Metric.HeartRate: EmitSignal(SignalName.HeartRateChanged, Fusion.HeartRate); break;
                case Metric.Speed: EmitSignal(SignalName.SpeedChanged, value); break;
            }
        };
        Fusion.ControlResponse += r =>
        {
            EmitSignal(SignalName.ControlResult, r.RequestOpcode, r.Ok);
            if (!r.Ok)
                EmitSignal(SignalName.StatusMessage,
                    $"Le trainer a refusé : {FtmsCommands.Describe(r.RequestOpcode)} ({FtmsCommands.DescribeResult(r.Result)}).");
        };
        Devices.Changed += () => EmitSignal(SignalName.DevicesChanged);
    }

    /// <summary>Change de transport : les appareils de l'ancien sont déconnectés et oubliés.</summary>
    public void UseBackend(SensorBackend backend)
    {
        if (Backend is not null)
        {
            foreach (var d in Devices.Sorted)
                if (d.State != LinkState.Available) Backend.DisconnectDevice(d.Address);
            Backend.QueueFree();
        }
        foreach (var d in Devices.Sorted) Devices.MarkDisconnected(d.Address);
        Devices.ClearAvailable();
        Backend = backend;
        TrainerAddress = "";
        IsScanning = false;
        AddChild(backend);
        backend.Notified += (address, charUuid, data) =>
        {
            if (Measured is { } measured)
                foreach (var (type, value) in SensorValues.Decode(charUuid, data)) measured(address, type, value);
            Fusion.Feed(address, charUuid, data, Now);
        };
        backend.Read += (address, charUuid, data) => Devices.Get(address)?.Specs.Apply(charUuid, data);
        backend.DeviceFound += (a, n, s, rssi) => Devices.Found(a, n, s, rssi, Now);
        backend.ScanFinished += () =>
        {
            IsScanning = false;
            EmitSignal(SignalName.DevicesChanged);
        };
        backend.DeviceConnected += OnDeviceConnected;
        backend.DeviceDisconnected += OnDeviceDisconnected;
        backend.Error += m => EmitSignal(SignalName.StatusMessage, m);
        EmitSignal(SignalName.DevicesChanged);
    }

    public void StartScan(double timeoutS = 10.0)
    {
        if (Backend is null) return;
        Devices.ClearAvailable();
        IsScanning = true;
        EmitSignal(SignalName.DevicesChanged);
        Backend.StartScan(timeoutS);
    }

    public void StopScan()
    {
        if (IsScanning) Backend?.StopScan();
    }

    public void ConnectDevice(string address)
    {
        if (Backend is null || !Devices.MarkConnecting(address, Now)) return;
        Backend.ConnectDevice(address);
    }

    public void DisconnectDevice(string address)
    {
        if (Backend is null || Devices.Get(address) is not { State: not LinkState.Available }) return;
        Backend.DisconnectDevice(address);
        // On n'attend pas le retour de l'appareil : l'écran doit réagir tout de suite.
        OnDeviceDisconnected(address);
    }

    public override void _Process(double delta)
    {
        foreach (var address in Devices.ExpireConnecting(Now))
        {
            Backend?.DisconnectDevice(address);
            var name = Devices.Get(address)?.DisplayName ?? address;
            EmitSignal(SignalName.StatusMessage, $"{name} ne répond pas. Vérifiez qu'il est allumé et qu'aucune autre appli n'y est connectée.");
        }
    }

    // --- Pilotage du home trainer ---

    public void SetTargetPower(int watts) => SendControl(FtmsCommands.SetTargetPower(watts));

    public void SetGrade(double gradePct, double windMps = 0.0, double crr = 0.004, double cw = 0.51) =>
        SendControl(FtmsCommands.SetSimulation(gradePct, windMps, crr, cw));

    public void SetResistance(double level) => SendControl(FtmsCommands.SetTargetResistance(level));

    /// <summary>Passe en mode ERG (prise de contrôle, démarrage, consigne).</summary>
    public void EnterErg(int watts) => SendControl(FtmsCommands.EnterErg(watts));

    /// <summary>Passe en mode simulation en effaçant d'abord toute consigne ERG.</summary>
    public void EnterSimulation(double gradePct, double windMps = 0.0, double crr = 0.004, double cw = 0.51) =>
        SendControl(FtmsCommands.EnterSimulation(gradePct, windMps, crr, cw));

    void SendControl(params byte[][] frames)
    {
        if (Backend is null || TrainerAddress == "")
        {
            EmitSignal(SignalName.StatusMessage, "Aucun home trainer FTMS connecté");
            return;
        }
        // Le transport les envoie dans l'ordre, une à la fois.
        foreach (var frame in frames) Write(TrainerAddress, frame);
    }

    void Write(string address, byte[] frame)
    {
        if (Commanded is { } commanded)
            foreach (var (type, value) in SensorValues.Command(frame)) commanded(address, type, value);
        Backend!.Write(address, BleUuids.FitnessMachineControlPoint, frame);
    }

    void OnDeviceConnected(string address, string[] services)
    {
        // Connexion arrivée après l'abandon (délai dépassé ou annulée) : on la referme.
        if (Devices.Get(address) is { State: LinkState.Available })
        {
            Backend!.DisconnectDevice(address);
            return;
        }
        Devices.MarkConnected(address, services);
        if (System.Array.IndexOf(services, BleUuids.FitnessMachine) >= 0)
        {
            TrainerAddress = address;
            // FTMS exige de demander le contrôle avant toute commande.
            Write(address, FtmsCommands.RequestControl());
            Write(address, FtmsCommands.Start());
        }
        EmitSignal(SignalName.DeviceConnected, address, services);
    }

    void OnDeviceDisconnected(string address)
    {
        if (Devices.Get(address) is not { State: not LinkState.Available }) return;
        Devices.MarkDisconnected(address);
        if (address == TrainerAddress) TrainerAddress = "";
        Fusion.Forget(address);
        EmitSignal(SignalName.DeviceDisconnected, address);
    }
}
