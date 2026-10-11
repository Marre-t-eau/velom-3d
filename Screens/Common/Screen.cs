using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Écran plein page : le bouton Retour d'Android (et Échap sur ordinateur) appelle <see cref="OnBack"/>.</summary>
public abstract partial class Screen : Control
{
    /// <summary>Sorties et profil, dans le dossier de l'appli sur l'appareil. Remplaçable par les tests.</summary>
    public static RideStore Store
    {
        get => _store ??= new RideStore(ProjectSettings.GlobalizePath("user://rides"));
        set => _store = value;
    }
    static RideStore? _store;

    protected abstract void OnBack();

    public override void _Notification(int what)
    {
        if (what == NotificationWMGoBackRequest) OnBack();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        OnBack();
    }
}
