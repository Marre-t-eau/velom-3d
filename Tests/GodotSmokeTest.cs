using System.IO;
using System.Linq;
using Godot;
using Velom3d.Rides;
using Velom3d.Screens;
using Velom3d.Sensors;

namespace Velom3d.Tests;

/// <summary>
/// Vérifie que tout fonctionne dans le moteur (autoload, signaux, écrans de connexion, menu, sortie libre,
/// bilan et historique) sur le simulateur :
///   godot --headless --path . res://Tests/GodotSmokeTest.tscn
/// La logique pure est testée plus finement par dotnet test (dossier UnitTests/).
/// </summary>
public partial class GodotSmokeTest : Node
{
    int _failures;
    int _powerSignals;
    int _okResponses;

    public override async void _Ready()
    {
        var hub = SensorHub.Instance;

        // 1. Sans Bluetooth natif (ordinateur), l'écran le dit et ne cherche rien.
        var scenePath = "res://Screens/DeviceConnection/DeviceConnectionScreen.tscn";
        var screen = GD.Load<PackedScene>(scenePath).Instantiate<DeviceConnectionScreen>();
        AddChild(screen);
        await Wait(0.5);
        Check(hub.Backend is null && hub.Devices.Sorted.Count == 0, "Sans plugin natif, aucun appareil inventé");
        OS.SetEnvironment(SensorBackends.DebugEnvVar, "1");
        var debug = SensorBackends.Create();
        Check(debug is SimulatedBackend { VaryEffort: true }, "Mode debug : capteurs simulés via la variable d'environnement");
        debug?.Free();
        OS.UnsetEnvironment(SensorBackends.DebugEnvVar);
        screen.QueueFree();
        await Wait(0.1);

        // 2. Avec un transport branché, l'écran liste les appareils et réutilise ses lignes
        //    quand les annonces Bluetooth se répètent (sinon le défilement au doigt casse).
        hub.UseBackend(new SimulatedBackend { ScanDelayS = 0.1, ConnectDelayS = 0.0 });
        screen = GD.Load<PackedScene>(scenePath).Instantiate<DeviceConnectionScreen>();
        AddChild(screen);
        await Wait(1.0);
        Check(hub.Devices.Sorted.Count == 3, $"Trois appareils listés ({hub.Devices.Sorted.Count})");
        var row = screen.RowFor(SimulatedBackend.Trainer);
        hub.Devices.Found(SimulatedBackend.Trainer, "", [], -30, 99.0); // nouvelle annonce, signal différent
        await Wait(0.1);
        Check(row is not null && screen.RowFor(SimulatedBackend.Trainer) == row && IsInstanceValid(row),
            "Une nouvelle annonce met la ligne à jour sans la recréer");
        screen.QueueFree();
        await Wait(0.1);

        // 3. Connexion et pilotage du trainer via l'autoload.
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

        await RideScreens(sim);

        hub.DisconnectDevice(SimulatedBackend.Trainer);
        Check(hub.TrainerAddress == "" && hub.Devices.Get(SimulatedBackend.Trainer)?.State == LinkState.Available,
            "Déconnexion du trainer");

        GD.Print(_failures == 0 ? "\nTOUS LES TESTS PASSENT" : $"\n{_failures} ÉCHEC(S)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>4. Menu, sortie libre en ERG puis en simulation, bilan et historique.
    /// Les sorties vont dans un dossier jetable ; les changements d'écran sont interceptés.</summary>
    async System.Threading.Tasks.Task RideScreens(SimulatedBackend sim)
    {
        var dir = ProjectSettings.GlobalizePath("user://test-rides-" + Time.GetTicksMsec());
        Screen.Store = new RideStore(dir);
        Node? next = null;
        Navigator.Override = n => next = n;

        var menu = Navigator.Create<MainMenuScreen>(Navigator.Menu);
        AddChild(menu);
        await Wait(0.1);
        Check(menu.StructuredButton.Disabled && !menu.FreeRideButton.Disabled,
            "Menu : entraînement structuré grisé, sortie libre disponible");
        menu.FreeRideButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.1);
        Check(next is FreeRideScreen, "Le menu ouvre la sortie libre");
        menu.QueueFree();

        var ride = next as FreeRideScreen ?? Navigator.Create<FreeRideScreen>(Navigator.FreeRide);
        next = null;
        AddChild(ride);
        await Wait(0.1);
        ride.Start(RideMode.TargetPower, 220);
        await Wait(3.5);
        var rec = ride.Recorder!;
        Check(sim.Model.ErgMode && sim.Model.TargetPowerW == 220, $"Sortie ERG : consigne envoyée ({sim.Model.TargetPowerW} W)");
        Check(rec.Ride.Samples.Count >= 3, $"Une mesure par seconde ({rec.Ride.Samples.Count})");
        Check(rec.SpeedKmh > 5 && rec.DistanceM > 1, $"Le vélo virtuel avance ({rec.SpeedKmh:0.0} km/h, {rec.DistanceM:0} m)");
        ride.OnFinishPressed();
        await Wait(0.1);
        Check(rec.IsPaused && ride.PauseButtonText == "Reprendre", "Terminer : la sortie se met en pause, Pause devient Reprendre");
        ride.OnFinishPressed();
        await Wait(0.3);
        Check(!sim.Model.ErgMode, "Fin de sortie : le trainer n'impose plus de puissance");
        Check(next is RideSummaryScreen, "Fin de sortie : ouverture du bilan");
        ride.QueueFree();

        if (next is RideSummaryScreen summary)
        {
            next = null;
            AddChild(summary);
            await Wait(0.2);
            Check(summary.ChartCount == 4, $"Bilan : courbes puissance, cadence, vitesse, cardio ({summary.ChartCount})");
            Check(summary.Ride.Info.Summary.Tss > 0 && summary.Ride.Info.Summary.Kcal > 0, "Bilan : TSS et calories calculés");
            summary.QueueFree();
        }

        ride = Navigator.Create<FreeRideScreen>(Navigator.FreeRide);
        AddChild(ride);
        await Wait(0.1);
        ride.Start(RideMode.Simulation, 0);
        await Wait(1.5);
        Check(sim.Model.TargetPowerW == 0, "Simulation après ERG : l'ancienne consigne est effacée");
        Check(!sim.Model.ErgMode && sim.Model.GradePct == 0 && Mathf.IsEqualApprox(sim.Model.Crr, 0.004)
              && Mathf.IsEqualApprox(sim.Model.Cw, 0.25), $"Simulation : plat, pneus et air envoyés (Crr {sim.Model.Crr}, Cw {sim.Model.Cw})");
        ride.Finish();
        await Wait(0.2);
        ride.QueueFree();
        (next as Node)?.Free();
        next = null;

        // Réglages : vitesses virtuelles 52/36 × 11-30 en 12 vitesses.
        var settings = Navigator.Create<SettingsScreen>(Navigator.Settings);
        AddChild(settings);
        await Wait(0.1);
        settings.Profile.VirtualGears = true;
        settings.Profile.Chainrings = [52, 36];
        settings.Profile.Cogs = [11, 12, 13, 14, 15, 16, 17, 19, 21, 24, 27, 30];
        Screen.Store.SaveProfile(settings.Profile);
        settings.QueueFree();

        ride = Navigator.Create<FreeRideScreen>(Navigator.FreeRide);
        AddChild(ride);
        await Wait(0.1);
        ride.Start(RideMode.Simulation, 0);
        await Wait(3.0);
        var gears = ride.Gears;
        Check(gears is { Chainring: 52, Cog: 17 } && sim.Model.ErgMode,
            $"Vitesses virtuelles : départ en {gears?.GearLabel}, résistance pilotée par le trainer");
        int before = sim.Model.TargetPowerW;
        double expected = gears?.RequiredPowerW(ride.Recorder!.Physics, sim.Model.RiderCadenceRpm) ?? 0;
        Check(Mathf.Abs(before - expected) < 25, $"Consigne selon la vitesse et la cadence ({before} W, attendu ≈ {expected:0} W)");
        ride.ShiftHarder();
        ride.ShiftHarder();
        await Wait(1.0);
        Check(sim.Model.TargetPowerW > before + 20, $"Vitesse plus dure : plus de résistance ({before} -> {sim.Model.TargetPowerW} W)");
        ride.ShiftFront();
        await Wait(1.0);
        Check(gears?.Chainring == 36 && sim.Model.TargetPowerW < before, $"Petit plateau : plus souple ({sim.Model.TargetPowerW} W)");
        ride.Finish();
        await Wait(0.2);
        Check(!sim.Model.ErgMode, "Fin de sortie avec vitesses : le trainer n'impose plus de puissance");
        ride.QueueFree();
        (next as Node)?.Free();
        next = null;

        var history = Navigator.Create<RideHistoryScreen>(Navigator.History);
        AddChild(history);
        await Wait(0.1);
        Check(history.RideCount == 3, $"Historique : les trois sorties sont listées ({history.RideCount})");
        Check(Screen.Store.List()[0].VirtualGears == "52/36 × 11-30 (12 v.)", "La sortie retient les vitesses utilisées");
        Check(Screen.Store.LoadProfile().LastMode == RideMode.Simulation, "Le dernier mode choisi est retenu");
        history.QueueFree();

        Navigator.Override = null;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    SignalAwaiter Wait(double s) => ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    void Check(bool ok, string label)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + label);
        if (!ok) _failures++;
    }
}
