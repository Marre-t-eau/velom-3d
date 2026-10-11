using System;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>
/// Profil d'un entraînement : un rectangle par bloc (largeur = durée, hauteur = puissance,
/// couleur = zone), une pente pour les rampes, un bloc gris bas pour la puissance libre.
/// Pendant la sortie, une barre verticale marque la position et les blocs passés s'estompent.
/// </summary>
public partial class WorkoutChart : Control
{
    public Workout? Workout { get; set; }
    public int FtpW { get; set; } = 200;
    /// <summary>Position dans l'entraînement (s) ; négative = pas de curseur.</summary>
    public double PositionS { get; set; } = -1;
    /// <summary>Bloc mis en avant (édition) ; -1 = aucun.</summary>
    public int Highlight { get; set; } = -1;

    static readonly Color FreeColor = new(1, 1, 1, 0.18f);

    public WorkoutChart()
    {
        CustomMinimumSize = new Vector2(0, 120);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Pass;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }

    public override void _Draw()
    {
        var bg = new StyleBoxFlat { BgColor = Ui.Card };
        bg.SetCornerRadiusAll(14);
        DrawStyleBox(bg, new Rect2(Vector2.Zero, Size));
        if (Workout is not { Blocks.Count: > 0 } w) return;
        int total = w.TotalDurationS;
        var plot = new Rect2(14, 12, Size.X - 28, Size.Y - 24);
        if (total <= 0 || plot.Size.X < 10 || plot.Size.Y < 10) return;

        // Échelle : la plus forte consigne, et au moins 120 % de la FTP pour garder les proportions.
        double top = FtpW * 1.2;
        foreach (var b in w.Blocks)
            if (b.Kind != BlockPower.Free)
                top = Math.Max(top, Math.Max(w.ToWatts(b.Power, FtpW), b.Kind == BlockPower.Ramp ? w.ToWatts(b.PowerEnd, FtpW) : 0));
        float Y(double watts) => plot.End.Y - (float)(Math.Clamp(watts / top, 0.02, 1.0) * plot.Size.Y);
        float X(double s) => plot.Position.X + (float)(s / total) * plot.Size.X;

        double start = 0;
        for (int i = 0; i < w.Blocks.Count; i++)
        {
            var b = w.Blocks[i];
            float x0 = X(start), x1 = Math.Max(X(start + b.DurationS) - 1, x0 + 1);
            bool past = PositionS >= 0 && start + b.DurationS <= PositionS;
            float alpha = past ? 0.3f : Highlight >= 0 && Highlight != i ? 0.55f : 1f;
            if (b.Kind == BlockPower.Free)
            {
                DrawRect(new Rect2(x0, Y(FtpW * 0.45), x1 - x0, plot.End.Y - Y(FtpW * 0.45)), FreeColor with { A = FreeColor.A * alpha });
            }
            else
            {
                double p0 = w.ToWatts(b.Power, FtpW), p1 = b.Kind == BlockPower.Ramp ? w.ToWatts(b.PowerEnd, FtpW) : p0;
                var color = Ui.ZoneColor(RideMetrics.ZoneOf(Math.Max(p0, p1), FtpW)) with { A = alpha };
                DrawColoredPolygon([new(x0, plot.End.Y), new(x0, Y(p0)), new(x1, Y(p1)), new(x1, plot.End.Y)], color);
            }
            if (i == Highlight)
                DrawRect(new Rect2(x0 - 1, plot.Position.Y - 4, x1 - x0 + 2, plot.Size.Y + 6), Colors.White, false, 2f);
            start += b.DurationS;
        }
        // Repère FTP en pointillé.
        DrawDashedLine(new Vector2(plot.Position.X, Y(FtpW)), new Vector2(plot.End.X, Y(FtpW)), Ui.Muted with { A = 0.5f }, 1f, 6f);
        if (PositionS >= 0)
        {
            float x = X(Math.Min(PositionS, total));
            DrawLine(new Vector2(x, plot.Position.Y - 6), new Vector2(x, plot.End.Y + 6), Colors.White, 3f);
        }
    }
}
