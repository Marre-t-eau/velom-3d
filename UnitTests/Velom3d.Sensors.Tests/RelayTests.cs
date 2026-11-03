using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Velom3d.Relay;
using Xunit;

namespace Velom3d.Sensors.Tests;

/// <summary>Relais de capteurs téléphone → PC, de bout en bout sur 127.0.0.1.</summary>
public class RelayTests
{
    static void PollUntil(Func<bool> done, params Action[] polls)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "délai dépassé");
            foreach (var p in polls) p();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Message_round_trip_keeps_raw_bytes_and_skips_nulls()
    {
        var m = RelayMessage.Notify("AA:BB:CC:DD:EE:FF", "2ad2", [0x44, 0x02, 0x00, 0xFF]);
        var json = RelayProtocol.Encode(m);
        Assert.DoesNotContain("\"n\"", json);
        var back = RelayProtocol.Decode(json)!;
        Assert.Equal(RelayOp.Notify, back.T);
        Assert.Equal("AA:BB:CC:DD:EE:FF", back.Address);
        Assert.Equal("2ad2", back.Char);
        Assert.Equal(new byte[] { 0x44, 0x02, 0x00, 0xFF }, back.Data);
        Assert.Null(RelayProtocol.Decode("pas du json"));
        Assert.Null(RelayProtocol.Decode("{}"));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", RelayProtocol.TcpPort)]
    [InlineData(" 10.0.0.5:5000 ", "10.0.0.5", 5000)]
    [InlineData("pixel.local", "pixel.local", RelayProtocol.TcpPort)]
    public void Parses_addresses_typed_on_the_pc(string text, string host, int port)
    {
        Assert.True(RelayDiscovery.TryParseAddress(text, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.20:abc")]
    [InlineData("192.168.1.20:70000")]
    public void Rejects_bad_addresses(string text) => Assert.False(RelayDiscovery.TryParseAddress(text, out _, out _));

    [Fact]
    public void Pc_drives_the_phone_and_receives_sensor_bytes()
    {
        using var server = new RelayServer("Pixel test", IPAddress.Loopback, port: 0, discovery: false);
        server.Start();
        var commands = new List<RelayMessage>();
        string? pcName = null;
        string? lost = null;
        server.Received += commands.Add;
        server.ClientConnected += (name, _) => pcName = name;
        server.ClientLost += reason => lost = reason;

        using var client = new RelayClient("127.0.0.1", server.Port, "PC-SALON");
        var received = new List<RelayMessage>();
        string? relayName = null;
        client.Connected += (name, problem) => { relayName = name; Assert.Null(problem); };
        client.Received += received.Add;
        // Envoyé avant la connexion : doit attendre puis partir.
        client.Send(RelayMessage.Scan(10));
        client.Start();

        PollUntil(() => relayName is not null && pcName is not null && commands.Count > 0, server.Poll, client.Poll);
        Assert.Equal("Pixel test", relayName);
        Assert.Equal("PC-SALON", pcName);
        Assert.Equal(RelayOp.Scan, commands[0].T);
        Assert.Equal(10, commands[0].Number);

        client.Send(RelayMessage.Write("AA:BB", "2ad9", [0x05, 0xC8, 0x00]));
        server.Send(RelayMessage.Found("AA:BB", "Trainer", ["1826", "1818"], -50));
        server.Send(RelayMessage.Notify("AA:BB", "2ad2", [1, 2, 3]));
        PollUntil(() => commands.Count >= 2 && received.Count >= 2, server.Poll, client.Poll);
        Assert.Equal(new byte[] { 0x05, 0xC8, 0x00 }, commands[1].Data);
        Assert.Equal(["1826", "1818"], received[0].Services!);
        Assert.Equal(-50, received[0].Rssi);
        Assert.Equal(new byte[] { 1, 2, 3 }, received[1].Data);

        client.Dispose();
        PollUntil(() => lost is not null, server.Poll);
        Assert.Null(server.Client);
    }

    [Fact]
    public void A_second_pc_is_refused_while_one_is_connected()
    {
        using var server = new RelayServer("Pixel", IPAddress.Loopback, port: 0, discovery: false);
        server.Start();
        using var first = new RelayClient("127.0.0.1", server.Port, "PC1");
        bool firstOk = false;
        first.Connected += (_, _) => firstOk = true;
        first.Start();
        PollUntil(() => firstOk && server.ClientName == "PC1", server.Poll, first.Poll);

        using var second = new RelayClient("127.0.0.1", server.Port, "PC2");
        string? error = null;
        second.Received += m => { if (m.T == RelayOp.Error) error = m.Message; };
        second.Start();
        PollUntil(() => error is not null, server.Poll, second.Poll, first.Poll);
        Assert.Contains("PC1", error);
        Assert.Equal("PC1", server.ClientName);
    }

    [Fact]
    public void Relay_without_bluetooth_says_why_in_hello()
    {
        using var server = new RelayServer("PC", IPAddress.Loopback, port: 0, discovery: false) { Problem = "Ce PC n'a pas de Bluetooth." };
        server.Start();
        using var client = new RelayClient("127.0.0.1", server.Port, "jeu");
        string? problem = null;
        client.Connected += (_, p) => problem = p;
        client.Start();
        PollUntil(() => problem is not null, server.Poll, client.Poll);
        Assert.Equal("Ce PC n'a pas de Bluetooth.", problem);
    }

    [Fact]
    public void Unreachable_relay_reports_a_failure()
    {
        using var client = new RelayClient("127.0.0.1", 1, "jeu") { ConnectTimeoutS = 0.5 };
        string? lost = null;
        client.Lost += r => lost = r;
        client.Start();
        PollUntil(() => lost is not null, client.Poll);
        Assert.Contains("127.0.0.1:1", lost);
    }

    [Fact]
    public async Task Discovery_finds_the_phone_and_its_port()
    {
        // Port de découverte libre pour le test (le vrai, 47800, peut être pris sur la machine).
        int discoveryPort = 47900 + Random.Shared.Next(90);
        using var server = new RelayServer("Pixel 8", IPAddress.Loopback, port: 0, discovery: true, discoveryPort: discoveryPort);
        server.Start();
        Assert.Null(server.DiscoveryProblem);
        var found = await RelayDiscovery.FindAsync(TimeSpan.FromSeconds(1),
            [new IPEndPoint(IPAddress.Loopback, discoveryPort)]);
        var phone = Assert.Single(found);
        Assert.Equal("Pixel 8", phone.Name);
        Assert.Equal("127.0.0.1", phone.Host);
        Assert.Equal(server.Port, phone.Port);
    }

    [Fact]
    public void Broadcast_targets_include_the_limited_broadcast_address() =>
        Assert.Contains(RelayDiscovery.BroadcastTargets(), e => e.Address.Equals(IPAddress.Broadcast) && e.Port == RelayProtocol.DiscoveryPort);
}
