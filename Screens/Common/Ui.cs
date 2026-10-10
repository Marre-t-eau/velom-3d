using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>Couleurs et briques d'interface communes aux écrans, pensées pour le tactile.</summary>
public static class Ui
{
    public static readonly Color Background = new(0.07f, 0.08f, 0.10f);
    public static readonly Color Card = new(0.13f, 0.15f, 0.18f);
    public static readonly Color CardActive = new(0.10f, 0.24f, 0.17f);
    public static readonly Color Muted = new(0.62f, 0.66f, 0.72f);
    public static readonly Color Accent = new(0.22f, 0.78f, 0.47f);
    public static readonly Color Warning = new(1.0f, 0.72f, 0.30f);
    public static readonly Color Danger = new(0.95f, 0.38f, 0.36f);

    // Une couleur par mesure, la même partout (tuiles, graphiques).
    public static readonly Color PowerColor = new(0.98f, 0.62f, 0.20f);
    public static readonly Color CadenceColor = new(0.35f, 0.70f, 0.98f);
    public static readonly Color SpeedColor = new(0.30f, 0.85f, 0.55f);
    public static readonly Color HeartRateColor = new(0.95f, 0.36f, 0.45f);
    public static readonly Color GradeColor = new(0.70f, 0.62f, 0.95f);

    /// <summary>Fond plein écran et colonne principale avec marges.</summary>
    public static VBoxContainer BuildPage(Control screen, int separation = 18)
    {
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var bg = new ColorRect { Color = Background };
        bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 32);
        screen.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", separation);
        margin.AddChild(root);
        return root;
    }

    /// <summary>Zone qui défile au doigt, verticalement.</summary>
    public static VBoxContainer AddScroll(Container parent, int separation = 16)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ScrollDeadzone = 12,
        };
        parent.AddChild(scroll);
        var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        content.AddThemeConstantOverride("separation", separation);
        scroll.AddChild(content);
        return content;
    }

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

    /// <summary>Bouton à bascule bien visible : vert bordé quand il est choisi.</summary>
    public static void MakeSelectable(Button button)
    {
        button.ToggleMode = true;
        StyleBoxFlat Style(Color bg, Color? border, float lighten = 0f)
        {
            var s = new StyleBoxFlat { BgColor = bg.Lightened(lighten) };
            s.SetCornerRadiusAll(14);
            if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(3); }
            return s;
        }
        button.AddThemeStyleboxOverride("normal", Style(Card, null));
        button.AddThemeStyleboxOverride("hover", Style(Card, null, 0.05f));
        button.AddThemeStyleboxOverride("pressed", Style(CardActive, Accent));
        button.AddThemeStyleboxOverride("hover_pressed", Style(CardActive, Accent, 0.05f));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }

    public static HBoxContainer Row(int separation = 16)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", separation);
        return row;
    }

    public static PanelContainer Panel(Color color, Color? border = null)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        SetPanelColor(panel, color, border);
        return panel;
    }

    public static void SetPanelColor(PanelContainer panel, Color color, Color? border = null)
    {
        var style = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 14, ContentMarginBottom = 14 };
        style.SetCornerRadiusAll(14);
        if (border is { } b) { style.BorderColor = b; style.SetBorderWidthAll(2); }
        panel.AddThemeStyleboxOverride("panel", style);
    }

    /// <summary>Tuile « titre / grande valeur » ; renvoie le label de valeur à mettre à jour.</summary>
    public static Label Tile(Container parent, string title, Color accent, int valueSize = 36)
    {
        var panel = Panel(Card);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddChild(box);
        box.AddChild(Label(title, 18, accent));
        var value = Label("--", valueSize);
        value.AutowrapMode = TextServer.AutowrapMode.Off;
        box.AddChild(value);
        parent.AddChild(panel);
        return value;
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
