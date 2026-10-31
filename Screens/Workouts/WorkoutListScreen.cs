using System;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Les entraînements structurés à faire : profil, durée, TSS prévu ; démarrer ou modifier.</summary>
public partial class WorkoutListScreen : Screen
{
    /// <summary>Carte d'un entraînement dans la liste (WorkoutCard.tscn).</summary>
    [Export] public PackedScene CardScene { get; set; } = null!;

    /// <summary>Nombre d'entraînements affichés (pour les tests).</summary>
    internal int WorkoutCount { get; private set; }
    internal Button NewButton { get; private set; } = null!;
    internal Label FtpLabel { get; private set; } = null!;

    int _ftpW;

    public override void _Ready()
    {
        var profile = Store.LoadProfile();
        _ftpW = profile.FtpW;
        NewButton = GetNode<Button>("%NewButton");
        NewButton.Pressed += CreateWorkout;
        GetNode<Button>("%BackButton").Pressed += OnBack;
        FtpLabel = GetNode<Label>("%FtpLabel");
        FtpLabel.Text = $"FTP {_ftpW} W";
        GetNode<Label>("%FtpDetail").Text = (profile.FtpTestedAt is { } at ? $"testée le {Ui.Date(at)}" : "pas encore testée")
                                           + " · les pourcentages se calculent dessus · modifiable dans Réglages";

        var list = GetNode<VBoxContainer>("%List");
        list.AddChild(Card(Workout.FtpTestWorkout(), "Test FTP : 20 min à fond, la FTP vaut 95 % de la moyenne."));
        list.AddChild(Card(Workout.RampTestWorkout(), "Test FTP : paliers de 1 min, de 100 W, +20 W chaque minute, jusqu'à ne plus tenir. "
                                                      + "La FTP vaut 75 % de la meilleure minute."));
        var workouts = Workouts.List();
        WorkoutCount = workouts.Count;
        var empty = GetNode<Label>("%Empty");
        empty.Visible = workouts.Count == 0;
        list.MoveChild(empty, -1);
        foreach (var w in workouts) list.AddChild(Card(w));
    }

    /// <summary>Carte d'un entraînement ; avec <paramref name="fixedNote"/>, entraînement intégré (pas de bouton Modifier).</summary>
    Control Card(Workout w, string fixedNote = "")
    {
        var card = CardScene.Instantiate<PanelContainer>();
        card.GetNode<Label>("%Name").Text = w.Name.Length > 0 ? w.Name : "Sans nom";
        // Test rampe : sa durée dépend de quand on lâche, la durée et le TSS « prévus » n'ont pas de sens.
        card.GetNode<Label>("%Detail").Text = w.Blocks.Exists(b => b.RampTest) ? "20 à 30 min selon votre forme"
            : $"{Ui.Duration(w.TotalDurationS)} · {w.Blocks.Count} bloc(s) · ≈ {w.EstimatedTss(_ftpW):0} TSS · "
              + (w.Unit == PowerUnit.PercentFtp ? "en % de FTP" : "en watts");
        var note = card.GetNode<Label>("%Note");
        note.Text = fixedNote;
        note.Visible = fixedNote.Length > 0;
        var edit = card.GetNode<Button>("%EditButton");
        edit.Visible = fixedNote.Length == 0;
        edit.Pressed += () => Edit(w);
        var start = card.GetNode<Button>("%StartButton");
        start.Disabled = w.Blocks.Count == 0;
        start.Pressed += () => StartWorkout(w);
        var chart = card.GetNode<WorkoutChart>("%Chart");
        chart.Workout = w;
        chart.FtpW = _ftpW;
        return card;
    }

    void CreateWorkout()
    {
        var w = Workout.CreateDefault(DateTimeOffset.Now, "Nouvel entraînement");
        Workouts.Save(w);
        Edit(w);
    }

    void Edit(Workout w)
    {
        var editor = Navigator.Create<WorkoutEditorScreen>(Navigator.WorkoutEditor);
        editor.WorkoutId = w.Id;
        Navigator.GoTo(this, editor);
    }

    internal void StartWorkout(Workout w)
    {
        var ride = Navigator.Create<FreeRideScreen>(Navigator.FreeRide);
        ride.Workout = w;
        Navigator.GoTo(this, ride);
    }

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Menu);
}
