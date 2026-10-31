using Godot;
using Velom3d.Relay;

namespace Velom3d.Sensors;

/// <summary>
/// Bluetooth du PC sous Windows. Godot n'a pas accès au Bluetooth de Windows depuis le C# du jeu
/// (il faut les API WinRT, réservées aux programmes compilés pour Windows) : un petit programme
/// d'aide, Desktop/BleHelper (Velom3dBleHelper.exe, copié à côté du jeu à l'export), parle au
/// Bluetooth et prête ses octets au jeu avec le même protocole que le relais du téléphone, sur
/// 127.0.0.1 (rien n'est ouvert sur le réseau). Le jeu le lance, et il s'arrête avec le jeu.
/// </summary>
public static class WindowsBle
{
    public const string HelperExe = "Velom3dBleHelper.exe";
    const string HelperProject = "res://Desktop/BleHelper/Velom3d.BleHelper.csproj";
    const string HelperPublished = "res://Desktop/BleHelper/publish/" + HelperExe;

    static int _pid = -1;

    public static bool IsWindows => OS.GetName() == "Windows";

    /// <summary>Transport vers le programme d'aide (lancé si besoin), ou null hors Windows.</summary>
    public static RelayBackend? Create()
    {
        if (!IsWindows) return null;
        bool slow = false;
        if (_pid <= 0 || !OS.IsProcessRunning(_pid))
        {
            _pid = Launch(out slow);
            if (_pid <= 0) return null;
        }
        // Avec « dotnet run » (depuis l'éditeur), la première compilation prend du temps.
        return new RelayBackend("127.0.0.1", RelayProtocol.HelperPort, "Bluetooth de ce PC", isLocalHelper: true,
            connectTimeoutS: slow ? 90.0 : 10.0);
    }

    /// <summary>Arrête le programme d'aide (il s'arrête aussi seul quand le jeu se ferme).</summary>
    public static void Stop()
    {
        if (_pid > 0 && OS.IsProcessRunning(_pid)) OS.Kill(_pid);
        _pid = -1;
    }

    static int Launch(out bool slow)
    {
        slow = false;
        string[] args = ["--port", RelayProtocol.HelperPort.ToString(), "--parent", OS.GetProcessId().ToString()];
        var exported = OS.GetExecutablePath().GetBaseDir().PathJoin(HelperExe);
        foreach (var exe in new[] { exported, ProjectSettings.GlobalizePath(HelperPublished) })
            if (FileAccess.FileExists(exe)) return OS.CreateProcess(exe, args);

        // Depuis l'éditeur, sans programme d'aide publié : on le compile et le lance avec le SDK .NET.
        var project = ProjectSettings.GlobalizePath(HelperProject);
        if (!OS.HasFeature("editor") || !FileAccess.FileExists(project))
        {
            GD.PushWarning($"{HelperExe} introuvable à côté du jeu : pas de Bluetooth direct sur ce PC (le relais par téléphone reste possible).");
            return -1;
        }
        slow = true;
        return OS.CreateProcess("dotnet", ["run", "--project", project, "-c", "Release", "--", .. args]);
    }
}
