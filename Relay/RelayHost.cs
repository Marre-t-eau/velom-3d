using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using Velom3d.Sensors;

namespace Velom3d.Relay;

/// <summary>
/// Mode relais du téléphone : prête son Bluetooth à un PC du même réseau (RelayScreen).
///
/// Les capteurs se connectent sur le téléphone, comme d'habitude (page de connexion), ou depuis
/// le PC : chaque demande du PC passe par le <see cref="SensorHub"/> du téléphone, qui garde ainsi
/// sa liste d'appareils à jour (et affiche les mesures), et chaque octet reçu des capteurs part
/// tel quel au PC. À l'arrivée du PC, les capteurs déjà connectés lui sont annoncés (avec leur
/// fiche). Activer ou arrêter le relais ne déconnecte rien. Quand le PC s'en va, le trainer
/// repasse en simulation sur le plat : il n'est pas laissé en ERG sans pilote.
///
/// Le relais vit sous l'autoload Sensors (<see cref="Start"/>, <see cref="Stop"/>) : il continue
/// quand on change d'écran sur le téléphone.
/// </summary>
public partial class RelayHost : Node
{
    /// <summary>Le relais en marche, ou null.</summary>
    public static RelayHost? Current { get; private set; }

    /// <summary>Démarre le relais s'il ne tourne pas déjà.</summary>
    public static RelayHost Start()
    {
        if (Current is { } running && IsInstanceValid(running)) return running;
        Current = new RelayHost { Name = "RelayHost" };
        SensorHub.Instance.AddChild(Current);
        return Current;
    }

    public static void Stop()
    {
        if (Current is { } running && IsInstanceValid(running)) running.QueueFree();
        Current = null;
    }

    RelayServer? _server;
    SensorBackend? _backend;

    public bool IsRunning => _server is not null;
    /// <summary>Le relais n'a pas pu démarrer (port déjà pris…), ou null.</summary>
    public string? StartError { get; private set; }
    public string? DiscoveryProblem => _server?.DiscoveryProblem;
    /// <summary>Nom du PC connecté, ou null.</summary>
    public string? ClientName { get; private set; }
    /// <summary>Dernière raison de départ du PC (à afficher), ou null.</summary>
    public string? LastLoss { get; private set; }
    public int Port => _server?.Port ?? RelayProtocol.TcpPort;

    /// <summary>Un PC est arrivé ou parti.</summary>
    public event Action? Changed;

    static SensorHub Hub => SensorHub.Instance;

    public override void _Ready()
    {
        if (Hub.Backend is null && SensorBackends.Create() is { } created) Hub.UseBackend(created);
        _backend = Hub.Backend;
        // Le PC peut piloter le trainer pendant une heure : l'écran ne doit pas se mettre en veille.
        DisplayServer.ScreenSetKeepOn(true);
        var server = new RelayServer(DeviceName(), IPAddress.Any)
        {
            Problem = _backend is null ? "Le Bluetooth n'est pas disponible dans cette version de l'appli sur le téléphone." : null,
        };
        try
        {
            server.Start();
        }
        catch (SocketException e)
        {
            StartError = $"Impossible d'ouvrir le port {RelayProtocol.TcpPort} ({e.Message}).";
            server.Dispose();
            Changed?.Invoke();
            return;
        }
        _server = server;
        server.ClientConnected += (name, address) =>
        {
            ClientName = name;
            LastLoss = null;
            AnnounceConnected(); // capteurs déjà connectés sur le téléphone
            Changed?.Invoke();
        };
        server.ClientLost += reason =>
        {
            ClientName = null;
            LastLoss = reason;
            ReleaseTrainer();
            Changed?.Invoke();
        };
        server.Received += OnCommand;

        if (_backend is not null)
        {
            _backend.DeviceFound += OnFound;
            _backend.ScanFinished += OnScanFinished;
            _backend.Notified += OnNotified;
            _backend.Read += OnRead;
            _backend.Error += OnError;
        }
        Hub.DeviceConnected += OnConnected;
        Hub.DeviceDisconnected += OnDisconnected;
    }

    public override void _Process(double delta) => _server?.Poll();

    public override void _ExitTree()
    {
        if (_backend is not null && IsInstanceValid(_backend))
        {
            _backend.DeviceFound -= OnFound;
            _backend.ScanFinished -= OnScanFinished;
            _backend.Notified -= OnNotified;
            _backend.Read -= OnRead;
            _backend.Error -= OnError;
        }
        Hub.DeviceConnected -= OnConnected;
        Hub.DeviceDisconnected -= OnDisconnected;
        if (_server?.Client is not null) ReleaseTrainer();
        _server?.Dispose();
        _server = null;
        DisplayServer.ScreenSetKeepOn(false);
        if (Current == this) Current = null;
    }

    /// <summary>Adresses IP du téléphone sur le réseau local, à saisir sur le PC si la découverte échoue.</summary>
    public static List<string> LocalAddresses() =>
        IP.GetLocalAddresses()
            .Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(ip) && !a.StartsWith("169.254."))
            .OrderBy(a => a.StartsWith("192.168.") ? 0 : a.StartsWith("10.") ? 1 : 2)
            .ToList();

    static string DeviceName()
    {
        var model = OS.GetModelName();
        return string.IsNullOrWhiteSpace(model) || model == "GenericDevice" ? System.Environment.MachineName : model;
    }

    void OnCommand(RelayMessage m)
    {
        string address = m.Address ?? "";
        switch (m.T)
        {
            case RelayOp.Scan:
                Hub.StartScan(m.Number is > 0 and < 120 ? m.Number.Value : 10.0);
                break;
            case RelayOp.StopScan:
                Hub.StopScan();
                break;
            case RelayOp.Connect:
                var device = Hub.Devices.Get(address);
                if (device is { State: LinkState.Connected })
                    _server?.Send(RelayMessage.Connected(address, device.Services));
                else if (device is null)
                {
                    _server?.Send(RelayMessage.Error("Le téléphone ne voit pas cet appareil : relancez la recherche."));
                    _server?.Send(RelayMessage.Disconnected(address));
                }
                else Hub.ConnectDevice(address);
                break;
            case RelayOp.Disconnect:
                Hub.DisconnectDevice(address);
                break;
            case RelayOp.Write when m.Char is not null && m.Data is not null:
                if (Hub.Devices.Get(address) is { State: LinkState.Connected }) Hub.Backend?.Write(address, m.Char, m.Data);
                break;
        }
    }

    void OnFound(string address, string name, string[] services, int rssi) =>
        _server?.Send(RelayMessage.Found(address, name, services, rssi));
    void OnScanFinished() => _server?.Send(RelayMessage.ScanDone());
    void OnNotified(string address, string charUuid, byte[] data) => _server?.Send(RelayMessage.Notify(address, charUuid, data));
    void OnRead(string address, string charUuid, byte[] data) => _server?.Send(RelayMessage.Read(address, charUuid, data));
    void OnError(string message) => _server?.Send(RelayMessage.Error(message));
    void OnConnected(string address, string[] services)
    {
        if (_server is null) return;
        // Connecté depuis le téléphone : le PC ne l'a peut-être pas vu au scan.
        if (Hub.Devices.Get(address) is { } d) _server.Send(RelayMessage.Found(address, d.Name, d.Services, d.Rssi));
        _server.Send(RelayMessage.Connected(address, services));
    }
    void OnDisconnected(string address) => _server?.Send(RelayMessage.Disconnected(address));

    /// <summary>Annonce au PC qui arrive les capteurs déjà connectés, avec leur fiche.</summary>
    void AnnounceConnected()
    {
        if (_server is null) return;
        foreach (var d in Hub.Devices.Sorted)
        {
            if (d.State != LinkState.Connected) continue;
            _server.Send(RelayMessage.Found(d.Address, d.Name, d.Services, d.Rssi));
            foreach (var (charUuid, data) in Hub.RawReads(d.Address)) _server.Send(RelayMessage.Read(d.Address, charUuid, data));
            _server.Send(RelayMessage.Connected(d.Address, d.Services));
        }
    }

    /// <summary>Le PC est parti : le trainer quitte l'ERG (plat), les capteurs restent connectés.</summary>
    static void ReleaseTrainer()
    {
        Hub.StopScan();
        if (Hub.TrainerAddress != "") Hub.EnterSimulation(0.0);
    }
}
