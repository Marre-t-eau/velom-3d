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
/// Le PC pilote tout (recherche, connexion, commandes du trainer) comme s'il avait le Bluetooth :
/// chaque demande passe par le <see cref="SensorHub"/> du téléphone, qui garde ainsi sa liste
/// d'appareils à jour (et affiche les mesures), et chaque octet reçu des capteurs part tel quel
/// au PC. Quand le PC s'en va, les capteurs sont déconnectés : le trainer n'est pas laissé en ERG.
/// </summary>
public partial class RelayHost : Node
{
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
        ReleaseDevices();
        _backend = Hub.Backend;
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
            return;
        }
        _server = server;
        server.ClientConnected += (name, address) =>
        {
            ClientName = name;
            LastLoss = null;
            ReleaseDevices(); // le PC commence avec une liste propre
            Changed?.Invoke();
        };
        server.ClientLost += reason =>
        {
            ClientName = null;
            LastLoss = reason;
            ReleaseDevices();
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
        _server?.Dispose();
        _server = null;
        ReleaseDevices();
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
    void OnConnected(string address, string[] services) => _server?.Send(RelayMessage.Connected(address, services));
    void OnDisconnected(string address) => _server?.Send(RelayMessage.Disconnected(address));

    /// <summary>Arrête la recherche et déconnecte tout (un trainer ne reste pas en ERG sans pilote).</summary>
    static void ReleaseDevices()
    {
        Hub.StopScan();
        foreach (var d in Hub.Devices.Sorted)
            if (d.State != LinkState.Available) Hub.DisconnectDevice(d.Address);
    }
}
