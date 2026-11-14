using System;
using Godot;
using Velom3d.Rides;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>Entraînement structuré dans <see cref="FreeRideScreen"/> : consignes des blocs vers le trainer,
/// bloc en cours, blocs à venir, ERG au choix pendant les blocs libres, arrêt du test rampe.</summary>
public partial class FreeRideScreen
{
    /// <summary>Entraînement structuré à dérouler, à renseigner avant l'ouverture (null = sortie libre).</summary>
    public Workout? Workout { get; set; }
    /// <summary>Avancement de l'entraînement en cours (pour les tests).</summary>
    internal WorkoutProgress? Progress { get; private set; }
    Control _workoutPanel = null!;
    WorkoutChart _workoutChart = null!;
    Label _blockTitle = null!, _blockRemaining = null!, _blockPower = null!, _blockCadence = null!;
    ProgressBar _blockBar = null!;
    VBoxContainer _upcoming = null!;
    readonly System.Collections.Generic.List<(Label Label, int StartS)> _upcomingStarts = new();
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
        if (FreeErg && FreeErgW == 0) FreeErgW = Math.Clamp((int)Math.Round(Math.Max(Hub.Power, 100) / 5.0) * 5, MinTargetW, MaxTargetW);
        _sinceTargetSent = 1.0;
    }

    internal void ChangeFreeErg(int delta)
    {
        if (!FreeErg) ToggleFreeErg();
        FreeErgW = Math.Clamp(FreeErgW + delta, MinTargetW, MaxTargetW);
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
        if (!Hub.HasTrainer) return;
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
        _freeErgToggle.SetPressedNoSignal(FreeErg);
        _freeErgToggle.Text = FreeErg ? $"ERG {FreeErgW} W" : "ERG : non";
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

    /// <summary>Test rampe : sous 40 tr/min pendant 5 s, le palier n'est plus tenu, le test s'arrête.</summary>
    void CheckRampStall(double delta)
    {
        if (Progress?.Current is not { RampTest: true } || Recorder!.IsPaused) return;
        _rampStallS = Hub.Cadence < 40 ? _rampStallS + delta : 0;
        if (_rampStallS < RampStallLimitS) return;
        _rampStallS = 0;
        SkipBlock();
    }
}
