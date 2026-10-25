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
/// Avec un <see cref="Workout"/>, le même écran déroule un entraînement structuré : la consigne
/// suit les blocs (ERG fixe ou en rampe, simulation sur le plat pour les blocs libres), et la liste
/// des blocs à venir s'affiche avec leur durée, leur puissance et leur cadence.
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

    /// <summary>Entraînement structuré à dérouler, à renseigner avant l'ouverture (null = sortie libre).</summary>
    public Workout? Workout { get; set; }
    /// <summary>Avancement de l'entraînement en cours (pour les tests).</summary>
    internal WorkoutProgress? Progress { get; private set; }
    Control _workoutPanel = null!;
    WorkoutChart _workoutChart = null!;
    Label _blockTitle = null!, _blockRemaining = null!, _blockPower = null!, _blockCadence = null!, _cadenceTitle = null!;
    ProgressBar _blockBar = null!;
    VBoxContainer _upcoming = null!;
    readonly System.Collections.Generic.List<(Label Label, int StartS)> _upcomingStarts = new();
    Button _skipButton = null!;
    /// <summary>Test rampe : temps passé sous 40 tr/min (le cycliste ne tient plus le palier).</summary>
    double _rampStallS;
    const double RampStallLimitS = 5.0;
    int _shownBlock = -1;
    /// <summary>Le trainer est-il en ERG ? null au départ (rien envoyé encore).</summary>
    bool? _workoutErg;
    int _sentTargetW = -1, _sentBlock = -1;
    /// <summary>Blocs libres : ERG choisi par le cycliste (watts réglables), ou vraiment libre.</summary>
    internal bool FreeErg { get; private set; }
    internal int FreeErgW { get; private set; }
    Control _freeErgRow = null!;
    Button _freeErgToggle = null!;
    double _sinceTargetSent;

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
        if (Workout is not null) StartWorkout(Workout);
    }

    public override void _ExitTree()
    {
        Hub.StatusMessage -= ShowTrainerStatus;
        StopDataLog();
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
        _cadenceTitle = (Label)_cadence.GetParent().GetChild(0);
        _hr = Ui.Tile(row1, "Fréquence cardiaque", Ui.HeartRateColor, 52);
        _workoutPanel = BuildWorkoutPanel(content);
        _workoutPanel.Visible = false;
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
        _skipButton = Ui.Button("Bloc suivant", SkipBlock);
        _skipButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _skipButton.Visible = false;
        actions.AddChild(_skipButton);
        _finishButton = Ui.Button("Terminer", OnFinishPressed);
        _finishButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_finishButton);
        return page;
    }

    // --- Entraînement structuré ---

    /// <summary>Bloc en cours (temps restant, consigne, profil) à gauche, blocs à venir à droite.</summary>
    Control BuildWorkoutPanel(Container content)
    {
        var page = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        page.AddThemeConstantOverride("separation", 14);
        content.AddChild(page);
        var row = Ui.Row(14);
        page.AddChild(row);

        var current = Ui.Panel(Ui.Card, Ui.Accent);
        current.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        current.SizeFlagsStretchRatio = 1.0f;
        row.AddChild(current);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        current.AddChild(box);
        _blockTitle = Ui.Label("", 20, Ui.Accent);
        box.AddChild(_blockTitle);
        // Temps restant à gauche ; consigne de puissance et de cadence à droite.
        var line = Ui.Row(20);
        box.AddChild(line);
        _blockRemaining = Ui.Label("", 52);
        _blockRemaining.AutowrapMode = TextServer.AutowrapMode.Off;
        _blockRemaining.VerticalAlignment = VerticalAlignment.Center;
        line.AddChild(_blockRemaining);
        var targets = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass, SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        line.AddChild(targets);
        _blockPower = Ui.Label("", 26, Ui.PowerColor);
        targets.AddChild(_blockPower);
        _blockCadence = Ui.Label("", 22, Ui.CadenceColor);
        targets.AddChild(_blockCadence);
        _blockBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 14),
            MouseFilter = MouseFilterEnum.Pass,
        };
        var fill = new StyleBoxFlat { BgColor = Ui.Accent };
        fill.SetCornerRadiusAll(6);
        var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.08f) };
        back.SetCornerRadiusAll(6);
        _blockBar.AddThemeStyleboxOverride("fill", fill);
        _blockBar.AddThemeStyleboxOverride("background", back);
        box.AddChild(_blockBar);
        // Bloc libre : le cycliste peut quand même faire imposer des watts par le trainer.
        var freeErg = Ui.Row(10);
        box.AddChild(freeErg);
        _freeErgToggle = Ui.Button("", ToggleFreeErg, 190, 64, 20);
        Ui.MakeSelectable(_freeErgToggle);
        _freeErgToggle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        freeErg.AddChild(_freeErgToggle);
        foreach (var step in new[] { -10, -5, 5, 10 })
            freeErg.AddChild(Ui.Button(step < 0 ? $"−{-step}" : $"+{step}", () => ChangeFreeErg(step), 72, 64, 22));
        _freeErgRow = freeErg;
        // Profil de l'entraînement sous le bloc en cours : où on en est, ce qui reste.
        _workoutChart = new WorkoutChart { CustomMinimumSize = new Vector2(0, 70), SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddChild(_workoutChart);

        var next = Ui.Panel(Ui.Card);
        next.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        next.SizeFlagsStretchRatio = 1.3f;
        row.AddChild(next);
        var nextBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        nextBox.AddThemeConstantOverride("separation", 6);
        next.AddChild(nextBox);
        nextBox.AddChild(Ui.Label("À venir", 20, Ui.Muted));
        _upcoming = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _upcoming.AddThemeConstantOverride("separation", 4);
        nextBox.AddChild(_upcoming);
        return page;
    }

    internal void StartWorkout(Workout workout)
    {
        Workout = workout;
        _target = 0;
        Progress = new WorkoutProgress(workout, _profile.FtpW);
        Recorder = new RideRecorder(RideMode.Workout, _profile, DateTimeOffset.Now, Progress.TargetW ?? 0, workout.Name);
        StartDataLog();
        Gears = null;
        _workoutChart.Workout = workout;
        _workoutChart.FtpW = _profile.FtpW;
        UpdateWorkoutControl(0);
        _modeLabel.Text = $"{workout.Name} · FTP {_profile.FtpW} W";
        _ergControls.Visible = false;
        _gearControls.Visible = false;
        _workoutPanel.Visible = true;
        _skipButton.Visible = true;
        _setup.Visible = false;
        _riding.Visible = true;
        DisplayServer.ScreenSetKeepOn(true);
    }

    /// <summary>ERG oui / non pendant les blocs libres ; démarre à la puissance du moment, arrondie à 5 W.</summary>
    internal void ToggleFreeErg()
    {
        FreeErg = !FreeErg;
        if (FreeErg && FreeErgW == 0) FreeErgW = Math.Clamp((int)Math.Round(Math.Max(Hub.Power, 100) / 5.0) * 5, 50, 1000);
        _sinceTargetSent = 1.0;
    }

    internal void ChangeFreeErg(int delta)
    {
        if (!FreeErg) ToggleFreeErg();
        FreeErgW = Math.Clamp(FreeErgW + delta, 50, 1000);
        _sinceTargetSent = 1.0; // la nouvelle consigne part tout de suite
    }

    internal void SkipBlock()
    {
        if (Progress is null || Recorder is null) return;
        // Pendant les paliers du test rampe, le bouton arrête le test (direction retour au calme).
        if (Progress.Current is { RampTest: true }) Progress.EndRampTest();
        else Progress.SkipBlock();
        UpdateWorkoutControl(0);
    }

    /// <summary>Consignes du moment vers l'enregistrement et le trainer : ERG quand le bloc impose
    /// une puissance (une mise à jour par seconde au plus dans une rampe, tout de suite à chaque
    /// nouveau bloc), simulation sur le plat quand elle est libre ou l'entraînement fini.</summary>
    void UpdateWorkoutControl(double delta)
    {
        var progress = Progress!;
        // Pas de consigne (bloc libre, séance finie) : celle du cycliste s'il a choisi l'ERG.
        int? target = progress.TargetW ?? (FreeErg ? FreeErgW : null);
        int block = progress.BlockIndex;
        Recorder!.TargetPowerW = target ?? 0;
        Recorder.TargetCadenceRpm = progress.TargetCadenceRpm;
        _sinceTargetSent += delta;
        if (Hub.TrainerAddress == "") return;
        if (target is not { } watts)
        {
            if (_workoutErg != false) ApplySimulation();
            _workoutErg = false;
        }
        else if (_workoutErg != true)
        {
            Hub.EnterErg(watts);
            _workoutErg = true;
        }
        else if (watts != _sentTargetW && (block != _sentBlock || _sinceTargetSent >= 1.0))
            Hub.SetTargetPower(watts);
        else
            return;
        _sentTargetW = target ?? -1;
        _sentBlock = block;
        _sinceTargetSent = 0;
    }

    /// <summary>« 220 W », « 150 à 250 W », « puissance libre », en watts avec la FTP des Réglages.</summary>
    string BlockPowerText(WorkoutBlock b)
    {
        var w = Workout!;
        if (b.Kind == BlockPower.Free) return "puissance libre";
        int p0 = w.ToWatts(b.Power, _profile.FtpW), p1 = w.ToWatts(b.PowerEnd, _profile.FtpW);
        return b.Kind == BlockPower.Ramp ? $"{p0} à {p1} W" : $"{p0} W";
    }

    static string CadenceText(WorkoutBlock b) => b.FreeCadence ? "cadence libre" : $"{b.CadenceRpm} tr/min";

    /// <summary>Blocs suivants seulement : les blocs passés et le bloc en cours n'y sont pas.</summary>
    void RebuildUpcoming(int current)
    {
        var w = Workout!;
        foreach (var child in _upcoming.GetChildren()) child.QueueFree();
        _upcomingStarts.Clear();
        int rampRows = 0;
        for (int i = current + 1; i < w.Blocks.Count; i++)
        {
            var b = w.Blocks[i];
            // Test rampe : les 4 prochains paliers suffisent, la suite est « jusqu'à ne plus tenir ».
            if (b.RampTest && ++rampRows > 4)
            {
                if (rampRows == 5) _upcoming.AddChild(Ui.Label("… +6 % de FTP chaque minute, jusqu'à ne plus tenir", 18, Ui.Muted));
                continue;
            }
            var row = Ui.Row(12);
            _upcoming.AddChild(row);
            var duration = Ui.Label(Ui.Duration(b.DurationS), 20);
            duration.CustomMinimumSize = new Vector2(80, 0);
            duration.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(duration);
            var power = Ui.Label(BlockPowerText(b), 20, Ui.PowerColor);
            power.AutowrapMode = TextServer.AutowrapMode.Off;
            power.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(power);
            var cadence = Ui.Label(CadenceText(b), 20, Ui.CadenceColor);
            cadence.CustomMinimumSize = new Vector2(150, 0);
            cadence.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(cadence);
            var start = Ui.Label("", 18, Ui.Muted);
            start.CustomMinimumSize = new Vector2(110, 0);
            start.HorizontalAlignment = HorizontalAlignment.Right;
            start.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(start);
            _upcomingStarts.Add((start, w.StartOf(i)));
        }
        if (current + 1 >= w.Blocks.Count)
            _upcoming.AddChild(Ui.Label(current >= w.Blocks.Count ? "Rien : entraînement terminé." : "Rien : c'est le dernier bloc.", 20, Ui.Muted));
    }

    /// <summary>Nombre de blocs listés « à venir » (pour les tests).</summary>
    internal int UpcomingCount => _upcomingStarts.Count;

    void RefreshWorkout()
    {
        var progress = Progress!;
        var w = progress.Workout;
        int index = progress.BlockIndex;
        if (index != _shownBlock)
        {
            _shownBlock = index;
            RebuildUpcoming(index);
        }
        foreach (var (label, start) in _upcomingStarts) label.Text = "dans " + Ui.Duration(Math.Ceiling(start - progress.PositionS));
        _workoutChart.PositionS = progress.PositionS;
        _workoutChart.QueueRedraw();

        if (progress.Current is { } b)
        {
            _blockTitle.Text = b.FtpTest ? "Test FTP : à fond, régulier · reste"
                : b.RampTest ? $"Test rampe · palier {index - w.Blocks.FindIndex(x => x.RampTest) + 1} · reste"
                : $"Bloc {index + 1} / {w.Blocks.Count} · reste";
            _blockRemaining.Text = Ui.Duration(Math.Ceiling(progress.BlockRemainingS));
            _blockPower.Text = b.FtpTest ? $"Moyenne {progress.FtpTestAveragePowerW:0} W"
                : b.Kind == BlockPower.Ramp && progress.TargetW is { } now ? $"{now} W, vers {w.ToWatts(b.PowerEnd, progress.FtpW)} W"
                : b.Kind == BlockPower.Free ? "Puissance libre" : BlockPowerText(b);
            _blockCadence.Text = b.RampTest ? progress.RampBestMinuteW > 0 ? $"Meilleure minute {progress.RampBestMinuteW:0} W" : "Tenez le palier"
                : b.FreeCadence ? "Cadence libre" : $"Cadence {b.CadenceRpm} tr/min";
            _skipButton.Text = b.RampTest ? "J'arrête le test" : "Bloc suivant";
            _blockBar.Value = 1.0 - progress.BlockRemainingS / Math.Max(1, b.DurationS);
            _freeErgRow.Visible = b.Kind == BlockPower.Free;
            _skipButton.Disabled = false;
        }
        else
        {
            _blockTitle.Text = "Entraînement terminé";
            _blockRemaining.Text = "Bravo !";
            _blockPower.Text = "Puissance libre";
            _blockCadence.Text = progress.FtpTestResultW is { } ftp ? $"Nouvelle FTP : {ftp} W. Touchez Terminer pour enregistrer."
                : "Touchez Terminer pour enregistrer.";
            _blockBar.Value = 1;
            _freeErgRow.Visible = true;
            _skipButton.Disabled = true;
        }
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
        StartDataLog();
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
    void GearChanged()
    {
        _sinceGearCommand = 2.0;
        Recorder?.LogEvent("gear", Gears!.GearLabel);
    }

    /// <summary>Fichier de données : chaque valeur des capteurs et chaque commande au trainer à partir
    /// d'ici, jusqu'à la fin. Les capteurs connectés entrent dans l'en-tête avec leur fiche.</summary>
    void StartDataLog()
    {
        Hub.Measured -= OnMeasured;
        Hub.Measured += OnMeasured;
        Hub.Commanded -= OnMeasured;
        Hub.Commanded += OnMeasured;
        foreach (var d in Hub.Devices.Sorted)
            if (d.State == LinkState.Connected) SensorIndex(d.Address);
        if (Gears is not null) Recorder!.LogEvent("gear", Gears.GearLabel);
    }

    void StopDataLog()
    {
        Hub.Measured -= OnMeasured;
        Hub.Commanded -= OnMeasured;
    }

    int SensorIndex(string address)
    {
        var d = Hub.Devices.Get(address);
        return Recorder!.SensorIndex(address, d?.DisplayName ?? address, d?.Kind ?? DeviceKind.Unknown, d?.Services ?? [], d?.Specs ?? new DeviceSpecs());
    }

    void OnMeasured(string address, string type, string value)
    {
        if (Recorder is null) return;
        Recorder.Log(DateTimeOffset.Now, type, SensorIndex(address), value);
    }

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
        // On rend la main au cycliste : plus de consigne ERG après la sortie (notée dans les données).
        if (Hub.TrainerAddress != "") Hub.EnterSimulation(0.0);
        var ride = Recorder.Finish();
        // Bilan calculé comme à la réouverture : depuis le fichier de données.
        if (ride.Events.Exists(e => e.Sensor != RideEvent.App)) { ride.Rebuild(); ride.Summarize(); }
        // Test FTP réussi : la nouvelle FTP est enregistrée tout de suite dans les réglages.
        int? newFtp = Progress?.FtpTestResultW;
        string ftpExplanation = Progress?.FtpTestExplanation ?? "";
        int oldFtp = _profile.FtpW;
        if (newFtp is { } ftp)
        {
            ride.Info.FtpTestW = ftp;
            _profile.FtpW = ftp;
            _profile.FtpTestedAt = DateTimeOffset.Now;
        }
        Recorder = null;
        StopDataLog();
        Gears = null;
        Progress = null;
        DisplayServer.ScreenSetKeepOn(false);
        if (ride.Samples.Count == 0)
        {
            Navigator.GoTo(this, Workout is null ? Navigator.Menu : Navigator.Workouts);
            return;
        }
        Store.Save(ride);
        Store.SaveProfile(_profile);
        var summary = Navigator.Create<RideSummaryScreen>(Navigator.Summary);
        summary.Ride = ride;
        summary.JustFinished = true;
        summary.PreviousFtpW = oldFtp;
        summary.FtpTestExplanation = ftpExplanation;
        Navigator.GoTo(this, summary);
    }

    public override void _Process(double delta)
    {
        if (Recorder is null) return;
        if (Progress is not null) UpdateWorkoutControl(delta);
        double before = Recorder.ElapsedS;
        Recorder.Tick(delta, Hub.Power, Hub.Cadence, Hub.HeartRate);
        Progress?.Advance(Recorder.ElapsedS - before, Hub.Power);
        // Test rampe : sous 40 tr/min pendant 5 s, le palier n'est plus tenu, le test s'arrête.
        if (Progress?.Current is { RampTest: true } && !Recorder.IsPaused)
        {
            _rampStallS = Hub.Cadence < 40 ? _rampStallS + delta : 0;
            if (_rampStallS >= RampStallLimitS)
            {
                _rampStallS = 0;
                SkipBlock();
            }
        }
        if (Gears is not null) UpdateGearResistance(delta);

        _sinceAutosave += delta;
        if (_sinceAutosave >= AutosaveEveryS) Autosave();

        _clock.Text = Ui.Duration(Recorder.ElapsedS);
        _power.Text = $"{Hub.Power} W";
        _powerTitle.Text = Progress is not null ? Recorder.TargetPowerW > 0 ? $"Puissance · cible {Recorder.TargetPowerW} W" : "Puissance · libre"
            : Recorder.Ride.Info.Mode == RideMode.TargetPower ? $"Puissance · cible {Recorder.TargetPowerW} W"
            : Gears is not null && _gearTargetW >= 0 ? $"Puissance · demandée {_gearTargetW} W" : "Puissance";
        if (Gears is not null)
        {
            _gearLabel.Text = $"{Gears.GearLabel}\n{Gears.RearGearNumber}/{Gears.Cogs.Length}";
            _frontButton.Text = $"Plateau {Gears.Chainring}";
            _frontButton.Disabled = Gears.Chainrings.Length < 2;
        }
        _cadence.Text = $"{Hub.Cadence:0} tr/min";
        _cadenceTitle.Text = Recorder.TargetCadenceRpm > 0 ? $"Cadence · cible {Recorder.TargetCadenceRpm}" : "Cadence";
        // Hors de la cadence demandée (± 5 tr/min) : la valeur passe en orange.
        _cadence.Modulate = Recorder.TargetCadenceRpm > 0 && Math.Abs(Hub.Cadence - Recorder.TargetCadenceRpm) > 5 ? Ui.Warning : Colors.White;
        if (Progress is not null) RefreshWorkout();
        _freeErgToggle.SetPressedNoSignal(FreeErg);
        _freeErgToggle.Text = FreeErg ? $"ERG {FreeErgW} W" : "ERG : non";
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
        if (Recorder is null) Navigator.GoTo(this, Workout is null ? Navigator.Menu : Navigator.Workouts);
        else if (!Recorder.IsPaused) TogglePause(); // pendant la sortie, Retour met en pause sans rien perdre
    }
}
