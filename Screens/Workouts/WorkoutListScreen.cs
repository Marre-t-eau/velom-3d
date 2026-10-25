using System;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Les entraînements structurés à faire : profil, durée, TSS prévu ; démarrer ou modifier.</summary>
public partial class WorkoutListScreen : Screen
{
    /// <summary>Nombre d'entraînements affichés (pour les tests).</summary>
    internal int WorkoutCount { get; private set; }
    internal Button NewButton { get; private set; } = null!;
    internal Label FtpLabel { get; private set; } = null!;

    int _ftpW;

    public override void _Ready()
    {
        var profile = Store.LoadProfile();
        _ftpW = profile.FtpW;
        var root = Ui.BuildPage(this);
        var header = Ui.Row();
        root.AddChild(header);
        var title = Ui.Label("Entraînements", 40);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        NewButton = Ui.Button("Nouvel entraînement", CreateWorkout, 300, 72);
        header.AddChild(NewButton);
        header.AddChild(Ui.Button("Retour", OnBack, 200, 72));

        var ftp = Ui.Row(12);
        root.AddChild(ftp);
        FtpLabel = Ui.Label($"FTP {_ftpW} W", 30, Ui.Accent);
        FtpLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        ftp.AddChild(FtpLabel);
        var ftpDetail = Ui.Label((profile.FtpTestedAt is { } at ? $"testée le {Ui.Date(at)}" : "pas encore testée")
                                 + " · les pourcentages se calculent dessus · modifiable dans Réglages", 19, Ui.Muted);
        ftpDetail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ftpDetail.VerticalAlignment = VerticalAlignment.Center;
        ftp.AddChild(ftpDetail);

        var list = Ui.AddScroll(root, 12);
        list.AddChild(Card(Workout.FtpTestWorkout(), "Test FTP : 20 min à fond, la FTP vaut 95 % de la moyenne."));
        list.AddChild(Card(Workout.RampTestWorkout(), "Test FTP : paliers de 1 min, de 100 W, +20 W chaque minute, jusqu'à ne plus tenir. "
                                                      + "La FTP vaut 75 % de la meilleure minute."));
        var workouts = Workouts.List();
        WorkoutCount = workouts.Count;
        if (workouts.Count == 0)
            list.AddChild(Ui.Label("Aucun entraînement. Touchez « Nouvel entraînement » pour en créer un.", 22, Ui.Muted));
        foreach (var w in workouts) list.AddChild(Card(w));
    }

    /// <summary>Carte d'un entraînement ; avec <paramref name="fixedNote"/>, entraînement intégré (pas de bouton Modifier).</summary>
    Control Card(Workout w, string fixedNote = "")
    {
        var panel = Ui.Panel(Ui.Card);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);

        var top = Ui.Row();
        box.AddChild(top);
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        top.AddChild(titles);
        titles.AddChild(Ui.Label(w.Name.Length > 0 ? w.Name : "Sans nom", 26));
        // Test rampe : sa durée dépend de quand on lâche, la durée et le TSS « prévus » n'ont pas de sens.
        titles.AddChild(Ui.Label(w.Blocks.Exists(b => b.RampTest) ? "20 à 30 min selon votre forme"
            : $"{Ui.Duration(w.TotalDurationS)} · {w.Blocks.Count} bloc(s) · ≈ {w.EstimatedTss(_ftpW):0} TSS · "
              + (w.Unit == PowerUnit.PercentFtp ? "en % de FTP" : "en watts"), 18, Ui.Muted));
        if (fixedNote.Length > 0) titles.AddChild(Ui.Label(fixedNote, 18, Ui.Accent));
        else top.AddChild(Ui.Button("Modifier", () => Edit(w), 180, 72, 22));
        var start = Ui.Button("Démarrer", () => StartWorkout(w), 200, 72, 22);
        start.Disabled = w.Blocks.Count == 0;
        top.AddChild(start);

        box.AddChild(new WorkoutChart { Workout = w, FtpW = _ftpW, CustomMinimumSize = new Vector2(0, 90) });
        return panel;
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
