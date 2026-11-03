using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Velom3d.Relay;

/// <summary>
/// Une connexion TCP du relais, d'un côté ou de l'autre. Lecture et écriture sur deux fils
/// d'exécution à part : le jeu n'attend jamais le réseau. Les messages reçus s'empilent et sont
/// lus par <see cref="TryReceive"/> depuis le fil principal (_Process dans Godot).
/// <see cref="Tick"/> envoie un ping régulier et ferme la connexion si l'autre côté ne donne
/// plus de nouvelles (Wi-Fi coupé, téléphone en veille) : sans cela, TCP peut attendre des minutes.
/// </summary>
public sealed class RelayLink : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly ConcurrentQueue<RelayMessage> _in = new();
    readonly BlockingCollection<byte[]> _out = new();
    long _lastReceivedMs;
    long _lastSentMs;
    int _closed;

    /// <summary>Adresse IP de l'autre côté.</summary>
    public string RemoteAddress { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    /// <summary>Pourquoi la connexion s'est fermée (à montrer au joueur), ou null si elle est ouverte.</summary>
    public string? CloseReason { get; private set; }

    static long NowMs => Environment.TickCount64;

    public RelayLink(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true; // petites trames, à envoyer tout de suite
        _stream = tcp.GetStream();
        RemoteAddress = (tcp.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "?";
        _lastReceivedMs = _lastSentMs = NowMs;
        new Thread(ReadLoop) { IsBackground = true, Name = "relay-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "relay-write" }.Start();
    }

    public void Send(RelayMessage message)
    {
        if (!IsOpen) return;
        try
        {
            _out.Add(RelayProtocol.EncodeLine(message));
            Interlocked.Exchange(ref _lastSentMs, NowMs);
        }
        catch (InvalidOperationException) { } // fermée entre-temps
    }

    public bool TryReceive(out RelayMessage message) => _in.TryDequeue(out message!);

    /// <summary>À appeler régulièrement : ping, et fermeture si l'autre côté se tait.</summary>
    public void Tick()
    {
        if (!IsOpen) return;
        long now = NowMs;
        if (now - Interlocked.Read(ref _lastReceivedMs) > RelayProtocol.TimeoutS * 1000)
        {
            Close("L'autre appareil ne répond plus (réseau coupé ou appli en veille).");
            return;
        }
        if (now - Interlocked.Read(ref _lastSentMs) > RelayProtocol.PingIntervalS * 1000) Send(RelayMessage.Ping());
    }

    /// <summary>Ferme la connexion. <paramref name="flush"/> : envoie d'abord les messages en attente
    /// (ex. l'erreur qui explique un refus).</summary>
    public void Close(string reason, bool flush = false)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        _out.CompleteAdding();
        if (!flush) CloseSocket(); // sinon, WriteLoop la ferme une fois tout envoyé
    }

    void CloseSocket()
    {
        try { _tcp.Close(); } catch { /* déjà fermée */ }
    }

    public void Dispose() => Close("Connexion fermée.");

    void ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (IsOpen)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                Interlocked.Exchange(ref _lastReceivedMs, NowMs);
                if (line.Length == 0) continue;
                var message = RelayProtocol.Decode(line);
                if (message is null || message.T == RelayOp.Ping) continue;
                _in.Enqueue(message);
            }
            Close("L'autre appareil a fermé la connexion.");
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Close("Connexion perdue : " + e.Message);
        }
    }

    void WriteLoop()
    {
        try
        {
            foreach (var bytes in _out.GetConsumingEnumerable())
                _stream.Write(bytes, 0, bytes.Length);
            CloseSocket();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Close("Connexion perdue : " + e.Message);
        }
    }
}
