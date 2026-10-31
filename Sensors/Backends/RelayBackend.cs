using System.Collections.Generic;
using Godot;
using Velom3d.Relay;

namespace Velom3d.Sensors;

/// <summary>
/// Le Bluetooth d'un autre programme, par le réseau (protocole dans Relay/Core/RelayProtocol.cs) :
/// <list type="bullet">
/// <item>le téléphone en mode relais, sur le même réseau Wi-Fi que le PC ;</item>
/// <item>le programme d'aide Bluetooth de Windows (Desktop/BleHelper), sur 127.0.0.1.</item>
/// </list>
/// Le relais transporte les octets bruts : décodage, fusion et pilotage FTMS restent ceux du jeu.
/// </summary>
public partial class RelayBackend : SensorBackend
{
    readonly RelayClient _client;
    readonly HashSet<string> _connected = new();
    bool _scanning;

    /// <summary>Nom affiché : modèle du téléphone, ou « Bluetooth de ce PC ».</summary>
    public string DisplayName { get; private set; }
    /// <summary>Vrai pour le programme d'aide Bluetooth de Windows (pas un téléphone).</summary>
    public bool IsLocalHelper { get; }
    public bool IsRelayConnected => _client.IsConnected;
    /// <summary>Connexion au relais perdue ou impossible : plus rien ne passera par ce transport.</summary>
    public bool IsLost { get; private set; }
    public string Host => _client.Host;
    /// <summary>Le relais n'a pas de Bluetooth utilisable (raison), ou null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Le relais a répondu (ou la connexion a échoué) : l'écran se met à jour.</summary>
    public event System.Action? StateChanged;

    public RelayBackend(string host, int port, string displayName, bool isLocalHelper = false, double connectTimeoutS = 6.0)
    {
        DisplayName = displayName;
        IsLocalHelper = isLocalHelper;
        _client = new RelayClient(host, port, System.Environment.MachineName) { ConnectTimeoutS = connectTimeoutS };
        _client.Connected += OnRelayConnected;
        _client.Received += OnReceived;
        _client.Lost += OnLost;
    }

    public override void _Ready() => _client.Start();

    public override void _Process(double delta) => _client.Poll();

    public override void _ExitTree() => _client.Dispose();

    public override void StartScan(double timeoutS = 10.0)
    {
        if (IsLost)
        {
            RaiseScanFinished();
            return;
        }
        _scanning = true;
        _client.Send(RelayMessage.Scan(timeoutS));
    }

    public override void StopScan() => _client.Send(RelayMessage.StopScan());
    public override void ConnectDevice(string address) => _client.Send(RelayMessage.Connect(address));

    public override void DisconnectDevice(string address)
    {
        _client.Send(RelayMessage.Disconnect(address));
        _connected.Remove(address);
    }

    public override void Write(string address, string charUuid, byte[] data) =>
        _client.Send(RelayMessage.Write(address, BleUuids.ToShort(charUuid), data));

    void OnRelayConnected(string name, string? problem)
    {
        if (!IsLocalHelper && name.Length > 0) DisplayName = name;
        Problem = problem;
        if (problem is not null)
        {
            RaiseError(problem);
            if (_scanning) { _scanning = false; RaiseScanFinished(); }
        }
        StateChanged?.Invoke();
    }

    void OnReceived(RelayMessage m)
    {
        string address = m.Address ?? "";
        switch (m.T)
        {
            case RelayOp.Found:
                RaiseDeviceFound(address, m.Name ?? "", m.Services ?? [], m.Rssi ?? 0);
                break;
            case RelayOp.ScanDone:
                _scanning = false;
                RaiseScanFinished();
                break;
            case RelayOp.Connected:
                _connected.Add(address);
                RaiseDeviceConnected(address, m.Services ?? []);
                break;
            case RelayOp.Disconnected:
                _connected.Remove(address);
                RaiseDeviceDisconnected(address);
                break;
            case RelayOp.Notify when m.Char is not null && m.Data is not null:
                RaiseNotified(address, m.Char, m.Data);
                break;
            case RelayOp.Read when m.Char is not null && m.Data is not null:
                RaiseRead(address, m.Char, m.Data);
                break;
            case RelayOp.Error:
                RaiseError(m.Message ?? "Erreur du relais.");
                break;
        }
    }

    void OnLost(string reason)
    {
        IsLost = true;
        RaiseError(IsLocalHelper
            ? $"Bluetooth de ce PC indisponible : {reason}"
            : $"Connexion au téléphone ({DisplayName}) perdue : {reason}");
        foreach (var address in new List<string>(_connected)) RaiseDeviceDisconnected(address);
        _connected.Clear();
        if (_scanning) { _scanning = false; RaiseScanFinished(); }
        StateChanged?.Invoke();
    }
}
