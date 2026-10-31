using System;
using Godot;

namespace Velom3d.Screens;

/// <summary>Passage d'un écran à l'autre.</summary>
public static class Navigator
{
    public const string Connection = "res://Screens/DeviceConnection/DeviceConnectionScreen.tscn";
    public const string Menu = "res://Screens/MainMenu/MainMenuScreen.tscn";
    public const string FreeRide = "res://Screens/FreeRide/FreeRideScreen.tscn";
    public const string History = "res://Screens/RideHistory/RideHistoryScreen.tscn";
    public const string Summary = "res://Screens/RideSummary/RideSummaryScreen.tscn";
    public const string Settings = "res://Screens/Settings/SettingsScreen.tscn";
    public const string Workouts = "res://Screens/Workouts/WorkoutListScreen.tscn";
    public const string WorkoutEditor = "res://Screens/WorkoutEditor/WorkoutEditorScreen.tscn";
    public const string Relay = "res://Screens/Relay/RelayScreen.tscn";

    /// <summary>Remplace le changement de scène (tests : l'écran de test reste en place).</summary>
    public static Action<Node>? Override { get; set; }

    public static T Create<T>(string path) where T : Node => GD.Load<PackedScene>(path).Instantiate<T>();

    public static void GoTo(Node from, string path) => GoTo(from, Create<Node>(path));

    public static void GoTo(Node from, Node screen)
    {
        if (Override is { } o) { o(screen); return; }
        // Différé : on ne libère pas l'écran courant au milieu de son propre signal.
        Callable.From(() => from.GetTree().ChangeSceneToNode(screen)).CallDeferred();
    }
}
