using System;
using System.Collections.Generic;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Plan d'entraînement endurance : la prochaine séance, l'avancement, et toutes les semaines
/// (mise en page : TrainingPlanScreen.tscn, PlanWeek.tscn, PlanSessionRow.tscn).</summary>
public partial class TrainingPlanScreen : Screen
{
    /// <summary>Une semaine du plan (PlanWeek.tscn).</summary>
    [Export] public PackedScene WeekScene { get; set; } = null!;
    /// <summary>Une séance dans sa semaine (PlanSessionRow.tscn).</summary>
    [Export] public PackedScene SessionScene { get; set; } = null!;

    internal TrainingPlan Plan { get; } = TrainingPlan.Endurance();
    /// <summary>Séances faites depuis le début du plan (pour les tests).</summary>
    internal HashSet<string> Done { get; private set; } = new();
    internal Workout? Next { get; private set; }
    internal Button RestartButton { get; private set; } = null!;
    internal Button NextStartButton { get; private set; } = null!;

    RiderProfile _profile = null!;

    public override void _Ready()
    {
        _profile = Store.LoadProfile();
        Done = Plan.DoneSessions(Store.List(), _profile.PlanStartedAt);
        Next = Plan.NextSession(Done);

        GetNode<Button>("%BackButton").Pressed += OnBack;
        RestartButton = GetNode<Button>("%RestartButton");
        RestartButton.Pressed += AskRestart;
        RestartButton.Visible = _profile.PlanStartedAt is not null;
        GetNode<Label>("%Title").Text = Plan.Name;
        GetNode<Label>("%Description").Text = Plan.Description + $" FTP actuelle : {_profile.FtpW} W.";

        GetNode<Label>("%ProgressLabel").Text = _profile.PlanStartedAt is { } started
            ? $"{Done.Count} / {Plan.SessionCount} séances · commencé le {Ui.Date(started)}"
            : $"{Plan.SessionCount} séances · pas encore commencé";
        var bar = GetNode<ProgressBar>("%ProgressBar");
        bar.MaxValue = Plan.SessionCount;
        bar.Value = Done.Count;

        ShowNext();

        var weeks = GetNode<VBoxContainer>("%Weeks");
        foreach (var week in Plan.Weeks)
        {
            var box = WeekScene.Instantiate<VBoxContainer>();
            box.GetNode<Label>("%Title").Text = week.Title;
            box.GetNode<Label>("%Goal").Text = week.Goal;
            var sessions = box.GetNode<VBoxContainer>("%Sessions");
            foreach (var s in week.Sessions) sessions.AddChild(Row(s));
            weeks.AddChild(box);
        }
    }

    void ShowNext()
    {
        NextStartButton = GetNode<Button>("%NextStartButton");
        if (Next is null)
        {
            GetNode<Label>("%NextTitle").Text = "PLAN TERMINÉ";
            GetNode<Label>("%NextName").Text = "Bravo, les 8 semaines sont faites !";
            GetNode<Label>("%NextDetail").Text = "Refaites un test FTP pour mesurer vos progrès, puis recommencez le plan avec la nouvelle FTP.";
            NextStartButton.Visible = false;
            GetNode<Control>("%NextChart").Visible = false;
            return;
        }
        GetNode<Label>("%NextTitle").Text = _profile.PlanStartedAt is null ? "PREMIÈRE SÉANCE" : "PROCHAINE SÉANCE";
        GetNode<Label>("%NextName").Text = Next.Name;
        GetNode<Label>("%NextDetail").Text = Detail(Next);
        NextStartButton.Pressed += () => StartSession(Next);
        var chart = GetNode<WorkoutChart>("%NextChart");
        chart.Workout = Next;
        chart.FtpW = _profile.FtpW;
    }

    Control Row(Workout s)
    {
        var row = SessionScene.Instantiate<PanelContainer>();
        var state = Plan.StateOf(s, Done, Next);
        row.GetNode<Label>("%Name").Text = s.Name;
        row.GetNode<Label>("%Detail").Text = Detail(s);
        var status = row.GetNode<Label>("%Status");
        status.Text = state switch { SessionState.Done => "faite", SessionState.Next => "prochaine", _ => "à faire" };
        status.ThemeTypeVariation = state == SessionState.Todo ? "MutedLabel" : "AccentLabel";
        if (state == SessionState.Next) row.ThemeTypeVariation = "ActivePanel";
        if (state == SessionState.Done) row.Modulate = new Color(1, 1, 1, 0.6f);
        var start = row.GetNode<Button>("%StartButton");
        start.Text = state == SessionState.Done ? "Refaire" : "Démarrer";
        if (state == SessionState.Next) start.ThemeTypeVariation = "PrimaryButton";
        start.Pressed += () => StartSession(s);
        return row;
    }

    /// <summary>« 1:00:00 · ≈ 43 TSS · jusqu'à 70 % FTP ».</summary>
    string Detail(Workout s)
    {
        int peak = 0;
        foreach (var b in s.Blocks) if (b.Kind == BlockPower.Fixed) peak = Math.Max(peak, b.Power);
        return $"{Ui.Duration(s.TotalDurationS)} · ≈ {s.EstimatedTss(_profile.FtpW):0} TSS · jusqu'à {peak} % FTP "
               + $"({(int)Math.Round(peak * _profile.FtpW / 100.0)} W)";
    }

    /// <summary>Démarre une séance ; la première commence le plan (les sorties comptent à partir de là).</summary>
    internal void StartSession(Workout s)
    {
        if (_profile.PlanStartedAt is null)
        {
            _profile.PlanStartedAt = DateTimeOffset.Now;
            Store.SaveProfile(_profile);
        }
        var ride = Navigator.Create<FreeRideScreen>(Navigator.FreeRide);
        ride.Workout = s;
        Navigator.GoTo(this, ride);
    }

    void AskRestart() => Confirm("Recommencer le plan ?",
        "Les séances déjà faites restent dans l'historique, mais le plan repart de la semaine 1.",
        "Recommencer", Restart);

    internal void Restart()
    {
        _profile.PlanStartedAt = DateTimeOffset.Now;
        Store.SaveProfile(_profile);
        Navigator.GoTo(this, Navigator.Plan);
    }

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Workouts);
}
