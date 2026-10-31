using System;
using Godot;
using Velom3d.Rides;
using Velom3d.Sensors;
using Velom3d.World;

namespace Velom3d.Screens;

/// <summary>
/// Sortie libre sur le plat. Deux façons de faire résister le home trainer :
/// puissance imposée (ERG, réglable en roulant) ou simulation réaliste (pente nulle, pneus et
/// air : le trainer durcit avec la vitesse). La vitesse affichée vient du même modèle physique.
/// En simulation, les vitesses virtuelles (si elles sont activées dans les réglages) remplacent
/// les paramètres de simulation : l'appli calcule la puissance que demandent la vitesse engagée
/// et la cadence, et la donne au trainer en consigne, mise à jour en continu.
/// La sortie est enregistrée sur l'appareil, et toutes les 30 s au cas où l'appli serait coupée.
/// Pendant la sortie, le cycliste roule en 3D sur un circuit en boucle (<see cref="RideWorld"/>), en fond d'écran.
/// Avec un <see cref="Workout"/>, le même écran déroule un entraînement structuré : la consigne
/// suit les blocs (ERG fixe ou en rampe, simulation sur le plat pour les blocs libres), et la liste
/// des blocs à venir s'affiche avec leur durée, leur puissance et leur cadence.
/// Le bouton Menu de la sortie ouvre une fenêtre : revenir à la sortie, passer au bloc suivant,
/// ouvrir la page des capteurs (par-dessus la sortie, mise en pause le temps d'y être) ou terminer.
/// Pendant un entraînement, « Terminer » n'est dans la barre du bas qu'une fois la séance finie.
/// Mise en page des deux pages (réglages, sortie) et du menu : FreeRideScreen.tscn.
/// </summary>
public partial class FreeRideScreen : Screen
{
    const double AutosaveEveryS = 30.0;

    RiderProfile _profile = null!;
    RideMode _mode;
    int _target;

    Control _setup = null!, _riding = null!;
    Button _ergChoice = null!, _simChoice = null!;
    Label _settingsSummary = null!, _trainerHint = null!;

    Label _clock = null!, _modeLabel = null!, _power = null!, _powerTitle = null!, _cadence = null!, _hr = null!;
    Label _speed = null!, _distance = null!, _rideTarget = null!, _trainerStatus = null!;
    Control _ergControls = null!, _gearControls = null!;
    Label _gearLabel = null!;
    Button _frontButton = null!;
    double _smoothCadence, _sinceGearCommand;
    int _gearTargetW = -1;
    Button _pauseButton = null!, _finishButton = null!;
    double _sinceAutosave;
    /// <summary>Temps restant (s) pendant lequel la tuile puissance montre la moyenne (touchée par le cycliste).</summary>
    double _showAverageS;
    const double ShowAverageForS = 5.0;
    /// <summary>« Terminer » touché une fois : sortie en pause, en attente de confirmation.</summary>
    bool _finishArmed;

    /// <summary>Enregistrement en cours (null avant le départ), exposé aux tests.</summary>
    internal RideRecorder? Recorder { get; private set; }
    /// <summary>Vitesses virtuelles de la sortie en cours (null si désactivées ou en ERG).</summary>
    internal Drivetrain? Gears { get; private set; }
    /// <summary>Vue 3D de la sortie en cours (null avant le départ).</summary>
    internal RideWorld? Scene3D { get; private set; }
    CanvasItem _backdrop = null!;

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
    Control _rideMenu = null!;
    Label _menuState = null!;
    Button _menuSkip = null!;
    /// <summary>Page des capteurs ouverte par-dessus la sortie (null sinon).</summary>
    internal DeviceConnectionScreen? SensorsPage { get; private set; }
    /// <summary>La sortie roulait avant d'ouvrir la page des capteurs : elle reprend au retour.</summary>
    bool _resumeAfterSensors;
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
        _backdrop = GetNode<CanvasItem>("%Background"); // fond uni de la page, masqué pendant la sortie pour voir la 3D
        FindSetup();
        FindRiding();
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

    void FindSetup()
    {
        _setup = GetNode<Control>("%Setup");
        _ergChoice = GetNode<Button>("%ErgChoice");
        _ergChoice.Pressed += () => { _mode = RideMode.TargetPower; RefreshSetup(); };
        _simChoice = GetNode<Button>("%SimChoice");
        _simChoice.Pressed += () => { _mode = RideMode.Simulation; RefreshSetup(); };
        _settingsSummary = GetNode<Label>("%SettingsSummary");
        _trainerHint = GetNode<Label>("%TrainerHint");
        GetNode<Button>("%BackButton").Pressed += OnBack;
        GetNode<Button>("%StartButton").Pressed += () => Start(_mode, _target);
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

    void FindRiding()
    {
        _riding = GetNode<Control>("%Riding");
        _clock = GetNode<Label>("%Clock");
        _modeLabel = GetNode<Label>("%ModeLabel");

        // HUD compact : chrono à gauche et mesures en bandeau en haut, commandes en bas,
        // entraînement en deux petits panneaux sur les côtés : la route reste visible au milieu.
        // La puissance moyenne n'a pas sa tuile : toucher la puissance l'affiche quelques secondes.
        var powerTile = GetNode<Control>("%PowerTile");
        _power = powerTile.GetNode<Label>("Box/Value");
        _powerTitle = powerTile.GetNode<Label>("Box/Title");
        powerTile.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ShowAveragePower();
        };
        _cadence = GetNode<Label>("%CadenceTile/Box/Value");
        _cadenceTitle = GetNode<Label>("%CadenceTile/Box/Title");
        _hr = GetNode<Label>("%HeartRateTile/Box/Value");
        _speed = GetNode<Label>("%SpeedTile/Box/Value");
        _distance = GetNode<Label>("%DistanceTile/Box/Value");
        FindWorkoutPanel();

        _ergControls = GetNode<Control>("%ErgControls");
        GetNode<Button>("%Minus10").Pressed += () => ChangeTarget(-10);
        GetNode<Button>("%Minus5").Pressed += () => ChangeTarget(-5);
        GetNode<Button>("%Plus5").Pressed += () => ChangeTarget(+5);
        GetNode<Button>("%Plus10").Pressed += () => ChangeTarget(+10);
        _rideTarget = GetNode<Label>("%RideTarget");

        // Vitesses virtuelles : plateau, puis pignon plus souple / plus dur.
        _gearControls = GetNode<Control>("%GearControls");
        _frontButton = GetNode<Button>("%FrontButton");
        _frontButton.Pressed += ShiftFront;
        GetNode<Button>("%EasierButton").Pressed += ShiftEasier;
        GetNode<Button>("%HarderButton").Pressed += ShiftHarder;
        _gearLabel = GetNode<Label>("%GearLabel");
        _trainerStatus = GetNode<Label>("%TrainerStatus");

        _pauseButton = GetNode<Button>("%PauseButton");
        _pauseButton.Pressed += TogglePause;
        _finishButton = GetNode<Button>("%FinishButton");
        _finishButton.Pressed += OnFinishPressed;
        GetNode<Button>("%MenuButton").Pressed += OpenMenu;
        FindMenu();
    }

    // --- Menu pendant la sortie ---

    void FindMenu()
    {
        _rideMenu = GetNode<Control>("%RideMenu");
        _menuState = GetNode<Label>("%MenuState");
        GetNode<ColorRect>("%Shade").GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) CloseMenu(); };
        GetNode<Button>("%MenuResume").Pressed += CloseMenu;
        _menuSkip = GetNode<Button>("%MenuSkip");
        _menuSkip.Pressed += () => { CloseMenu(); SkipBlock(); };
        GetNode<Button>("%MenuSensors").Pressed += OpenSensors;
        GetNode<Button>("%MenuFinish").Pressed += () =>
        {
            CloseMenu();
            Confirm("Terminer la sortie ?", "Elle est enregistrée et son bilan s'ouvre.", "Terminer", Finish);
        };
    }

    internal bool MenuOpen => _rideMenu.Visible;
    /// <summary>Bouton « bloc suivant » du menu (pour les tests).</summary>
    internal Button MenuSkipButton => _menuSkip;

    internal void OpenMenu()
    {
        if (Recorder is null) return;
        bool workout = Progress is { IsFinished: false };
        _menuSkip.Visible = workout;
        _menuSkip.Text = Progress?.Current is { RampTest: true } ? "J'arrête le test" : "Passer au bloc suivant";
        _menuState.Text = (Recorder.IsPaused ? "Sortie en pause · " : "La sortie continue · ") + Ui.Duration(Recorder.ElapsedS);
        _rideMenu.Visible = true;
    }

    internal void CloseMenu() => _rideMenu.Visible = false;

    /// <summary>Page des capteurs par-dessus la sortie : la sortie se met en pause (rien n'est perdu),
    /// et reprend au retour si elle roulait.</summary>
    internal void OpenSensors()
    {
        if (Recorder is null || SensorsPage is not null) return;
        CloseMenu();
        _resumeAfterSensors = !Recorder.IsPaused;
        if (_resumeAfterSensors) Recorder.Pause();
        _finishArmed = false;
        SensorsPage = Navigator.Create<DeviceConnectionScreen>(Navigator.Connection);
        SensorsPage.ReturnToRide = CloseSensors;
        _riding.Visible = false;
        AddChild(SensorsPage);
    }

    /// <summary>Retour à la sortie : le trainer (peut-être reconnecté ou nouveau) reçoit de nouveau sa consigne.</summary>
    internal void CloseSensors()
    {
        if (SensorsPage is null) return;
        Hub.StopScan();
        SensorsPage.QueueFree();
        SensorsPage = null;
        _riding.Visible = true;
        if (Recorder is null) return;
        if (_resumeAfterSensors && Recorder.IsPaused) Recorder.Resume();
        _resumeAfterSensors = false;
        ApplyTrainerMode();
    }

    /// <summary>La tuile puissance montre la moyenne de la sortie pendant quelques secondes.</summary>
    internal void ShowAveragePower() => _showAverageS = ShowAverageForS;

    // --- Entraînement structuré ---

    /// <summary>Bloc en cours (temps restant, consigne, profil) à gauche, blocs à venir à droite ;
    /// le milieu reste libre pour voir le cycliste en 3D.</summary>
    void FindWorkoutPanel()
    {
        _workoutPanel = GetNode<Control>("%WorkoutPanel");
        _blockTitle = GetNode<Label>("%BlockTitle");
        _blockRemaining = GetNode<Label>("%BlockRemaining");
        _blockPower = GetNode<Label>("%BlockPower");
        _blockCadence = GetNode<Label>("%BlockCadence");
        _blockBar = GetNode<ProgressBar>("%BlockBar");
        // Bloc libre : le cycliste peut quand même faire imposer des watts par le trainer.
        _freeErgRow = GetNode<Control>("%FreeErgRow");
        _freeErgToggle = GetNode<Button>("%FreeErgToggle");
        _freeErgToggle.Pressed += ToggleFreeErg;
        GetNode<Button>("%FreeErgMinus10").Pressed += () => ChangeFreeErg(-10);
        GetNode<Button>("%FreeErgMinus5").Pressed += () => ChangeFreeErg(-5);
        GetNode<Button>("%FreeErgPlus5").Pressed += () => ChangeFreeErg(+5);
        GetNode<Button>("%FreeErgPlus10").Pressed += () => ChangeFreeErg(+10);
        // Profil de l'entraînement sous le bloc en cours : où on en est, ce qui reste.
        _workoutChart = GetNode<WorkoutChart>("%WorkoutChart");
        _upcoming = GetNode<VBoxContainer>("%Upcoming");
    }

    /// <summary>Nombre de blocs à venir listés : au-delà, une ligne « + N autres blocs ».</summary>
    const int UpcomingShown = 3;

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
        ShowRiding();
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
        int shown = 0, hidden = 0;
        bool rampNote = false;
        for (int i = current + 1; i < w.Blocks.Count; i++)
        {
            var b = w.Blocks[i];
            // Au-delà des premières lignes : « + N autres blocs ». Test rampe : la suite des paliers
            // est « jusqu'à ne plus tenir ».
            if (shown >= UpcomingShown)
            {
                if (!b.RampTest) hidden++;
                else if (!rampNote)
                {
                    _upcoming.AddChild(Ui.Label("… +20 W chaque minute, jusqu'à ne plus tenir", 18, Ui.Muted));
                    rampNote = true;
                }
                continue;
            }
            shown++;
            var row = Ui.Row(8);
            _upcoming.AddChild(row);
            var duration = Ui.Label(Ui.Duration(b.DurationS), 21);
            duration.CustomMinimumSize = new Vector2(66, 0);
            duration.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(duration);
            var power = Ui.Label(BlockPowerText(b), 21, Ui.PowerColor);
            power.AutowrapMode = TextServer.AutowrapMode.Off;
            power.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(power);
            var cadence = Ui.Label(CadenceText(b), 21, Ui.CadenceColor);
            cadence.CustomMinimumSize = new Vector2(138, 0);
            cadence.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(cadence);
            var start = Ui.Label("", 18, Ui.Muted);
            start.CustomMinimumSize = new Vector2(99, 0);
            start.HorizontalAlignment = HorizontalAlignment.Right;
            start.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(start);
            _upcomingStarts.Add((start, w.StartOf(i)));
        }
        if (hidden > 0)
            _upcoming.AddChild(Ui.Label($"+ {hidden} autre{(hidden > 1 ? "s" : "")} bloc{(hidden > 1 ? "s" : "")}", 18, Ui.Muted));
        if (current + 1 >= w.Blocks.Count)
            _upcoming.AddChild(Ui.Label(current >= w.Blocks.Count ? "Rien : entraînement terminé." : "Rien : c'est le dernier bloc.", 18, Ui.Muted));
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
            _blockBar.Value = 1.0 - progress.BlockRemainingS / Math.Max(1, b.DurationS);
            _freeErgRow.Visible = b.Kind == BlockPower.Free;
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
        ApplyTrainerMode();
        _modeLabel.Text = mode == RideMode.TargetPower ? "Puissance imposée · plat"
            : Gears is not null ? $"Simulation réaliste · plat · {Gears.Describe()}"
            : "Simulation réaliste · plat";
        _ergControls.Visible = mode == RideMode.TargetPower;
        _gearControls.Visible = Gears is not null;
        ShowRiding();
    }

    /// <summary>Envoie au trainer le mode de la sortie (au départ, et au retour de la page des capteurs).</summary>
    void ApplyTrainerMode()
    {
        if (Recorder is null || Hub.TrainerAddress == "") return;
        if (Progress is not null)
        {
            // Entraînement : la prochaine mise à jour renvoie la consigne du bloc en cours.
            _workoutErg = null;
            _sentTargetW = -1;
            return;
        }
        if (_mode == RideMode.TargetPower) Hub.EnterErg(Recorder.TargetPowerW);
        else if (Gears is not null) Hub.EnterErg(GearTargetW());
        else ApplySimulation();
        _gearTargetW = Gears is null ? -1 : GearTargetW();
    }

    /// <summary>Page de la sortie, avec le cycliste en 3D en fond (le fond uni de la page est masqué).</summary>
    void ShowRiding()
    {
        _setup.Visible = false;
        _riding.Visible = true;
        Scene3D = new RideWorld();
        AddChild(Scene3D);
        _backdrop.Visible = false;
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
    internal string PowerTileTitle => _powerTitle.Text;
    internal bool FinishButtonShown => _finishButton.Visible;

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
        bool moving = !Recorder.IsPaused;
        Scene3D?.Follow(Recorder.DistanceM, moving ? Recorder.Physics.SpeedMps : 0, moving ? Hub.Cadence : 0);
        if (Gears is not null) UpdateGearResistance(delta);

        _sinceAutosave += delta;
        if (_sinceAutosave >= AutosaveEveryS) Autosave();

        _clock.Text = Ui.Duration(Recorder.ElapsedS);
        _showAverageS = Math.Max(0, _showAverageS - delta);
        if (_showAverageS > 0)
        {
            _power.Text = Recorder.ElapsedS > 0 ? $"{Recorder.AveragePowerW:0} W" : "--";
            _powerTitle.Text = "Puissance moyenne";
        }
        else
        {
            // En ERG, la cible est déjà affichée au-dessus des boutons ±.
            _power.Text = $"{Hub.Power} W";
            _powerTitle.Text = Progress is not null ? Recorder.TargetPowerW > 0 ? $"Puissance · cible {Recorder.TargetPowerW} W" : "Puissance · libre"
                : Gears is not null && _gearTargetW >= 0 ? $"Puissance · demandée {_gearTargetW} W" : "Puissance";
        }
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
        _rideTarget.Text = $"Cible {Recorder.TargetPowerW} W";
        _finishButton.Text = _finishArmed ? "Confirmer la fin" : "Terminer";
        // Entraînement : « Terminer » n'apparaît qu'une fois la séance finie (sinon, dans le menu).
        _finishButton.Visible = Progress is null || Progress.IsFinished;
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
        if (SensorsPage is not null) { base._UnhandledInput(e); return; }
        if (Gears is not null && e.IsActionPressed("ui_up")) { ShiftHarder(); GetViewport().SetInputAsHandled(); return; }
        if (Gears is not null && e.IsActionPressed("ui_down")) { ShiftEasier(); GetViewport().SetInputAsHandled(); return; }
        base._UnhandledInput(e);
    }

    protected override void OnBack()
    {
        if (SensorsPage is not null) CloseSensors();
        else if (MenuOpen) CloseMenu();
        else if (Recorder is null) Navigator.GoTo(this, Workout is null ? Navigator.Menu : Navigator.Workouts);
        else if (!Recorder.IsPaused) TogglePause(); // pendant la sortie, Retour met en pause sans rien perdre
    }
}
