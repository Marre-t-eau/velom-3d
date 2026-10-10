using System;
using System.Linq;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>
/// Bilan d'une sortie, juste après l'arrivée ou depuis l'historique : chiffres clés (TSS, IF,
/// puissance normalisée, calories…), temps passé dans chaque zone de puissance, et courbes de
/// puissance, cadence, vitesse avec la pente, et fréquence cardiaque.
/// </summary>
public partial class RideSummaryScreen : Screen
{
    public Ride Ride { get; set; } = null!;
    /// <summary>Ouvert à la fin d'une sortie : Retour mène au menu plutôt qu'à l'historique.</summary>
    public bool JustFinished { get; set; }

    /// <summary>Graphiques affichés (pour les tests).</summary>
    internal int ChartCount { get; private set; }

    Button _deleteButton = null!;
    double _deleteArmedUntil = -1;
    static double Now => Time.GetTicksMsec() / 1000.0;

    public override void _Ready()
    {
        var info = Ride.Info;
        var s = Ride.Summarize();
        var root = Ui.BuildPage(this, 14);

        var header = Ui.Row();
        root.AddChild(header);
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddChild(Ui.Label(JustFinished ? "Bravo, sortie enregistrée !" : "Bilan de la sortie", 36));
        titles.AddChild(Ui.Label($"{Ui.Date(info.StartedAt)}  ·  {RideHistoryScreen.ModeName(info.Mode)}  ·  FTP {info.FtpW} W", 19, Ui.Muted));
        header.AddChild(titles);
        header.AddChild(Ui.Button(JustFinished ? "Menu" : "Retour", OnBack, 200, 72));

        var content = Ui.AddScroll(root, 14);

        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        content.AddChild(grid);
        Stat(grid, "Durée", Ui.Duration(s.DurationS), Ui.Muted);
        Stat(grid, "Distance", Ui.Km(s.DistanceM), Ui.Muted);
        Stat(grid, "TSS", $"{s.Tss:0}", Ui.Accent);
        Stat(grid, "Facteur d'intensité", $"{s.IntensityFactor:0.00}", Ui.Accent);
        Stat(grid, "Puissance moyenne", $"{s.AvgPowerW:0} W", Ui.PowerColor);
        Stat(grid, "Puissance normalisée", $"{s.NormalizedPowerW:0} W", Ui.PowerColor);
        Stat(grid, "Puissance max", $"{s.MaxPowerW} W", Ui.PowerColor);
        Stat(grid, "Calories", $"{s.Kcal:0} kcal", Ui.Warning);
        Stat(grid, "Cadence moyenne", s.AvgCadenceRpm > 0 ? $"{s.AvgCadenceRpm:0} tr/min" : "--", Ui.CadenceColor);
        Stat(grid, "Vitesse moyenne", $"{s.AvgSpeedKmh:0.0} km/h", Ui.SpeedColor);
        Stat(grid, "FC moyenne", s.AvgHeartRateBpm > 0 ? $"{s.AvgHeartRateBpm:0} bpm" : "--", Ui.HeartRateColor);
        Stat(grid, "FC max", s.MaxHeartRateBpm > 0 ? $"{s.MaxHeartRateBpm} bpm" : "--", Ui.HeartRateColor);
        content.AddChild(Ui.Label($"Travail : {s.WorkKj:0} kJ. Calories estimées avec un rendement musculaire de 24 %.", 16, Ui.Muted));

        if (s.DurationS > 0) content.AddChild(Zones(s, info.FtpW));

        var t = Ride.Samples.Select(x => x.TimeS).ToArray();
        AddChart(content, "Puissance", "W", Ui.PowerColor, t, Ride.Samples.Select(x => (double)x.PowerW).ToArray(), s.AvgPowerW);
        AddChart(content, "Cadence", "tr/min", Ui.CadenceColor, t, Ride.Samples.Select(x => x.CadenceRpm).ToArray(), s.AvgCadenceRpm);
        // La pente n'est tracée que si elle varie ; sur le plat, le titre le dit.
        var grades = Ride.Samples.Select(x => x.GradePct).ToArray();
        bool flat = grades.All(g => Math.Abs(g - grades.FirstOrDefault()) < 0.05);
        var speed = AddChart(content, flat ? $"Vitesse · pente {grades.FirstOrDefault():0.#} % tout du long" : "Vitesse et pente",
            "km/h", Ui.SpeedColor, t, Ride.Samples.Select(x => x.SpeedKmh).ToArray(), s.AvgSpeedKmh);
        if (speed is not null && !flat)
        {
            speed.Secondary = grades;
            speed.SecondaryLabel = "pente (%)";
        }
        AddChart(content, "Fréquence cardiaque", "bpm", Ui.HeartRateColor, t, Ride.Samples.Select(x => (double)x.HeartRateBpm).ToArray(), s.AvgHeartRateBpm);

        var actions = Ui.Row();
        actions.Alignment = BoxContainer.AlignmentMode.End;
        content.AddChild(actions);
        _deleteButton = Ui.Button("Supprimer cette sortie", OnDeletePressed, 320, 72, 20);
        _deleteButton.Modulate = Ui.Danger;
        actions.AddChild(_deleteButton);
    }

    public override void _Process(double delta) =>
        _deleteButton.Text = Now <= _deleteArmedUntil ? "Toucher encore pour supprimer" : "Supprimer cette sortie";

    static void Stat(GridContainer grid, string title, string value, Color accent)
    {
        var label = Ui.Tile(grid, title, accent, 30);
        label.Text = value;
    }

    LineChart? AddChart(Container parent, string title, string unit, Color color, double[] times, double[] values, double average)
    {
        // Pas de ceinture cardio ou pas de capteur : pas de courbe plate à zéro.
        if (values.Length < 2 || values.All(v => v <= 0)) return null;
        var chart = new LineChart { Title = title, Unit = unit, LineColor = color, Times = times, Values = values, Average = average };
        parent.AddChild(chart);
        ChartCount++;
        return chart;
    }

    /// <summary>Barres horizontales : part du temps dans chaque zone de puissance.</summary>
    static Control Zones(RideSummary s, int ftpW)
    {
        var panel = Ui.Panel(Ui.Card);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        box.AddChild(Ui.Label($"Temps par zone de puissance (FTP {ftpW} W)", 20, Ui.PowerColor));
        double total = s.PowerZonesS.Sum();
        for (int z = 0; z < RideMetrics.ZoneCount; z++)
        {
            var row = Ui.Row(12);
            var name = Ui.Label($"Z{z + 1} {RideMetrics.ZoneNames[z]}", 17);
            name.CustomMinimumSize = new Vector2(240, 0);
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(name);
            double share = total > 0 ? s.PowerZonesS[z] / total : 0;
            var bar = new ProgressBar
            {
                MinValue = 0, MaxValue = 1, Value = share, ShowPercentage = false,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(0, 18), MouseFilter = MouseFilterEnum.Pass,
            };
            var fill = new StyleBoxFlat { BgColor = ZoneColor(z) };
            fill.SetCornerRadiusAll(6);
            var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.06f) };
            back.SetCornerRadiusAll(6);
            bar.AddThemeStyleboxOverride("fill", fill);
            bar.AddThemeStyleboxOverride("background", back);
            row.AddChild(bar);
            var time = Ui.Label($"{Ui.Duration(s.PowerZonesS[z])}  {share * 100:0} %", 17, Ui.Muted);
            time.CustomMinimumSize = new Vector2(150, 0);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            time.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(time);
            box.AddChild(row);
        }
        return panel;
    }

    static Color ZoneColor(int zone) => zone switch
    {
        0 => new Color(0.55f, 0.58f, 0.62f),
        1 => new Color(0.30f, 0.62f, 0.95f),
        2 => new Color(0.30f, 0.80f, 0.50f),
        3 => new Color(0.98f, 0.80f, 0.25f),
        4 => new Color(0.98f, 0.55f, 0.20f),
        5 => new Color(0.95f, 0.35f, 0.30f),
        _ => new Color(0.75f, 0.35f, 0.85f),
    };

    void OnDeletePressed()
    {
        if (Now > _deleteArmedUntil)
        {
            _deleteArmedUntil = Now + 3.0;
            return;
        }
        Store.Delete(Ride.Info.Id);
        Navigator.GoTo(this, Navigator.History);
    }

    protected override void OnBack() => Navigator.GoTo(this, JustFinished ? Navigator.Menu : Navigator.History);
}
