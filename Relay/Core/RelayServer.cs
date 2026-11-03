using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Velom3d.Relay;

/// <summary>
/// Côté relais (le téléphone, ou le programme d'aide Bluetooth de Windows) : attend un PC sur
/// le port TCP et, si demandé, répond aux questions de découverte UDP.
///
/// Un seul PC à la fois : un trainer ne se pilote pas à deux. Un second reçoit une erreur.
/// Les évènements sont levés dans <see cref="Poll"/>, donc sur le fil qui l'appelle.
/// </summary>
public sealed class RelayServer : IDisposable
{
    readonly IPAddress _bind;
    readonly int _requestedPort;
    readonly bool _discovery;
    readonly int _discoveryPort;
    readonly ConcurrentQueue<TcpClient> _accepted = new();
    TcpListener? _listener;
    UdpClient? _udp;
    volatile bool _stopped;

    /// <summary>Nom annoncé au PC (modèle du téléphone).</summary>
    public string Name { get; set; }
    /// <summary>Si le Bluetooth manque ici : raison envoyée au PC dans hello.</summary>
    public string? Problem { get; set; }
    /// <summary>Port TCP réellement ouvert (utile si 0 a été demandé).</summary>
    public int Port { get; private set; }
    /// <summary>Le PC connecté, ou null.</summary>
    public RelayLink? Client { get; private set; }
    /// <summary>Nom donné par le PC dans son hello.</summary>
    public string ClientName { get; private set; } = "";
    /// <summary>Vrai si la découverte UDP n'a pas pu démarrer (port pris) : seule l'adresse IP marche.</summary>
    public string? DiscoveryProblem { get; private set; }

    /// <summary>Un PC s'est connecté (nom, adresse IP).</summary>
    public event Action<string, string>? ClientConnected;
    /// <summary>Le PC est parti (raison).</summary>
    public event Action<string>? ClientLost;
    /// <summary>Message reçu du PC (hors hello et ping).</summary>
    public event Action<RelayMessage>? Received;

    public RelayServer(string name, IPAddress bind, int port = RelayProtocol.TcpPort, bool discovery = true,
        int discoveryPort = RelayProtocol.DiscoveryPort)
    {
        Name = name;
        _bind = bind;
        _requestedPort = port;
        _discovery = discovery;
        _discoveryPort = discoveryPort;
    }

    /// <summary>Ouvre le port. Lève <see cref="SocketException"/> s'il est déjà pris.</summary>
    public void Start()
    {
        _listener = new TcpListener(_bind, _requestedPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "relay-accept" }.Start();
        if (!_discovery) return;
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _udp.EnableBroadcast = true;
            new Thread(DiscoveryLoop) { IsBackground = true, Name = "relay-discovery" }.Start();
        }
        catch (SocketException e)
        {
            DiscoveryProblem = e.Message;
            _udp?.Dispose();
            _udp = null;
        }
    }

    /// <summary>À appeler à chaque image : nouveaux PC, messages reçus, connexion perdue.</summary>
    public void Poll()
    {
        while (_accepted.TryDequeue(out var tcp))
        {
            var link = new RelayLink(tcp);
            if (Client is { IsOpen: true })
            {
                link.Send(RelayMessage.Error($"{Name} sert déjà de relais à {ClientName}."));
                link.Close("refusé", flush: true);
                continue;
            }
            Client = link;
            ClientName = "";
            link.Send(RelayMessage.Hello(Name, Problem));
        }

        var client = Client;
        if (client is null) return;
        client.Tick();
        while (client.TryReceive(out var m))
        {
            if (m.T == RelayOp.Hello)
            {
                ClientName = string.IsNullOrWhiteSpace(m.Name) ? client.RemoteAddress : m.Name!;
                ClientConnected?.Invoke(ClientName, client.RemoteAddress);
            }
            else Received?.Invoke(m);
        }
        if (!client.IsOpen)
        {
            Client = null;
            ClientLost?.Invoke(client.CloseReason ?? "");
        }
    }

    public void Send(RelayMessage message) => Client?.Send(message);

    public void Dispose()
    {
        _stopped = true;
        try { _listener?.Stop(); } catch { /* déjà fermé */ }
        _udp?.Dispose();
        Client?.Close("Relais arrêté.");
        while (_accepted.TryDequeue(out var tcp)) tcp.Dispose();
    }

    void AcceptLoop()
    {
        try
        {
            while (!_stopped) _accepted.Enqueue(_listener!.AcceptTcpClient());
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    void DiscoveryLoop()
    {
        var udp = _udp!;
        while (!_stopped)
        {
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var bytes = udp.Receive(ref from);
                if (Encoding.UTF8.GetString(bytes).Trim() != RelayProtocol.DiscoveryRequest) continue;
                var reply = Encoding.UTF8.GetBytes(RelayProtocol.Encode(RelayMessage.Relay(Name, Port)));
                udp.Send(reply, reply.Length, from);
            }
            catch (SocketException) when (!_stopped) { Thread.Sleep(100); } // ex. réponse ICMP à un envoi précédent
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
        }
    }
}
