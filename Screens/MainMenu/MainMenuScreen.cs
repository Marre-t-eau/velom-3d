using Godot;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>Menu principal, après la connexion des capteurs (mise en page : MainMenuScreen.tscn).</summary>
public partial class MainMenuScreen : Screen
{
    /// <summary>Boutons exposés aux tests.</summary>
    internal Button StructuredButton { get; private set; } = null!;
    internal Button FreeRideButton { get; private set; } = null!;
    Label _sensors = null!;

    public override void _Ready()
    {
        StructuredButton = GetNode<Button>("%StructuredButton");
        FreeRideButton = GetNode<Button>("%FreeRideButton");
        _sensors = GetNode<Label>("%Sensors");
        GetNode<Button>("%SettingsButton").Pressed += () => Navigator.GoTo(this, Navigator.Settings);
        Choice(StructuredButton, Navigator.Workouts);
        Choice(FreeRideButton, Navigator.FreeRide);
        Choice(GetNode<Button>("%HistoryButton"), Navigator.History);
        Choice(GetNode<Button>("%SensorsButton"), Navigator.Connection);
    }

    public override void _Process(double delta)
    {
        var hub = SensorHub.Instance;
        int connected = 0;
        foreach (var d in hub.Devices.Sorted) if (d.State == LinkState.Connected) connected++;
        _sensors.Text = connected == 0 ? "Aucun capteur connecté"
            : hub.TrainerAddress == "" ? $"{connected} capteur(s) connecté(s), pas de home trainer pilotable"
            : $"{connected} capteur(s) connecté(s), home trainer prêt";
    }

    protected override void OnBack() => GetTree().Quit();

    void Choice(Button button, string screen)
    {
        button.Pressed += () => Navigator.GoTo(this, screen);
        // Grisé : le texte aussi.
        var content = button.GetNode<Control>("Content");
        button.Draw += () => content.Modulate = button.Disabled ? new Color(1, 1, 1, 0.4f) : Colors.White;
    }
}
