using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Toutes les sorties enregistrées sur l'appareil, la plus récente en haut.</summary>
public partial class RideHistoryScreen : Screen
{
    /// <summary>Nombre de sorties affichées (pour les tests).</summary>
    internal int RideCount { get; private set; }

    public override void _Ready()
    {
        var root = Ui.BuildPage(this);
        var header = Ui.Row();
        root.AddChild(header);
        var title = Ui.Label("Mes sorties", 40);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(Ui.Button("Retour", OnBack, 200, 72));

        var rides = Store.List();
        RideCount = rides.Count;
        if (rides.Count > 0) root.AddChild(Ui.Label(Totals(rides), 20, Ui.Muted));

        var list = Ui.AddScroll(root, 12);
        if (rides.Count == 0)
            list.AddChild(Ui.Label("Pas encore de sortie. Lancez une sortie libre depuis le menu : elle apparaîtra ici.", 22, Ui.Muted));
        foreach (var info in rides) list.AddChild(RideCard(info));
    }

    static string Totals(System.Collections.Generic.List<RideInfo> rides)
    {
        double seconds = 0, meters = 0, tss = 0;
        foreach (var r in rides) { seconds += r.Summary.DurationS; meters += r.Summary.DistanceM; tss += r.Summary.Tss; }
        return $"{rides.Count} sortie(s) · {Ui.Duration(seconds)} · {Ui.Km(meters)} · {tss:0} TSS au total";
    }

    Button RideCard(RideInfo info)
    {
        var s = info.Summary;
        var button = Ui.Button("", () => Open(info.Id), 0, 116);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        box.OffsetLeft = 24;
        box.OffsetRight = -24;
        var date = Ui.Label($"{Ui.Date(info.StartedAt)}  ·  {ModeName(info)}", 24);
        date.MouseFilter = MouseFilterEnum.Ignore;
        var stats = Ui.Label($"{Ui.Duration(s.DurationS)}   {Ui.Km(s.DistanceM)}   {s.AvgPowerW:0} W moy.   "
                             + $"{s.Tss:0} TSS   IF {s.IntensityFactor:0.00}   {s.Kcal:0} kcal", 19, Ui.Muted);
        stats.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(date);
        box.AddChild(stats);
        button.AddChild(box);
        return button;
    }

    public static string ModeName(RideInfo info) =>
        info.Mode == RideMode.TargetPower ? "Puissance imposée"
        : info.Mode == RideMode.Workout ? $"Entraînement · {info.WorkoutName}"
        : info.VirtualGears.Length > 0 ? $"Simulation réaliste · {info.VirtualGears}"
        : "Simulation réaliste";

    void Open(string id)
    {
        if (Store.Load(id) is not { } ride) return;
        var summary = Navigator.Create<RideSummaryScreen>(Navigator.Summary);
        summary.Ride = ride;
        Navigator.GoTo(this, summary);
    }

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Menu);
}
