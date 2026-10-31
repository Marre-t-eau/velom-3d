using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>
/// Fenêtre de confirmation plein écran (ConfirmPopup.tscn) : fond assombri qui bloque l'écran derrière,
/// question, précision, et deux grands boutons Non / Oui. Toucher le fond revient à Non.
/// Sans <c>onNo</c>, c'est une simple information avec un seul bouton.
/// </summary>
public partial class ConfirmPopup : Control
{
    public const string ScenePath = "res://Screens/Common/ConfirmPopup.tscn";

    internal Button YesButton => GetNode<Button>("%YesButton");
    internal Button NoButton => GetNode<Button>("%NoButton");

    /// <summary>Remplit la fenêtre ; à appeler avant de l'ajouter à l'écran.</summary>
    public void Setup(string question, string detail, string yes, Action onYes, Action? onNo)
    {
        bool info = onNo is null;
        onNo ??= onYes;
        GetNode<ColorRect>("%Shade").GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) onNo(); };
        GetNode<PanelContainer>("%Panel").ThemeTypeVariation = info ? "AccentPanel" : "DangerPanel";
        GetNode<Label>("%Question").Text = question;
        var details = GetNode<Label>("%Detail");
        details.Text = detail;
        details.Visible = detail.Length > 0;
        NoButton.Pressed += onNo;
        NoButton.Visible = !info;
        YesButton.Text = yes;
        YesButton.Pressed += onYes;
        var color = info ? Ui.Accent : Ui.Danger;
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color" })
            YesButton.AddThemeColorOverride(name, color);
    }
}
