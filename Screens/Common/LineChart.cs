using System;
using System.Collections.Generic;
using Godot;

namespace Velom3d.Screens;

/// <summary>
/// Courbe d'une mesure dans le temps, dessinée à la main (pas de dépendance) : titre, valeurs
/// min/max sur l'axe, temps en bas, ligne pointillée de la moyenne, et une courbe secondaire
/// optionnelle (la pente) avec sa propre échelle à droite.
/// </summary>
public partial class LineChart : Control
{
    public string Title { get; set; } = "";
    public string Unit { get; set; } = "";
    public Color LineColor { get; set; } = Colors.White;
    public double[] Times { get; set; } = [];
    public double[] Values { get; set; } = [];
    public double? Average { get; set; }
    /// <summary>Courbe secondaire (ex. pente en %), échelle à droite.</summary>
    public double[]? Secondary { get; set; }
    public string SecondaryLabel { get; set; } = "";
    public Color SecondaryColor { get; set; } = Ui.GradeColor;
    /// <summary>Cible (consigne ERG, cadence demandée), même échelle que la mesure ; 0 = pas de cible à cet instant.</summary>
    public double[]? Target { get; set; }

    const float Left = 64, Right = 64, Top = 44, Bottom = 34;

    public LineChart()
    {
        CustomMinimumSize = new Vector2(0, 240);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Pass;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawStyleBox(BackgroundStyle(), new Rect2(Vector2.Zero, Size));
        DrawString(font, new Vector2(20, 30), Title, HorizontalAlignment.Left, -1, 20, LineColor);
        if (Average is { } avg)
            DrawString(font, new Vector2(Size.X - 20 - 260, 30), $"moyenne {avg:0} {Unit}", HorizontalAlignment.Right, 260, 18, Ui.Muted);

        var plot = new Rect2(Left, Top, Size.X - Left - Right, Size.Y - Top - Bottom);
        if (plot.Size.X < 20 || plot.Size.Y < 20 || Values.Length < 2 || Times.Length != Values.Length) return;

        double tMax = Math.Max(1.0, Times[^1]);
        bool hasTarget = Target is { Length: > 1 } tgt && tgt.Length == Values.Length;
        var (lo, hi) = Range(hasTarget ? [.. Values, .. Target!] : Values, true);
        DrawAxis(font, plot, lo, hi, tMax);

        if (Secondary is { Length: > 1 } sec && sec.Length == Values.Length)
        {
            var (slo, shi) = Range(sec, false);
            DrawSeries(plot, sec, slo, shi, tMax, SecondaryColor with { A = 0.8f }, 2f);
            DrawString(font, new Vector2(plot.End.X + 6, plot.Position.Y + 14), $"{shi:0.#}", HorizontalAlignment.Left, -1, 14, SecondaryColor);
            DrawString(font, new Vector2(plot.End.X + 6, plot.End.Y), $"{slo:0.#}", HorizontalAlignment.Left, -1, 14, SecondaryColor);
            DrawString(font, new Vector2(plot.End.X - 160, plot.Position.Y - 6), SecondaryLabel, HorizontalAlignment.Right, 160, 14, SecondaryColor);
        }

        if (Average is { } a && hi > lo)
        {
            float y = Y(plot, a, lo, hi);
            DrawDashedLine(new Vector2(plot.Position.X, y), new Vector2(plot.End.X, y), Ui.Muted, 1.5f, 8f);
        }
        DrawSeries(plot, Values, lo, hi, tMax, LineColor, 3f);
        if (hasTarget)
        {
            DrawTarget(plot, Target!, lo, hi, tMax);
        }
    }

    /// <summary>La cible en traits blancs, en marches (pas de moyenne : un changement de bloc reste net) ;
    /// interrompue là où il n'y a pas de cible.</summary>
    void DrawTarget(Rect2 plot, double[] target, double lo, double hi, double tMax)
    {
        int step = Math.Max(1, target.Length / Math.Max(1, (int)plot.Size.X));
        var color = new Color(1, 1, 1, 0.85f);
        for (int i = 0; i + step < target.Length; i += step)
        {
            if (target[i] <= 0) continue;
            float x0 = plot.Position.X + (float)(Times[i] / tMax) * plot.Size.X;
            float x1 = plot.Position.X + (float)(Times[i + step] / tMax) * plot.Size.X;
            float y = Y(plot, target[i], lo, hi);
            DrawLine(new Vector2(x0, y), new Vector2(x1, y), color, 2f);
            if (target[i + step] > 0) DrawLine(new Vector2(x1, y), new Vector2(x1, Y(plot, target[i + step], lo, hi)), color, 2f);
        }
    }

    void DrawAxis(Font font, Rect2 plot, double lo, double hi, double tMax)
    {
        var grid = new Color(1, 1, 1, 0.08f);
        for (int i = 0; i <= 2; i++)
        {
            float y = plot.Position.Y + plot.Size.Y * i / 2f;
            DrawLine(new Vector2(plot.Position.X, y), new Vector2(plot.End.X, y), grid, 1f);
            double v = hi - (hi - lo) * i / 2.0;
            DrawString(font, new Vector2(4, y + 6), $"{v:0}", HorizontalAlignment.Right, Left - 12, 14, Ui.Muted);
        }
        for (int i = 0; i <= 4; i++)
        {
            float x = plot.Position.X + plot.Size.X * i / 4f;
            DrawString(font, new Vector2(x - 40, Size.Y - 8), Ui.Duration(tMax * i / 4.0), HorizontalAlignment.Center, 80, 14, Ui.Muted);
        }
    }

    /// <summary>Une valeur par colonne de pixels au plus (moyenne du paquet) : une sortie de
    /// trois heures reste lisible et rapide à dessiner.</summary>
    void DrawSeries(Rect2 plot, double[] values, double lo, double hi, double tMax, Color color, float width)
    {
        int buckets = Math.Max(2, Math.Min(values.Length, (int)plot.Size.X));
        var points = new List<Vector2>(buckets);
        for (int b = 0; b < buckets; b++)
        {
            int start = (int)((long)b * values.Length / buckets);
            int end = Math.Max(start + 1, (int)((long)(b + 1) * values.Length / buckets));
            double sum = 0, t = 0;
            for (int i = start; i < end; i++) { sum += values[i]; t += Times[i]; }
            int n = end - start;
            float x = plot.Position.X + (float)(t / n / tMax) * plot.Size.X;
            points.Add(new Vector2(x, Y(plot, sum / n, lo, hi)));
        }
        DrawPolyline(points.ToArray(), color, width, true);
    }

    static float Y(Rect2 plot, double v, double lo, double hi) =>
        plot.End.Y - (float)((v - lo) / Math.Max(1e-9, hi - lo)) * plot.Size.Y;

    /// <summary>Échelle arrondie ; les mesures positives partent de 0 pour ne pas exagérer les écarts.</summary>
    static (double Lo, double Hi) Range(double[] values, bool fromZero)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in values) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (fromZero && lo >= 0) lo = 0;
        if (hi - lo < 1e-6) { hi += 1; lo -= fromZero && lo == 0 ? 0 : 1; }
        double step = hi - lo > 50 ? 10 : 1;
        return (Math.Floor(lo / step) * step, Math.Ceiling(hi / step) * step);
    }

    static StyleBoxFlat BackgroundStyle()
    {
        var style = new StyleBoxFlat { BgColor = Ui.Card };
        style.SetCornerRadiusAll(14);
        return style;
    }
}
