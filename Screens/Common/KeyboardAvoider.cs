using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>
/// Remonte l'écran quand le clavier du téléphone cacherait le champ en cours de saisie : Godot ne
/// redimensionne pas la fenêtre quand le clavier s'ouvre, il passerait donc par-dessus. Ajouté à
/// chaque <see cref="Screen"/> ; l'écran revient à sa place quand le clavier se ferme.
/// </summary>
public partial class KeyboardAvoider : Node
{
    /// <summary>Espace laissé entre le bas du champ et le haut du clavier.</summary>
    const float Margin = 24;

    Control _screen = null!;
    float _shift;

    public override void _Ready() => _screen = GetParent<Control>();

    public override void _Process(double delta)
    {
        float target = Target();
        if (Math.Abs(target - _shift) < 0.5f) return;
        // Glissement rapide plutôt qu'un saut.
        _shift = Mathf.Lerp(_shift, target, (float)Math.Min(1, delta * 18));
        if (Math.Abs(target - _shift) < 0.5f) _shift = target;
        _screen.Position = new Vector2(_screen.Position.X, -_shift);
    }

    /// <summary>Décalage vers le haut pour que le champ actif soit au-dessus du clavier, 0 sinon.</summary>
    float Target()
    {
        int keyboardPx = DisplayServer.VirtualKeyboardGetHeight();
        if (keyboardPx <= 0) return 0;
        var viewport = GetViewport();
        if (viewport.GuiGetFocusOwner() is not Control field || field is not (LineEdit or TextEdit)
            || !_screen.IsAncestorOf(field) || !field.IsVisibleInTree())
            return 0;
        // Le clavier est mesuré en pixels de l'écran, l'interface en unités du canvas (étirée).
        var visible = viewport.GetVisibleRect().Size;
        float windowPx = DisplayServer.WindowGetSize().Y;
        if (windowPx <= 0) return 0;
        float keyboardTop = visible.Y - keyboardPx * visible.Y / windowPx;
        float fieldBottom = field.GetGlobalRect().End.Y + _shift; // position sans le décalage actuel
        return Math.Max(0, fieldBottom + Margin - keyboardTop);
    }
}
