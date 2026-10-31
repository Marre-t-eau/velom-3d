using System;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>Écran plein page, dont la mise en page est dans sa scène (.tscn) : le bouton Retour d'Android (et Échap sur ordinateur) appelle <see cref="OnBack"/>,
/// ou ferme la fenêtre de confirmation ouverte.</summary>
public abstract partial class Screen : Control
{
    /// <summary>Sorties et profil, dans le dossier de l'appli sur l'appareil. Remplaçable par les tests.</summary>
    public static RideStore Store
    {
        get => _store ??= new RideStore(ProjectSettings.GlobalizePath("user://rides"));
        set => _store = value;
    }
    static RideStore? _store;

    /// <summary>Entraînements structurés enregistrés sur l'appareil. Remplaçable par les tests.</summary>
    public static WorkoutStore Workouts
    {
        get => _workouts ??= new WorkoutStore(ProjectSettings.GlobalizePath("user://workouts"));
        set => _workouts = value;
    }
    static WorkoutStore? _workouts;

    protected abstract void OnBack();

    /// <summary>Fenêtre de confirmation ouverte (pour les tests).</summary>
    internal ConfirmPopup? Popup { get; private set; }

    /// <summary>Fenêtre « question ? Non / Oui » par-dessus l'écran ; <paramref name="onYes"/> n'est appelé que sur Oui.</summary>
    protected void Confirm(string question, string detail, string yes, Action onYes)
    {
        Popup?.QueueFree();
        Popup = Navigator.Create<ConfirmPopup>(ConfirmPopup.ScenePath);
        Popup.Setup(question, detail, yes, () => { ClosePopup(); onYes(); }, ClosePopup);
        AddChild(Popup);
    }

    /// <summary>Fenêtre d'information avec un seul bouton.</summary>
    protected void Inform(string title, string detail, string ok = "OK")
    {
        Popup?.QueueFree();
        Popup = Navigator.Create<ConfirmPopup>(ConfirmPopup.ScenePath);
        Popup.Setup(title, detail, ok, ClosePopup, null);
        AddChild(Popup);
    }

    void ClosePopup()
    {
        Popup?.QueueFree();
        Popup = null;
    }

    void Back()
    {
        if (Popup is not null) ClosePopup();
        else OnBack();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMGoBackRequest) Back();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        Back();
    }
}
