using System;
using Godot;
using Velom3d.Rides;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Sortie libre sur le plat. Deux façons de faire résister le home trainer :
/// puissance imposée (ERG, réglable en roulant) ou simulation réaliste (pente nulle, pneus et
/// air : le trainer durcit avec la vitesse). La vitesse affichée vient du même modèle physique.
/// En simulation, les vitesses virtuelles (si elles sont activées dans les réglages) remplacent
/// les paramètres de simulation : l'appli calcule la puissance que demandent la vitesse engagée
/// et la cadence, et la donne au trainer en consigne, mise à jour en continu.
/// La sortie est enregistrée sur l'appareil, et toutes les 30 s au cas où l'appli serait coupée.
/// </summary>
public partial class FreeRideScreen : Screen
{
    const double AutosaveEveryS = 30.0;
    /// <summary>Boutons de réglage de la consigne ERG pendant la sortie.</summary>
    static readonly int[] PowerSteps = [10, 5];

    RiderProfile _profile = null!;
    RideMode _mode;
    int _target;

    Control _setup = null!, _riding = null!;
    Button _ergChoice = null!, _simChoice = null!;
    Label _settingsSummary = null!, _trainerHint = null!;

    Label _clock = null!, _modeLabel = null!, _power = null!, _powerTitle = null!, _cadence = null!, _hr = null!;
    Label _speed = null!, _distance = null!, _avgPower = null!, _rideTarget = null!, _trainerStatus = null!;
    Control _ergControls = null!, _gearControls = null!;
    Label _gearLabel = null!;
    Button _frontButton = null!;
    double _smoothCadence, _sinceGearCommand;
    int _gearTargetW = -1;
    Button _pauseButton = null!, _finishButton = null!;
    double _sinceAutosave;
    /// <summary>« Terminer » touché une fois : sortie en pause, en attente de confirmation.</summary>
    bool _finishArmed;

    /// <summary>Enregistrement en cours (null avant le départ), exposé aux tests.</summary>
    internal RideRecorder? Recorder { get; private set; }
    /// <summary>Vitesses virtuelles de la sortie en cours (null si désactivées ou en ERG).</summary>
    internal Drivetrain? Gears { get; private set; }

    SensorHub Hub => SensorHub.Instance;

    public override void _Ready()
    {
        _profile = Store.LoadProfile();
        _mode = _profile.LastMode;
        _target = Math.Clamp(_profile.LastTargetPowerW, 50, 1000);
        var root = Ui.BuildPage(this);
        _setup = BuildSetup(root);
        _riding = BuildRiding(root);
        _riding.Visible = false;
        RefreshSetup();
        Hub.StatusMessage += ShowTrainerStatus;
    }

    public override void _ExitTree()
    {
        Hub.StatusMessage -= ShowTrainerStatus;
        DisplayServer.ScreenSetKeepOn(false);
    }

    /// <summary>Refus du trainer (mode non pris en charge…) : visible pendant la sortie.</summary>
    void ShowTrainerStatus(string text)
    {
        _trainerStatus.Text = text;
        _trainerStatus.Visible = text.Length > 0;
    }

    // --- Réglages avant le départ ---

    Control BuildSetup(Container root)
    {
        var page = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 18);
        root.AddChild(page);
        page.AddChild(Ui.Label("Sortie libre", 40));
        page.AddChild(Ui.Label("Parcours plat. Choisissez comment le home trainer vous résiste.", 20, Ui.Muted));

        var content = Ui.AddScroll(page);
        var modes = Ui.Row();
        content.AddChild(modes);
        _ergChoice = ModeChoice(modes, "Puissance imposée",
            "Le trainer impose les watts, quelle que soit la cadence (mode ERG). Réglables de 5 ou 10 W en roulant.", RideMode.TargetPower);
        _simChoice = ModeChoice(modes, "Simulation réaliste",
            "La résistance suit la vitesse : roulement des pneus et frottement de l'air, sur le plat.", RideMode.Simulation);

        // Rappel en lecture seule : tout se règle dans Réglages, depuis le menu.
        _settingsSummary = Ui.Label("", 19, Ui.Muted);
        content.AddChild(_settingsSummary);

        _trainerHint = Ui.Label("", 20, Ui.Warning);
        content.AddChild(_trainerHint);

        var actions = Ui.Row();
        page.AddChild(actions);
        actions.AddChild(Ui.Button("Retour", OnBack));
        var start = Ui.Button("Démarrer", () => Start(_mode, _target));
        start.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(start);
        return page;
    }

    Button ModeChoice(Container parent, string title, string detail, RideMode mode)
    {
        var button = Ui.Button("", () => { _mode = mode; RefreshSetup(); }, 0, 150);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Ui.MakeSelectable(button);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        box.OffsetLeft = 20;
        box.OffsetRight = -20;
        var name = Ui.Label(title, 26);
        name.MouseFilter = MouseFilterEnum.Ignore;
        var hint = Ui.Label(detail, 17, Ui.Muted);
        hint.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(name);
        box.AddChild(hint);
        button.AddChild(box);
        parent.AddChild(button);
        return button;
    }

    void RefreshSetup()
    {
        _ergChoice.ButtonPressed = _mode == RideMode.TargetPower;
        _simChoice.ButtonPressed = _mode == RideMode.Simulation;
        var gears = _mode != RideMode.Simulation ? ""
            : _profile.VirtualGears ? $" · vitesses virtuelles {_profile.CreateDrivetrain().Describe()}"
            : " · vitesses virtuelles désactivées";
        _settingsSummary.Text = $"FTP {_profile.FtpW} W · {_profile.RiderMassKg:0} kg{gears}. À modifier dans Réglages, depuis le menu.";
        _trainerHint.Text = Hub.TrainerAddress == ""
            ? "Aucun home trainer pilotable connecté : la sortie enregistrera vos capteurs sans régler la résistance."
            : "";
    }

    // --- Pendant la sortie ---

    Control BuildRiding(Container root)
    {
        var page = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 16);
        root.AddChild(page);

        var header = Ui.Row();
        page.AddChild(header);
        _clock = Ui.Label("0:00", 52);
        _clock.AutowrapMode = TextServer.AutowrapMode.Off;
        header.AddChild(_clock);
        _modeLabel = Ui.Label("", 20, Ui.Muted);
        _modeLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _modeLabel.VerticalAlignment = VerticalAlignment.Center;
        _modeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        header.AddChild(_modeLabel);

        var content = Ui.AddScroll(page, 14);
        var row1 = Ui.Row(14);
        content.AddChild(row1);
        _power = Ui.Tile(row1, "Puissance", Ui.PowerColor, 52);
        _powerTitle = (Label)_power.GetParent().GetChild(0);
        _cadence = Ui.Tile(row1, "Cadence", Ui.CadenceColor, 52);
        _hr = Ui.Tile(row1, "Fréquence cardiaque", Ui.HeartRateColor, 52);
        var row2 = Ui.Row(14);
        content.AddChild(row2);
        _speed = Ui.Tile(row2, "Vitesse", Ui.SpeedColor);
        _distance = Ui.Tile(row2, "Distance", Ui.Muted);
        _avgPower = Ui.Tile(row2, "Puissance moyenne", Ui.PowerColor);

        var erg = Ui.Row();
        erg.Alignment = BoxContainer.AlignmentMode.Center;
        content.AddChild(erg);
        foreach (var step in PowerSteps) erg.AddChild(Ui.Button($"−{step} W", () => ChangeTarget(-step), 150, 96, 28));
        _rideTarget = Ui.Label("", 34);
        _rideTarget.CustomMinimumSize = new Vector2(260, 0);
        _rideTarget.HorizontalAlignment = HorizontalAlignment.Center;
        _rideTarget.VerticalAlignment = VerticalAlignment.Center;
        erg.AddChild(_rideTarget);
        for (int i = PowerSteps.Length - 1; i >= 0; i--)
        {
            int step = PowerSteps[i];
            erg.AddChild(Ui.Button($"+{step} W", () => ChangeTarget(+step), 150, 96, 28));
        }
        _ergControls = erg;

        // Vitesses virtuelles : plateau, puis pignon plus souple / plus dur.
        var gears = Ui.Row();
        gears.Alignment = BoxContainer.AlignmentMode.Center;
        content.AddChild(gears);
        _frontButton = Ui.Button("Plateau", ShiftFront, 190, 96, 24);
        gears.AddChild(_frontButton);
        gears.AddChild(Ui.Button("− Plus souple", ShiftEasier, 230, 96, 26));
        _gearLabel = Ui.Label("", 30);
        _gearLabel.CustomMinimumSize = new Vector2(220, 0);
        _gearLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _gearLabel.VerticalAlignment = VerticalAlignment.Center;
        gears.AddChild(_gearLabel);
        gears.AddChild(Ui.Button("Plus dur +", ShiftHarder, 230, 96, 26));
        _gearControls = gears;

        _trainerStatus = Ui.Label("", 18, Ui.Warning);
        _trainerStatus.Visible = false;
        content.AddChild(_trainerStatus);

        var actions = Ui.Row();
        page.AddChild(actions);
        _pauseButton = Ui.Button("Pause", TogglePause);
        _pauseButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_pauseButton);
        _finishButton = Ui.Button("Terminer", OnFinishPressed);
        _finishButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_finishButton);
        return page;
    }

    internal void Start(RideMode mode, int targetW)
    {
        _mode = mode;
        _target = targetW;
        _profile.LastMode = mode;
        _profile.LastTargetPowerW = targetW;
        Store.SaveProfile(_profile);

        Recorder = new RideRecorder(mode, _profile, DateTimeOffset.Now, targetW);
        Gears = mode == RideMode.Simulation && _profile.VirtualGears ? _profile.CreateDrivetrain() : null;
        _smoothCadence = Hub.Cadence;
        if (Hub.TrainerAddress != "")
        {
            if (mode == RideMode.TargetPower) Hub.EnterErg(targetW);
            else if (Gears is not null) Hub.EnterErg(GearTargetW());
            else ApplySimulation();
            _gearTargetW = Gears is null ? -1 : GearTargetW();
        }
        _modeLabel.Text = mode == RideMode.TargetPower ? "Puissance imposée · plat"
            : Gears is not null ? $"Simulation réaliste · plat · {Gears.Describe()}"
            : "Simulation réaliste · plat";
        _ergControls.Visible = mode == RideMode.TargetPower;
        _gearControls.Visible = Gears is not null;
        _setup.Visible = false;
        _riding.Visible = true;
        DisplayServer.ScreenSetKeepOn(true);
    }

    /// <summary>Pente nulle ; le trainer applique les mêmes pneus et la même traînée que la vitesse affichée.
    /// L'ancienne consigne ERG éventuelle est effacée d'abord.</summary>
    void ApplySimulation()
    {
        var physics = Recorder!.Physics;
        Hub.EnterSimulation(physics.GradePct, physics.WindMps, physics.Crr, physics.WindResistanceCoefficient);
    }

    int GearTargetW() => (int)Math.Round(Gears!.RequiredPowerW(Recorder!.Physics, _smoothCadence));

    /// <summary>La consigne suit la cadence lissée : deux mises à jour par seconde au plus, et une
    /// toutes les 2 s même sans changement (au cas où une écriture se serait perdue).</summary>
    void UpdateGearResistance(double delta)
    {
        _smoothCadence += (Hub.Cadence - _smoothCadence) * Math.Clamp(delta, 0.0, 1.0);
        _sinceGearCommand += delta;
        if (Hub.TrainerAddress == "" || _sinceGearCommand < 0.5) return;
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
    void GearChanged() => _sinceGearCommand = 2.0;

    void ChangeTarget(int delta)
    {
        if (Recorder is null) return;
        Recorder.TargetPowerW = Math.Clamp(Recorder.TargetPowerW + delta, 50, 1000);
        _profile.LastTargetPowerW = Recorder.TargetPowerW;
        if (Hub.TrainerAddress != "") Hub.SetTargetPower(Recorder.TargetPowerW);
    }

    /// <summary>Pause / Reprendre. Reprendre annule aussi une fin demandée.</summary>
    void TogglePause()
    {
        if (Recorder is null) return;
        if (Recorder.IsPaused) Recorder.Resume(); else Recorder.Pause();
        _finishArmed = false;
    }

    /// <summary>Premier appui : la sortie se met en pause et attend confirmation (Reprendre pour
    /// continuer). Second appui : fin. Un doigt qui glisse ne coupe donc pas une sortie.</summary>
    internal void OnFinishPressed()
    {
        if (Recorder is null) return;
        if (_finishArmed)
        {
            Finish();
            return;
        }
        _finishArmed = true;
        Recorder.Pause();
    }

    /// <summary>État affiché des boutons (pour les tests).</summary>
    internal string PauseButtonText => _pauseButton.Text;

    internal void Finish()
    {
        if (Recorder is null) return;
        var ride = Recorder.Finish();
        Recorder = null;
        Gears = null;
        // On rend la main au cycliste : plus de consigne ERG après la sortie.
        if (Hub.TrainerAddress != "") Hub.EnterSimulation(0.0);
        DisplayServer.ScreenSetKeepOn(false);
        if (ride.Samples.Count == 0)
        {
            Navigator.GoTo(this, Navigator.Menu);
            return;
        }
        Store.Save(ride);
        Store.SaveProfile(_profile);
        var summary = Navigator.Create<RideSummaryScreen>(Navigator.Summary);
        summary.Ride = ride;
        summary.JustFinished = true;
        Navigator.GoTo(this, summary);
    }

    public override void _Process(double delta)
    {
        if (Recorder is null) return;
        Recorder.Tick(delta, Hub.Power, Hub.Cadence, Hub.HeartRate);
        if (Gears is not null) UpdateGearResistance(delta);

        _sinceAutosave += delta;
        if (_sinceAutosave >= AutosaveEveryS) Autosave();

        _clock.Text = Ui.Duration(Recorder.ElapsedS);
        _power.Text = $"{Hub.Power} W";
        _powerTitle.Text = Recorder.Ride.Info.Mode == RideMode.TargetPower ? $"Puissance · cible {Recorder.TargetPowerW} W"
            : Gears is not null && _gearTargetW >= 0 ? $"Puissance · demandée {_gearTargetW} W" : "Puissance";
        if (Gears is not null)
        {
            _gearLabel.Text = $"{Gears.GearLabel}\n{Gears.RearGearNumber}/{Gears.Cogs.Length}";
            _frontButton.Text = $"Plateau {Gears.Chainring}";
            _frontButton.Disabled = Gears.Chainrings.Length < 2;
        }
        _cadence.Text = $"{Hub.Cadence:0} tr/min";
        _hr.Text = Hub.HeartRate > 0 ? $"{Hub.HeartRate} bpm" : "--";
        _speed.Text = $"{Recorder.SpeedKmh:0.0} km/h";
        _distance.Text = Ui.Km(Recorder.DistanceM);
        _avgPower.Text = Recorder.ElapsedS > 0 ? $"{Recorder.AveragePowerW:0} W" : "--";
        _rideTarget.Text = $"Cible {Recorder.TargetPowerW} W";
        _finishButton.Text = _finishArmed ? "Confirmer la fin" : "Terminer";
        _pauseButton.Text = Recorder.IsPaused ? "Reprendre" : "Pause";
    }

    void Autosave()
    {
        _sinceAutosave = 0;
        if (Recorder is not { Ride.Samples.Count: > 0 }) return;
        Recorder.Ride.Summarize();
        Store.Save(Recorder.Ride);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        // L'appli passe en arrière-plan : Android peut la tuer, on enregistre.
        if (what == NotificationApplicationPaused) Autosave();
    }

    /// <summary>Au clavier (ordinateur, mode debug) : flèches haut/bas pour changer de pignon.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (Gears is not null && e.IsActionPressed("ui_up")) { ShiftHarder(); GetViewport().SetInputAsHandled(); return; }
        if (Gears is not null && e.IsActionPressed("ui_down")) { ShiftEasier(); GetViewport().SetInputAsHandled(); return; }
        base._UnhandledInput(e);
    }

    protected override void OnBack()
    {
        if (Recorder is null) Navigator.GoTo(this, Navigator.Menu);
        else if (!Recorder.IsPaused) TogglePause(); // pendant la sortie, Retour met en pause sans rien perdre
    }
}
