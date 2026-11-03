using System;
using System.Linq;
using Godot;

namespace Velom3d.Sensors;

/// <summary>
/// Choix du transport des capteurs. Deux implémentations de <see cref="SensorBackend"/> :
/// <list type="bullet">
/// <item><see cref="NativeBleBackend"/> : le vrai Bluetooth du téléphone (plugin Android).</item>
/// <item><see cref="RelayBackend"/> : le Bluetooth de Windows (programme d'aide, voir <see cref="WindowsBle"/>),
/// ou celui d'un téléphone en mode relais sur le réseau local (choisi dans l'écran de connexion).</item>
/// <item><see cref="SimulatedBackend"/> : mode debug, un faux home trainer, des pédales et une
/// ceinture cardio qui génèrent puissance, cadence et fréquence cardiaque, et obéissent aux
/// commandes ERG et simulation. Pour développer sur ordinateur, sans vélo.</item>
/// </list>
/// Le mode debug s'active avec la variable d'environnement <c>VELOM3D_DEBUG_SENSORS=1</c>
/// ou l'argument <c>--debug-sensors</c> (après <c>--</c> sur la ligne de commande Godot, ou dans
/// Projet > Paramètres > Éditeur > Run > Main Run Args). Sinon : Bluetooth natif s'il existe (Android), Bluetooth de Windows sur PC.
/// </summary>
public static class SensorBackends
{
    public const string DebugEnvVar = "VELOM3D_DEBUG_SENSORS";
    public const string DebugArg = "--debug-sensors";

    public static bool DebugRequested =>
        IsOn(OS.GetEnvironment(DebugEnvVar))
        || OS.GetCmdlineUserArgs().Contains(DebugArg)
        || OS.GetCmdlineArgs().Contains(DebugArg);

    /// <summary>Transport à utiliser, ou null si aucun n'est disponible ici.</summary>
    public static SensorBackend? Create()
    {
        if (DebugRequested) return new SimulatedBackend { VaryEffort = true };
        if (NativeBleBackend.IsAvailable) return new NativeBleBackend();
        return WindowsBle.Create();
    }

    static bool IsOn(string value) =>
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "oui" or "on";
}
