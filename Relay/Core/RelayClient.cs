using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace Velom3d.Relay;

/// <summary>
/// Côté PC : se connecte à un relais (téléphone sur le réseau local, ou programme d'aide
/// Bluetooth de Windows sur 127.0.0.1). La connexion se fait en arrière-plan, en réessayant
/// jusqu'à <see cref="ConnectTimeoutS"/> (le programme d'aide met quelques secondes à démarrer).
/// Les messages envoyés avant la connexion attendent et partent dès qu'elle est établie.
/// Les évènements sont levés dans <see cref="Poll"/>, donc sur le fil qui l'appelle.
/// </summary>
public sealed class RelayClient : IDisposable
{
    readonly string _host;
    readonly int _port;
    readonly string _name;
    readonly ConcurrentQueue<object> _results = new(); // RelayLink, ou string (échec)
    readonly List<RelayMessage> _pending = new();
    volatile bool _stopped;
    bool _helloReceived;

    public double ConnectTimeoutS { get; init; } = 5.0;
    public RelayLink? Link { get; private set; }
    public string Host => _host;
    public int Port => _port;
    /// <summary>Nom du relais, reçu dans son hello.</summary>
    public string RelayName { get; private set; } = "";
    public bool IsConnected => _helloReceived && Link is { IsOpen: true };

    /// <summary>Le relais a dit bonjour : son nom, et la raison s'il n'a pas de Bluetooth (sinon null).</summary>
    public event Action<string, string?>? Connected;
    /// <summary>Message du relais (hors hello et ping).</summary>
    public event Action<RelayMessage>? Received;
    /// <summary>Connexion impossible, ou perdue : raison à montrer.</summary>
    public event Action<string>? Lost;

    public RelayClient(string host, int port, string name)
    {
        _host = host;
        _port = port;
        _name = name;
    }

    public void Start() => new Thread(ConnectLoop) { IsBackground = true, Name = "relay-connect" }.Start();

    public void Send(RelayMessage message)
    {
        if (Link is { } link) link.Send(message);
        else if (!_stopped) _pending.Add(message);
    }

    public void Poll()
    {
        while (_results.TryDequeue(out var r))
        {
            if (r is string failure)
            {
                _stopped = true;
                Lost?.Invoke(failure);
                return;
            }
            var link = (RelayLink)r;
            if (_stopped) { link.Close("arrêté"); continue; }
            Link = link;
            link.Send(RelayMessage.Hello(_name));
            foreach (var m in _pending) link.Send(m);
            _pending.Clear();
        }

        var current = Link;
        if (current is null) return;
        current.Tick();
        while (current.TryReceive(out var m))
        {
            if (m.T == RelayOp.Hello)
            {
                _helloReceived = true;
                RelayName = m.Name ?? _host;
                Connected?.Invoke(RelayName, string.IsNullOrEmpty(m.Message) ? null : m.Message);
            }
            else Received?.Invoke(m);
        }
        if (!current.IsOpen && !_stopped)
        {
            _stopped = true;
            Lost?.Invoke(current.CloseReason ?? "Connexion perdue.");
        }
    }

    public void Dispose()
    {
        _stopped = true;
        Link?.Close("Déconnecté.");
    }

    void ConnectLoop()
    {
        var deadline = Environment.TickCount64 + (long)(ConnectTimeoutS * 1000);
        string last = "";
        while (!_stopped)
        {
            var tcp = new TcpClient();
            try
            {
                var remaining = Math.Max(500, deadline - Environment.TickCount64);
                if (!tcp.ConnectAsync(_host, _port).Wait(TimeSpan.FromMilliseconds(Math.Min(remaining, 3000))))
                    throw new TimeoutException("délai dépassé");
                _results.Enqueue(new RelayLink(tcp));
                return;
            }
            catch (Exception e)
            {
                tcp.Dispose();
                last = (e is AggregateException { InnerException: { } inner } ? inner : e).Message;
            }
            if (Environment.TickCount64 >= deadline) break;
            Thread.Sleep(400);
        }
        if (!_stopped) _results.Enqueue($"Connexion impossible à {_host}:{_port} ({last}).");
    }
}
