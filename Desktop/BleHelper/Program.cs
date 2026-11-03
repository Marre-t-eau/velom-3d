using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Velom3d.Relay;

namespace Velom3d.BleHelper;

/// <summary>
/// Programme d'aide Bluetooth de Windows pour Velom3d. Le jeu le lance
/// (<c>--port 47802 --parent &lt;pid du jeu&gt;</c>) et s'y connecte sur 127.0.0.1 avec le protocole
/// du relais (Relay/Core) : recherche, connexion, notifications et écritures Bluetooth passent par
/// les API Windows (WinRT), les octets bruts vont au jeu, qui décode tout lui-même.
/// Il s'arrête tout seul quand le jeu se ferme.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        int port = RelayProtocol.HelperPort;
        int parent = -1;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--port") int.TryParse(args[i + 1], out port);
            if (args[i] == "--parent") int.TryParse(args[i + 1], out parent);
        }

        using var ble = new WinBle();
        // Seulement sur 127.0.0.1 : rien n'est ouvert sur le réseau, pas de question du pare-feu.
        using var server = new RelayServer("Bluetooth de ce PC", IPAddress.Loopback, port, discovery: false);
        ble.Send = server.Send;
        server.Received += m => { if (server.Problem is null) ble.Handle(m); }; // sans Bluetooth, le jeu le sait déjà
        server.ClientConnected += (_, _) => ble.Reset();
        server.ClientLost += _ => ble.Reset(); // le jeu est parti : on libère le trainer
        try
        {
            server.Problem = ble.CheckAdapterAsync().GetAwaiter().GetResult();
            server.Start();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Velom3dBleHelper : démarrage impossible sur le port {port} : {e.Message}");
            return 1;
        }

        var lastParentCheck = Stopwatch.StartNew();
        while (true)
        {
            server.Poll();
            if (lastParentCheck.ElapsedMilliseconds > 1000)
            {
                lastParentCheck.Restart();
                if (parent > 0 && !IsRunning(parent)) break;
            }
            Thread.Sleep(5);
        }
        ble.Reset();
        return 0;
    }

    static bool IsRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
