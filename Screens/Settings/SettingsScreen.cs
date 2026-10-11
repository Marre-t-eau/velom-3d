using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>
/// Réglages du cycliste et du vélo : FTP, poids, roue, et vitesses virtuelles (plateaux et
/// cassette, choisis parmi les transmissions courantes ou saisis à la main).
/// Chaque changement est enregistré tout de suite.
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
        var root = Ui.BuildPage(this);
        var header = Ui.Row();
        root.AddChild(header);
        var title = Ui.Label("Réglages", 40);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(Ui.Button("Retour", OnBack, 200, 72));

        var content = Ui.AddScroll(root, 14);

        Section(content, "Cycliste");
        Ui.Stepper(content, "FTP", out _ftp, d => _profile.FtpW = Math.Clamp(_profile.FtpW + d * 5, 50, 600), Changed);
        Ui.Stepper(content, "Poids du cycliste", out _riderMass, d => _profile.RiderMassKg = Math.Clamp(_profile.RiderMassKg + d, 30, 200), Changed);

        Section(content, "Vélo");
        Ui.Stepper(content, "Poids du vélo", out _bikeMass, d => _profile.BikeMassKg = Math.Clamp(_profile.BikeMassKg + d * 0.5, 4, 30), Changed);
        content.AddChild(Ui.Label("Pneus (circonférence de roue)", 20, Ui.Muted));
        var wheels = Ui.Flow(content);
        foreach (var (label, mm) in Gearing.Wheels)
            _wheelChips.Add((Ui.Chip(wheels, $"{label}  {mm} mm", () => { _profile.WheelCircumferenceMm = mm; Changed(); }), mm));

        Section(content, "Vitesses virtuelles");
        _gearsToggle = new CheckButton { Text = "Simuler plateaux et cassette en mode simulation", MouseFilter = MouseFilterEnum.Pass };
        _gearsToggle.AddThemeFontSizeOverride("font_size", 24);
        _gearsToggle.Toggled += on => { _profile.VirtualGears = on; Changed(); };
        content.AddChild(_gearsToggle);
        content.AddChild(Ui.Label("Vous changez de plateau et de pignon dans l'appli, comme sur la route : la résistance du "
            + "trainer suit la vitesse engagée et votre cadence. Laissez la chaîne du vélo sur un pignon du milieu.", 18, Ui.Muted));

        var options = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        options.AddThemeConstantOverride("separation", 12);
        content.AddChild(options);
        _gearOptions = options;

        options.AddChild(Ui.Label("Plateaux", 22));
        var rings = Ui.Flow(options);
        foreach (var p in Gearing.Chainrings)
            _chainringChips.Add((Ui.Chip(rings, p.Label, () => SetChainrings(p.Teeth)), p.Teeth));
        Custom(options, "Autres plateaux, ex. 53/42", Gearing.ParseChainrings, SetChainrings,
            "1 à 3 plateaux de 8 à 60 dents, séparés par « / ».");

        options.AddChild(Ui.Label("Cassette", 22));
        foreach (var (speeds, cassettes) in Gearing.Cassettes)
        {
            var row = Ui.Flow(options);
            var label = Ui.Label($"{speeds} v.", 20, Ui.Muted);
            label.CustomMinimumSize = new Vector2(70, 64);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(label);
            foreach (var c in cassettes)
                _cassetteChips.Add((Ui.Chip(row, c.Label, () => SetCassette(c.Teeth)), c.Teeth));
        }
        Custom(options, "Autre cassette, ex. 11,12,13,14,16,18,20,22,25,28", Gearing.ParseCassette, SetCassette,
            "5 à 14 pignons de 8 à 60 dents, séparés par des virgules.");

        _gearSummary = Ui.Label("", 20, Ui.Accent);
        options.AddChild(_gearSummary);

        Refresh();
    }

    static void Section(Container parent, string title)
    {
        var label = Ui.Label(title, 28);
        label.CustomMinimumSize = new Vector2(0, 52);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        parent.AddChild(label);
    }

    /// <summary>Saisie libre ; appliquée dès qu'elle est valable.</summary>
    static void Custom(Container parent, string placeholder, Func<string, int[]?> parse, Action<int[]> apply, string rule)
    {
        var edit = new LineEdit { PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 64), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.AddThemeFontSizeOverride("font_size", 22);
        parent.AddChild(edit);
        var err = Ui.Label("", 17, Ui.Warning);
        err.Visible = false;
        parent.AddChild(err);
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
