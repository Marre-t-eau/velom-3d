using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>
/// Fenêtre de confirmation plein écran : fond assombri qui bloque l'écran derrière, question,
/// précision, et deux grands boutons Non / Oui. Toucher le fond revient à Non.
/// Sans <c>onNo</c>, c'est une simple information avec un seul bouton.
/// </summary>
public partial class ConfirmPopup : Control
{
    internal Button YesButton { get; } = null!;
    internal Button NoButton { get; } = null!;

    public ConfirmPopup() { }

    public ConfirmPopup(string question, string detail, string yes, Action onYes, Action? onNo)
    {
        bool info = onNo is null;
        onNo ??= onYes;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        shade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) onNo(); };
        AddChild(shade);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = Ui.Panel(Ui.Card, info ? Ui.Accent : Ui.Danger);
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
        NoButton.Visible = !info;
        buttons.AddChild(NoButton);
        YesButton = Ui.Button(yes, onYes, 0, 88, 26);
        YesButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var color = info ? Ui.Accent : Ui.Danger;
        YesButton.AddThemeColorOverride("font_color", color);
        YesButton.AddThemeColorOverride("font_hover_color", color);
        YesButton.AddThemeColorOverride("font_pressed_color", color);
        buttons.AddChild(YesButton);
    }
}
