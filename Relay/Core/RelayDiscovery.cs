using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Velom3d.Relay;

/// <summary>Un téléphone en mode relais trouvé sur le réseau.</summary>
public sealed record RelayInfo(string Name, string Host, int Port);

/// <summary>
/// Côté PC : cherche les téléphones en mode relais sur le réseau local. Envoie la question en
/// diffusion (255.255.255.255 et l'adresse de diffusion de chaque carte réseau, car Windows
/// n'envoie la première que sur une seule carte), deux fois, et écoute les réponses.
/// </summary>
public static class RelayDiscovery
{
    public static async Task<List<RelayInfo>> FindAsync(TimeSpan duration, IEnumerable<IPEndPoint>? targets = null,
        CancellationToken cancel = default)
    {
        var found = new Dictionary<string, RelayInfo>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var request = Encoding.UTF8.GetBytes(RelayProtocol.DiscoveryRequest);
        var destinations = (targets ?? BroadcastTargets()).ToList();

        void SendAll()
        {
            foreach (var target in destinations)
            {
                try { udp.Send(request, request.Length, target); }
                catch (SocketException) { } // carte sans réseau
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(duration);
        SendAll();
        var resend = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(600, duration.TotalMilliseconds / 2)), timeout.Token)
            .ContinueWith(t => { if (!t.IsCanceled) SendAll(); }, TaskScheduler.Default);
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // ICMP « port fermé » renvoyé par une adresse de diffusion
            var m = RelayProtocol.Decode(Encoding.UTF8.GetString(result.Buffer));
            if (m is not { T: RelayOp.Relay }) continue;
            var host = result.RemoteEndPoint.Address.ToString();
            found[host] = new RelayInfo(string.IsNullOrWhiteSpace(m.Name) ? host : m.Name!, host,
                m.Number is { } port and > 0 and < 65536 ? (int)port : RelayProtocol.TcpPort);
        }
        try { await resend; } catch (OperationCanceledException) { }
        return found.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>255.255.255.255 et l'adresse de diffusion de chaque carte réseau IPv4 active.</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets(int port = RelayProtocol.DiscoveryPort)
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var (ip, mask) in LocalIPv4())
        {
            var a = ip.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            addresses.Add(new IPAddress(b));
        }
        return addresses.Select(a => new IPEndPoint(a, port));
    }

    /// <summary>Adresses IPv4 de cet appareil sur le réseau local (hors 127.x).</summary>
    public static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalIPv4()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception) { yield break; } // certaines plateformes refusent la liste
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            UnicastIPAddressInformationCollection unicast;
            try { unicast = nic.GetIPProperties().UnicastAddresses; }
            catch (Exception) { continue; }
            foreach (var u in unicast)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                IPAddress? mask = null;
                try { mask = u.IPv4Mask; } catch (Exception) { }
                if (mask is null || mask.Equals(IPAddress.Any)) mask = IPAddress.Parse("255.255.255.0");
                yield return (u.Address, mask);
            }
        }
    }

    /// <summary>"192.168.1.20", "192.168.1.20:47801" → hôte et port (port du relais par défaut).</summary>
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        host = "";
        port = RelayProtocol.TcpPort;
        text = text.Trim();
        if (text.Length == 0) return false;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535) return false;
            text = text[..colon];
        }
        host = text;
        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }
}
