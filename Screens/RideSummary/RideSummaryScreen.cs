using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Rides;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Bilan d'une sortie, juste après l'arrivée ou depuis l'historique : chiffres clés (TSS, IF,
/// puissance normalisée, calories…), temps passé dans chaque zone de puissance, et courbes de
/// puissance, cadence, vitesse avec la pente, et fréquence cardiaque. Mise en page : RideSummaryScreen.tscn ;
/// les barres de zones et les courbes dépendent des données et sont ajoutées ici.
/// </summary>
public partial class RideSummaryScreen : Screen
{
    public Ride Ride { get; set; } = null!;
    /// <summary>Ouvert à la fin d'une sortie : Retour mène au menu plutôt qu'à l'historique.</summary>
    public bool JustFinished { get; set; }
    /// <summary>FTP avant la sortie, pour annoncer celle mesurée par un test FTP.</summary>
    public int PreviousFtpW { get; set; }
    /// <summary>D'où vient la FTP mesurée (20 min ou rampe).</summary>
    public string FtpTestExplanation { get; set; } = "";

    /// <summary>Graphiques affichés (pour les tests).</summary>
    internal int ChartCount { get; private set; }
    /// <summary>Graphiques avec une cible tracée (pour les tests).</summary>
    internal int TargetCount { get; private set; }

    public override void _Ready()
    {
        var info = Ride.Info;
        var s = Ride.Summarize();

        GetNode<Label>("%Title").Text = JustFinished ? "Bravo, sortie enregistrée !" : "Bilan de la sortie";
        GetNode<Label>("%Subtitle").Text = $"{Ui.Date(info.StartedAt)}  ·  {RideHistoryScreen.ModeName(info)}  ·  FTP {info.FtpW} W"
                                           + (info.FtpTestW > 0 ? $"  ·  nouvelle FTP {info.FtpTestW} W" : "");
        var back = GetNode<Button>("%BackButton");
        back.Text = JustFinished ? "Menu" : "Retour";
        back.Pressed += OnBack;

        Stat("Duration", Ui.Duration(s.DurationS));
        Stat("Distance", Ui.Km(s.DistanceM));
        Stat("Tss", $"{s.Tss:0}");
        Stat("Intensity", $"{s.IntensityFactor:0.00}");
        Stat("AvgPower", $"{s.AvgPowerW:0} W");
        Stat("NormPower", $"{s.NormalizedPowerW:0} W");
        Stat("MaxPower", $"{s.MaxPowerW} W");
        Stat("Kcal", $"{s.Kcal:0} kcal");
        Stat("AvgCadence", s.AvgCadenceRpm > 0 ? $"{s.AvgCadenceRpm:0} tr/min" : "--");
        Stat("AvgSpeed", $"{s.AvgSpeedKmh:0.0} km/h");
        Stat("AvgHeartRate", s.AvgHeartRateBpm > 0 ? $"{s.AvgHeartRateBpm:0} bpm" : "--");
        Stat("MaxHeartRate", s.MaxHeartRateBpm > 0 ? $"{s.MaxHeartRateBpm} bpm" : "--");
        // Rapport poids/puissance avec le poids du cycliste ce jour-là ; sans poids, ces tuiles sont masquées.
        bool mass = info.RiderMassKg > 0;
        Stat("AvgPerKg", mass ? $"{Ride.PerKg(s.AvgPowerW, info):0.00}" : null);
        Stat("NormPerKg", mass ? $"{Ride.PerKg(s.NormalizedPowerW, info):0.00}" : null);
        Stat("MaxPerKg", mass ? $"{Ride.PerKg(s.MaxPowerW, info):0.00}" : null);
        Stat("Mass", mass ? $"{info.RiderMassKg:0.#} + {info.BikeMassKg:0.#} kg" : null);
        GetNode<Label>("%WorkNote").Text = $"Travail : {s.WorkKj:0} kJ. Calories estimées avec un rendement musculaire de 24 %.";

        Timeline(Ride);
        var zones = GetNode<Control>("%Zones");
        zones.Visible = s.DurationS > 0;
        if (zones.Visible) Zones(s, info.FtpW);

        var charts = GetNode<Container>("%Charts");
        var t = Ride.Samples.Select(x => x.TimeS).ToArray();
        // Cible enregistrée (consigne ERG, cadence demandée) : tracée par-dessus la mesure.
        var power = AddChart(charts, "Puissance", "W", Ui.PowerColor, t, Ride.Samples.Select(x => (double)x.PowerW).ToArray(), s.AvgPowerW);
        if (power is not null && Ride.Samples.Any(x => x.TargetPowerW > 0)) {
            power.Target = Ride.Samples.Select(x => (double)x.TargetPowerW).ToArray();
            power.Title += " · cible en blanc";
        }
        var cadence = AddChart(charts, "Cadence", "tr/min", Ui.CadenceColor, t, Ride.Samples.Select(x => x.CadenceRpm).ToArray(), s.AvgCadenceRpm);
        if (cadence is not null && Ride.Samples.Any(x => x.TargetCadenceRpm > 0)) {
            cadence.Target = Ride.Samples.Select(x => (double)x.TargetCadenceRpm).ToArray();
            cadence.Title += " · cible en blanc";
        }
        TargetCount = (power?.Target is null ? 0 : 1) + (cadence?.Target is null ? 0 : 1);
        // La pente n'est tracée que si elle varie ; sur le plat, le titre le dit.
        var grades = Ride.Samples.Select(x => x.GradePct).ToArray();
        bool flat = grades.All(g => Math.Abs(g - grades.FirstOrDefault()) < 0.05);
        var speed = AddChart(charts, flat ? $"Vitesse · pente {grades.FirstOrDefault():0.#} % tout du long" : "Vitesse et pente",
            "km/h", Ui.SpeedColor, t, Ride.Samples.Select(x => x.SpeedKmh).ToArray(), s.AvgSpeedKmh);
        if (speed is not null && !flat)
        {
            speed.Secondary = grades;
            speed.SecondaryLabel = "pente (%)";
        }
        AddChart(charts, "Fréquence cardiaque", "bpm", Ui.HeartRateColor, t, Ride.Samples.Select(x => (double)x.HeartRateBpm).ToArray(), s.AvgHeartRateBpm);

        GetNode<Button>("%DeleteButton").Pressed += OnDeletePressed;

        if (JustFinished && info.FtpTestW > 0)
        {
            int diff = info.FtpTestW - PreviousFtpW;
            string change = PreviousFtpW <= 0 ? "" : diff == 0 ? $" C'est la même qu'avant." : $" Avant : {PreviousFtpW} W ({diff:+0;-0} W).";
            Inform($"Nouvelle FTP : {info.FtpTestW} W", $"{FtpTestExplanation}{change} "
                   + "Elle est enregistrée dans les Réglages : vos entraînements en % de FTP s'y adaptent.");
        }
    }

    /// <summary>Valeur d'une tuile de la grille ; null masque la tuile.</summary>
    void Stat(string tile, string? value)
    {
        var node = GetNode<Control>($"%{tile}Tile");
        node.Visible = value is not null;
        node.GetNode<Label>("Box/Value").Text = value ?? "";
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

    /// <summary>Heures de départ et de fin, et chaque pause avec sa durée.</summary>
    void Timeline(Ride ride)
    {
        var info = ride.Info;
        string end = info.EndedAt is { } e ? $"  ·  fin {Clock(e)}" : "";
        GetNode<Label>("%Times").Text = $"Départ {Clock(info.StartedAt)}{end}";
        PausesText = ride.Pauses.Count == 0 ? "Aucune pause."
            : string.Join("\n", ride.Pauses.Select((p, i) => p.ResumedAt is { } r
                ? $"Pause {i + 1} : {Clock(p.PausedAt)} → {Clock(r)} ({Ui.Duration(p.DurationS)}), après {Ui.Duration(p.AtElapsedS)} de sortie"
                : $"Pause {i + 1} : {Clock(p.PausedAt)}, après {Ui.Duration(p.AtElapsedS)} de sortie, jusqu'à la fin"));
        GetNode<Label>("%Pauses").Text = PausesText;
        var physics = GetNode<Label>("%Physics");
        physics.Visible = info.Crr > 0;
        if (info.Crr > 0)
            physics.Text = string.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"),
                $"Résistance des pneus Crr {info.Crr:0.0000}  ·  aéro CdA {info.CdA:0.00} m²  ·  roue {info.WheelCircumferenceMm} mm");
        SensorsText = string.Join("\n", info.Sensors.Select(SensorLine));
        var sensors = GetNode<Label>("%Sensors");
        sensors.Visible = SensorsText != "";
        sensors.Text = "Capteurs\n" + SensorsText;
    }

    /// <summary>« 1. KICKR CORE · Home trainer · Wahoo KICKR CORE · 0 à 2000 W ».</summary>
    static string SensorLine(RideSensor sensor)
    {
        var sp = sensor.Specs;
        var parts = new List<string> { $"{sensor.Index}. {sensor.Name}", DeviceList.KindLabel(sensor.Kind) };
        string model = string.Join(" ", new[] { sp.Manufacturer, sp.Model }.Where(x => !string.IsNullOrEmpty(x)));
        if (model != "") parts.Add(model);
        if (sp.PowerMaxW is { } max) parts.Add($"{sp.PowerMinW ?? 0} à {max} W");
        if (sp.ResistanceMax is { } rmax) parts.Add($"résistance {sp.ResistanceMin ?? 0:0.#} à {rmax:0.#}");
        if (sp.InclineMaxPct is { } imax) parts.Add($"pente {sp.InclineMinPct ?? 0:0.#} à {imax:0.#} %");
        if (sp.Location is { Length: > 0 } loc) parts.Add(loc);
        return string.Join("  ·  ", parts);
    }

    /// <summary>Texte des capteurs affiché (pour les tests).</summary>
    internal static string SensorsText { get; private set; } = "";

    /// <summary>Texte des pauses affiché (pour les tests).</summary>
    internal static string PausesText { get; private set; } = "";

    static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Barres horizontales : part du temps dans chaque zone de puissance.</summary>
    void Zones(RideSummary s, int ftpW)
    {
        var box = GetNode<Container>("%ZoneRows");
        GetNode<Label>("%ZonesTitle").Text = $"Temps par zone de puissance (FTP {ftpW} W)";
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
            // Fond du thème, remplissage à la couleur de la zone.
            var fill = new StyleBoxFlat { BgColor = Ui.ZoneColor(z) };
            fill.SetCornerRadiusAll(6);
            bar.AddThemeStyleboxOverride("fill", fill);
            row.AddChild(bar);
            var time = Ui.Label($"{Ui.Duration(s.PowerZonesS[z])}  {share * 100:0} %", 17, Ui.Muted);
            time.CustomMinimumSize = new Vector2(150, 0);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            time.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(time);
            box.AddChild(row);
        }
    }

    internal void OnDeletePressed() => Confirm("Supprimer cette sortie ?", "Ses mesures et son bilan seront effacés de l'appareil.", "Oui, supprimer", () =>
    {
        Store.Delete(Ride.Info.Id);
        Navigator.GoTo(this, Navigator.History);
    });

    protected override void OnBack() => Navigator.GoTo(this, JustFinished ? Navigator.Menu : Navigator.History);
}
