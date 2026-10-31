using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Toutes les sorties enregistrées sur l'appareil, la plus récente en haut.</summary>
public partial class RideHistoryScreen : Screen
{
    /// <summary>Carte d'une sortie dans la liste (RideCard.tscn).</summary>
    [Export] public PackedScene CardScene { get; set; } = null!;

    /// <summary>Nombre de sorties affichées (pour les tests).</summary>
    internal int RideCount { get; private set; }

    public override void _Ready()
    {
        GetNode<Button>("%BackButton").Pressed += OnBack;
        var rides = Store.List();
        RideCount = rides.Count;
        var totals = GetNode<Label>("%Totals");
        totals.Text = rides.Count > 0 ? Totals(rides) : "";
        totals.Visible = rides.Count > 0;
        GetNode<Label>("%Empty").Visible = rides.Count == 0;
        var list = GetNode<VBoxContainer>("%List");
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
        var card = CardScene.Instantiate<Button>();
        card.GetNode<Label>("%Date").Text = $"{Ui.Date(info.StartedAt)}  ·  {ModeName(info)}";
        card.GetNode<Label>("%Stats").Text = $"{Ui.Duration(s.DurationS)}   {Ui.Km(s.DistanceM)}   {s.AvgPowerW:0} W moy.{PerKg(s.AvgPowerW, info)}   "
                                             + $"{s.Tss:0} TSS   IF {s.IntensityFactor:0.00}   {s.Kcal:0} kcal";
        card.Pressed += () => Open(info.Id);
        return card;
    }

    static string PerKg(double powerW, RideInfo info) => info.RiderMassKg > 0 ? $" ({Ride.PerKg(powerW, info):0.0} W/kg)" : "";

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
