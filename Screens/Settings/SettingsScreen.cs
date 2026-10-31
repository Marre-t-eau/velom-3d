using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>
/// Réglages du cycliste et du vélo : FTP, poids, roue, et vitesses virtuelles (plateaux et
/// cassette, choisis parmi les transmissions courantes ou saisis à la main).
/// Chaque changement est enregistré tout de suite. Mise en page : SettingsScreen.tscn.
/// </summary>
public partial class SettingsScreen : Screen
{
    RiderProfile _profile = null!;
    Label _ftp = null!, _riderMass = null!, _bikeMass = null!, _gearSummary = null!;
    CheckButton _gearsToggle = null!;
    Control _gearOptions = null!;
    readonly List<(Button Chip, int Mm)> _wheelChips = new();
    readonly List<(Button Chip, int[] Teeth)> _chainringChips = new();
    readonly List<(Button Chip, int[] Teeth)> _cassetteChips = new();

    /// <summary>Profil en cours de modification (pour les tests).</summary>
    internal RiderProfile Profile => _profile;

    public override void _Ready()
    {
        _profile = Store.LoadProfile();
        GetNode<Button>("%BackButton").Pressed += OnBack;

        _ftp = Stepper("%FtpStepper", d => _profile.FtpW = Math.Clamp(_profile.FtpW + d * 5, 50, 600));
        _riderMass = Stepper("%RiderMassStepper", d => _profile.RiderMassKg = Math.Clamp(_profile.RiderMassKg + d, 30, 200));
        _bikeMass = Stepper("%BikeMassStepper", d => _profile.BikeMassKg = Math.Clamp(_profile.BikeMassKg + d * 0.5, 4, 30));

        // Les choix viennent des listes de Gearing : leurs boutons sont créés ici.
        var wheels = GetNode<Container>("%Wheels");
        foreach (var (label, mm) in Gearing.Wheels)
            _wheelChips.Add((Ui.Chip(wheels, $"{label}  {mm} mm", () => { _profile.WheelCircumferenceMm = mm; Changed(); }), mm));

        _gearsToggle = GetNode<CheckButton>("%GearsToggle");
        _gearsToggle.Toggled += on => { _profile.VirtualGears = on; Changed(); };
        _gearOptions = GetNode<Control>("%GearOptions");

        var rings = GetNode<Container>("%Chainrings");
        foreach (var p in Gearing.Chainrings)
            _chainringChips.Add((Ui.Chip(rings, p.Label, () => SetChainrings(p.Teeth)), p.Teeth));
        Custom("%ChainringsEdit", "%ChainringsError", Gearing.ParseChainrings, SetChainrings,
            "1 à 3 plateaux de 8 à 60 dents, séparés par « / ».");

        var cassettes = GetNode<Container>("%Cassettes");
        foreach (var (speeds, list) in Gearing.Cassettes)
        {
            var row = Ui.Flow(cassettes);
            var label = Ui.Label($"{speeds} v.", 20, Ui.Muted);
            label.CustomMinimumSize = new Vector2(70, 64);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(label);
            foreach (var c in list)
                _cassetteChips.Add((Ui.Chip(row, c.Label, () => SetCassette(c.Teeth)), c.Teeth));
        }
        Custom("%CassetteEdit", "%CassetteError", Gearing.ParseCassette, SetCassette,
            "5 à 14 pignons de 8 à 60 dents, séparés par des virgules.");

        _gearSummary = GetNode<Label>("%GearSummary");
        Refresh();
    }

    /// <summary>Branche les boutons − / + d'une ligne de réglage ; renvoie le label de la valeur.</summary>
    Label Stepper(string node, Action<int> change)
    {
        var row = GetNode<Control>(node + "/Row");
        row.GetNode<Button>("Minus").Pressed += () => { change(-1); Changed(); };
        row.GetNode<Button>("Plus").Pressed += () => { change(+1); Changed(); };
        return row.GetNode<Label>("Value");
    }

    /// <summary>Saisie libre ; appliquée dès qu'elle est valable.</summary>
    void Custom(string editNode, string errorNode, Func<string, int[]?> parse, Action<int[]> apply, string rule)
    {
        var edit = GetNode<LineEdit>(editNode);
        var err = GetNode<Label>(errorNode);
        edit.TextChanged += text =>
        {
            var teeth = parse(text);
            err.Text = text.Trim().Length == 0 || teeth is not null ? "" : rule;
            err.Visible = err.Text.Length > 0;
            if (teeth is not null) apply(teeth);
        };
    }

    void SetChainrings(int[] teeth)
    {
        _profile.Chainrings = teeth;
        Changed();
    }

    void SetCassette(int[] teeth)
    {
        _profile.Cogs = teeth;
        Changed();
    }

    void Changed()
    {
        Store.SaveProfile(_profile);
        Refresh();
    }

    void Refresh()
    {
        _ftp.Text = $"{_profile.FtpW} W";
        _riderMass.Text = $"{_profile.RiderMassKg:0} kg";
        _bikeMass.Text = $"{_profile.BikeMassKg:0.0} kg";
        foreach (var (chip, mm) in _wheelChips) chip.SetPressedNoSignal(mm == _profile.WheelCircumferenceMm);
        _gearsToggle.SetPressedNoSignal(_profile.VirtualGears);
        _gearOptions.Modulate = _profile.VirtualGears ? Colors.White : new Color(1, 1, 1, 0.45f);
        foreach (var (chip, teeth) in _chainringChips) chip.SetPressedNoSignal(Same(teeth, _profile.Chainrings));
        foreach (var (chip, teeth) in _cassetteChips) chip.SetPressedNoSignal(Same(teeth, _profile.Cogs));

        var d = _profile.CreateDrivetrain();
        double easiest = (double)d.Chainrings[^1] / d.Cogs[^1] * d.WheelCircumferenceM;
        double hardest = (double)d.Chainrings[0] / d.Cogs[0] * d.WheelCircumferenceM;
        _gearSummary.Text = $"{d.Describe()} : de {easiest:0.0} à {hardest:0.0} m par tour de pédale, "
                            + $"soit {easiest * 90 / 60 * 3.6:0} à {hardest * 90 / 60 * 3.6:0} km/h à 90 tr/min.";
    }

    static bool Same(int[] a, int[] b) => a.OrderBy(t => t).SequenceEqual(b.OrderBy(t => t));

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Menu);
}
