using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>
/// Couleurs et petites briques pour ce que les écrans créent eux-mêmes parce que ça dépend des données
/// (lignes de liste, boutons de choix). La mise en page fixe est dans les scènes (.tscn), et les styles
/// communs dans le thème Screens/Common/Theme.tres (fond des cartes, boutons à bascule, libellés grisés…).
/// </summary>
public static class Ui
{
    // Palette violette, la même que Theme.tres.
    public static readonly Color Card = new(0.102f, 0.078f, 0.188f);
    public static readonly Color Muted = new(0.635f, 0.612f, 0.749f);
    public static readonly Color Accent = new(0.655f, 0.545f, 0.98f);
    public static readonly Color Warning = new(1.0f, 0.72f, 0.30f);
    public static readonly Color Danger = new(0.95f, 0.38f, 0.36f);

    // Une couleur par mesure, la même partout (tuiles, graphiques).
    public static readonly Color PowerColor = new(0.98f, 0.62f, 0.20f);
    public static readonly Color CadenceColor = new(0.35f, 0.70f, 0.98f);
    public static readonly Color SpeedColor = new(0.30f, 0.85f, 0.55f);
    public static readonly Color HeartRateColor = new(0.95f, 0.36f, 0.45f);
    public static readonly Color GradeColor = new(0.70f, 0.62f, 0.95f);

    /// <summary>Couleur d'une zone de puissance (Z1 gris … Z7 violet).</summary>
    public static Color ZoneColor(int zone) => zone switch
    {
        0 => new Color(0.55f, 0.58f, 0.62f),
        1 => new Color(0.30f, 0.62f, 0.95f),
        2 => new Color(0.30f, 0.80f, 0.50f),
        3 => new Color(0.98f, 0.80f, 0.25f),
        4 => new Color(0.98f, 0.55f, 0.20f),
        5 => new Color(0.95f, 0.35f, 0.30f),
        _ => new Color(0.75f, 0.35f, 0.85f),
    };

    public static Label Label(string text, int size, Color? color = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Pass };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) label.Modulate = c;
        return label;
    }

    public static Button Button(string text, Action onPressed, int minWidth = 260, int height = 80, int fontSize = 24)
    {
        // Pass : un glissé qui commence sur un bouton fait quand même défiler la page.
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, height), MouseFilter = Control.MouseFilterEnum.Pass };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.Pressed += onPressed;
        return button;
    }

    public static HBoxContainer Row(int separation = 16)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", separation);
        return row;
    }

    /// <summary>Petit bouton à bascule pour choisir parmi des préréglages.</summary>
    public static Button Chip(Container parent, string text, Action onPressed)
    {
        var chip = Button(text, onPressed, 120, 64, 22);
        chip.ToggleMode = true;
        chip.ThemeTypeVariation = "ChoiceButton"; // violet bordé quand il est choisi (Theme.tres)
        parent.AddChild(chip);
        return chip;
    }

    /// <summary>Rangée qui passe à la ligne quand elle est pleine.</summary>
    public static HFlowContainer Flow(Container parent)
    {
        var flow = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        flow.AddThemeConstantOverride("h_separation", 12);
        flow.AddThemeConstantOverride("v_separation", 12);
        parent.AddChild(flow);
        return flow;
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Km(double meters) => (meters / 1000.0).ToString("0.00") + " km";

    static readonly string[] Days = ["dim.", "lun.", "mar.", "mer.", "jeu.", "ven.", "sam."];
    static readonly string[] Months = ["janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc."];

    /// <summary>« sam. 10 oct. 2026 · 12:05 » à l'heure du téléphone, sans dépendre des données
    /// de langue (absentes de certaines versions Android de .NET).</summary>
    public static string Date(DateTimeOffset at)
    {
        var d = at.ToLocalTime();
        return $"{Days[(int)d.DayOfWeek]} {d.Day} {Months[d.Month - 1]} {d.Year} · {d.Hour:00}:{d.Minute:00}";
    }
}
