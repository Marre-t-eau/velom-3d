using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>
/// Fenêtre de confirmation plein écran : fond assombri qui bloque l'écran derrière, question,
/// précision, et deux grands boutons Non / Oui. Toucher le fond revient à Non.
/// </summary>
public partial class ConfirmPopup : Control
{
    internal Button YesButton { get; } = null!;
    internal Button NoButton { get; } = null!;

    public ConfirmPopup() { }

    public ConfirmPopup(string question, string detail, string yes, Action onYes, Action onNo)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        shade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) onNo(); };
        AddChild(shade);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = Ui.Panel(Ui.Card, Ui.Danger);
        panel.MouseFilter = MouseFilterEnum.Stop;
        panel.CustomMinimumSize = new Vector2(620, 0);
        center.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 22);
        panel.AddChild(box);
        box.AddChild(Ui.Label(question, 32));
        if (detail.Length > 0) box.AddChild(Ui.Label(detail, 20, Ui.Muted));
        var buttons = Ui.Row();
        box.AddChild(buttons);
        NoButton = Ui.Button("Non", onNo, 0, 88, 26);
        NoButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        buttons.AddChild(NoButton);
        YesButton = Ui.Button(yes, onYes, 0, 88, 26);
        YesButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        YesButton.AddThemeColorOverride("font_color", Ui.Danger);
        YesButton.AddThemeColorOverride("font_hover_color", Ui.Danger);
        YesButton.AddThemeColorOverride("font_pressed_color", Ui.Danger);
        buttons.AddChild(YesButton);
    }
}
