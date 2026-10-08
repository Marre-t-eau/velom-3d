using System.Linq;
using Godot;
using Velom3d.Sensors;

namespace Velom3d.Tests;

/// <summary>
/// Vérifie que tout fonctionne dans le moteur (autoload, signaux, écran de connexion) sur le simulateur :
///   godot --headless --path . res://Tests/GodotSmokeTest.tscn
/// La logique pure est testée plus finement par dotnet test (dossier tests/).
/// </summary>
public partial class GodotSmokeTest : Node
{
    int _failures;
    int _powerSignals;
    int _okResponses;

    public override async void _Ready()
    {
        var hub = SensorHub.Instance;

        // 1. L'écran de connexion se charge et liste les appareils de démo.
        var screen = GD.Load<PackedScene>("res://Screens/DeviceConnection/DeviceConnectionScreen.tscn").Instantiate();
        AddChild(screen);
        await Wait(4.0);
        Check(hub.Backend is SimulatedBackend, "Sans plugin natif, l'écran passe en mode démo");
        Check(hub.Devices.Sorted.Count == 3, $"Trois appareils de démo listés ({hub.Devices.Sorted.Count})");
        Check(!hub.IsScanning, "Le scan se termine");
        screen.QueueFree();
        await Wait(0.1);

        // 2. Connexion et pilotage du trainer via l'autoload.
        var sim = new SimulatedBackend { ScanDelayS = 0.0, ConnectDelayS = 0.0 };
        hub.UseBackend(sim);
        Check(hub.Devices.Sorted.Count == 0, "Changer de transport oublie les anciens appareils");
        hub.PowerChanged += _ => _powerSignals++;
        hub.ControlResult += (_, ok) => { if (ok) _okResponses++; };
        hub.StartScan();
        await Wait(0.5);
        foreach (var d in hub.Devices.Sorted) hub.ConnectDevice(d.Address);
        Check(hub.Devices.Sorted.All(d => d.State == LinkState.Connecting), "Appareils en cours de connexion");

        await Wait(4.0);
        Check(hub.Devices.Sorted.All(d => d.State == LinkState.Connected), "Trois appareils connectés");
        Check(hub.Devices.ConnectedFor(DeviceKind.PowerMeter)?.Address == SimulatedBackend.Pedals,
            "Les pédales passent avant le trainer pour la puissance");
        Check(hub.TrainerAddress == SimulatedBackend.Trainer, "Trainer FTMS repéré via l'autoload");
        Check(_powerSignals > 5, $"Signal PowerChanged reçu ({_powerSignals} fois)");
        Check(hub.Power is > 150 and < 210, $"Puissance ({hub.Power} W)");
        Check(hub.Cadence is > 80 and < 95, $"Cadence ({hub.Cadence:0.0} tr/min)");
        Check(hub.HeartRate > 60, $"Cardio ({hub.HeartRate} bpm)");
        Check(_okResponses >= 2, "Prise de contrôle + démarrage acceptés");

        double flat = hub.Speed;
        hub.SetGrade(8.0);
        await Wait(1.0);
        Check(Mathf.IsEqualApprox(sim.Model.GradePct, 8.0), "Pente transmise au trainer");
        Check(hub.Speed < flat * 0.6, $"Ça ralentit en montée ({flat:0.0} -> {hub.Speed:0.0} km/h)");

        hub.SetTargetPower(300);
        await Wait(3.0);
        Check(sim.Model.ErgMode && Mathf.Abs(hub.Power - 300) < 15, $"Mode ERG 300 W ({hub.Power} W)");

        hub.DisconnectDevice(SimulatedBackend.Trainer);
        Check(hub.TrainerAddress == "" && hub.Devices.Get(SimulatedBackend.Trainer)?.State == LinkState.Available,
            "Déconnexion du trainer");

        GD.Print(_failures == 0 ? "\nTOUS LES TESTS PASSENT" : $"\n{_failures} ÉCHEC(S)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    SignalAwaiter Wait(double s) => ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    void Check(bool ok, string label)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + label);
        if (!ok) _failures++;
    }
}
