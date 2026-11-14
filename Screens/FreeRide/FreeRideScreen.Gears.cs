using System;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Vitesses virtuelles dans <see cref="FreeRideScreen"/> (simulation) : l'appli calcule la puissance
/// que demandent la vitesse engagée et la cadence, et la donne au trainer en consigne ERG.</summary>
public partial class FreeRideScreen
{
    Control _gearControls = null!;
    Label _gearLabel = null!;
    Button _frontButton = null!;
    double _smoothCadence, _sinceGearCommand;
    int _gearTargetW = -1;

    void FindGearControls()
    {
        // Vitesses virtuelles : plateau, puis pignon plus souple / plus dur.
        _gearControls = GetNode<Control>("%GearControls");
        _frontButton = GetNode<Button>("%FrontButton");
        _frontButton.Pressed += ShiftFront;
        GetNode<Button>("%EasierButton").Pressed += ShiftEasier;
        GetNode<Button>("%HarderButton").Pressed += ShiftHarder;
        _gearLabel = GetNode<Label>("%GearLabel");
    }

    int GearTargetW() => (int)Math.Round(Gears!.RequiredPowerW(Recorder!.Physics, _smoothCadence));

    /// <summary>La consigne suit la cadence lissée : deux mises à jour par seconde au plus, et une
    /// toutes les 2 s même sans changement (au cas où une écriture se serait perdue).</summary>
    void UpdateGearResistance(double delta)
    {
        _smoothCadence += (Hub.Cadence - _smoothCadence) * Math.Clamp(delta, 0.0, 1.0);
        _sinceGearCommand += delta;
        if (!Hub.HasTrainer || _sinceGearCommand < 0.5) return;
        int target = GearTargetW();
        if (Math.Abs(target - _gearTargetW) < 3 && _sinceGearCommand < 2.0) return;
        _gearTargetW = target;
        _sinceGearCommand = 0;
        Hub.SetTargetPower(target);
    }

    internal void ShiftHarder() { if (Gears?.ShiftHarder() == true) GearChanged(); }
    internal void ShiftEasier() { if (Gears?.ShiftEasier() == true) GearChanged(); }

    internal void ShiftFront()
    {
        if (Gears is not { Chainrings.Length: > 1 }) return;
        Gears.ShiftFront();
        GearChanged();
    }

    /// <summary>Nouvelle vitesse : la consigne part tout de suite, sans attendre le prochain envoi.</summary>
    void GearChanged()
    {
        _sinceGearCommand = 2.0;
        Recorder?.LogEvent("gear", Gears!.GearLabel);
    }

    void RefreshGears()
    {
        _gearLabel.Text = $"{Gears!.GearLabel}\n{Gears.RearGearNumber}/{Gears.Cogs.Length}";
        _frontButton.Text = $"Plateau {Gears.Chainring}";
        _frontButton.Disabled = Gears.Chainrings.Length < 2;
    }
}
