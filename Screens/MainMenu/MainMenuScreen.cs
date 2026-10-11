using Godot;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>Menu principal, après la connexion des capteurs.</summary>
public partial class MainMenuScreen : Screen
{
    /// <summary>Bouton de l'entraînement structuré (grisé pour l'instant), exposé aux tests.</summary>
    internal Button StructuredButton { get; private set; } = null!;
    internal Button FreeRideButton { get; private set; } = null!;
    Label _sensors = null!;

    public override void _Ready()
    {
        var root = Ui.BuildPage(this, 22);
        root.AddChild(Ui.Label("Velom3d", 44));
        _sensors = Ui.Label("", 20, Ui.Muted);
        root.AddChild(_sensors);

        // Grille 2 × 2 : les quatre choix tiennent sur un téléphone à l'horizontale.
        var content = new GridContainer { Columns = 2, SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("h_separation", 18);
        content.AddThemeConstantOverride("v_separation", 18);
        root.AddChild(content);
        StructuredButton = Choice(content, "Entraînement structuré", "Bientôt : séances par intervalles en mode ERG.", () => { });
        StructuredButton.Disabled = true;
        FreeRideButton = Choice(content, "Sortie libre", "Roulez sur le plat, à puissance imposée ou en simulation réaliste.",
            () => Navigator.GoTo(this, Navigator.FreeRide));
        Choice(content, "Mes sorties", "Historique, graphiques, TSS, IF et calories de chaque sortie.",
            () => Navigator.GoTo(this, Navigator.History));
        Choice(content, "Capteurs", "Connecter ou changer de home trainer, capteur de puissance, ceinture cardio.",
            () => Navigator.GoTo(this, Navigator.Connection));
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

    static Button Choice(Container parent, string title, string detail, System.Action onPressed)
    {
        var button = Ui.Button("", onPressed, 0, 150);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.SizeFlagsVertical = SizeFlags.ExpandFill;
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        box.OffsetLeft = 28;
        box.OffsetRight = -28;
        box.Alignment = BoxContainer.AlignmentMode.Center;
        var name = Ui.Label(title, 30);
        name.MouseFilter = MouseFilterEnum.Ignore;
        var hint = Ui.Label(detail, 18, Ui.Muted);
        hint.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(name);
        box.AddChild(hint);
        button.AddChild(box);
        // Grisé : le texte aussi.
        button.Draw += () => box.Modulate = button.Disabled ? new Color(1, 1, 1, 0.4f) : Colors.White;
        parent.AddChild(button);
        return button;
    }
}
