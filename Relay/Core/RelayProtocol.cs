using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velom3d.Relay;

/// <summary>
/// Protocole du relais de capteurs : un appareil qui a le Bluetooth (le téléphone, ou le
/// programme d'aide Bluetooth de Windows) le prête à un autre (le PC) par le réseau.
///
/// Une connexion TCP, un message JSON par ligne. Le relais ne décode rien : il transporte les
/// mêmes octets bruts que les backends Bluetooth (voir SensorBackend), dans les deux sens.
/// <code>
/// PC → relais : hello, scan, stop_scan, connect, disconnect, write, ping
/// relais → PC : hello, found, scan_done, connected, disconnected, notify, read, error, ping
/// </code>
/// Découverte sur le réseau local : le PC envoie <see cref="DiscoveryRequest"/> en diffusion UDP
/// sur <see cref="DiscoveryPort"/>, chaque téléphone en mode relais répond directement
/// (message <c>relay</c> avec son nom et son port TCP). La réponse revient au port d'où est
/// partie la question : le pare-feu du PC la laisse passer sans règle à ajouter.
/// </summary>
public static class RelayProtocol
{
    public const int Version = 1;
    /// <summary>Port TCP du relais sur le téléphone.</summary>
    public const int TcpPort = 47801;
    /// <summary>Port UDP où le téléphone attend les questions de découverte.</summary>
    public const int DiscoveryPort = 47800;
    /// <summary>Port TCP du programme d'aide Bluetooth de Windows (sur 127.0.0.1 seulement).</summary>
    public const int HelperPort = 47802;
    public const string DiscoveryRequest = "VELOM3D_RELAY?";
    /// <summary>Sans nouvelles pendant ce temps (même pas un ping), la connexion est considérée perdue.</summary>
    public const double TimeoutS = 8.0;
    public const double PingIntervalS = 2.0;

    public static string Encode(RelayMessage message) =>
        JsonSerializer.Serialize(message, RelayJson.Default.RelayMessage);

    public static RelayMessage? Decode(string line)
    {
        try
        {
            var m = JsonSerializer.Deserialize(line, RelayJson.Default.RelayMessage);
            return m is { T.Length: > 0 } ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] EncodeLine(RelayMessage message) => Encoding.UTF8.GetBytes(Encode(message) + "\n");
}

/// <summary>Types de messages (champ <c>t</c>).</summary>
public static class RelayOp
{
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string Error = "error";
    // PC → relais
    public const string Scan = "scan";
    public const string StopScan = "stop_scan";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string Write = "write";
    // relais → PC
    public const string Found = "found";
    public const string ScanDone = "scan_done";
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
    public const string Notify = "notify";
    public const string Read = "read";
    /// <summary>Réponse UDP à une question de découverte.</summary>
    public const string Relay = "relay";
}

/// <summary>Un message du relais. Seuls les champs utiles à son type sont remplis.</summary>
public sealed class RelayMessage
{
    /// <summary>Type (<see cref="RelayOp"/>).</summary>
    [JsonPropertyName("t")] public string T { get; set; } = "";
    /// <summary>Adresse de l'appareil Bluetooth.</summary>
    [JsonPropertyName("a")] public string? Address { get; set; }
    /// <summary>Nom : appareil Bluetooth (found), ou appareil qui parle (hello, relay).</summary>
    [JsonPropertyName("n")] public string? Name { get; set; }
    /// <summary>Services (UUID courts).</summary>
    [JsonPropertyName("s")] public string[]? Services { get; set; }
    [JsonPropertyName("r")] public int? Rssi { get; set; }
    /// <summary>Caractéristique (UUID court).</summary>
    [JsonPropertyName("c")] public string? Char { get; set; }
    /// <summary>Octets bruts (base64 dans le JSON).</summary>
    [JsonPropertyName("d")] public byte[]? Data { get; set; }
    /// <summary>Texte : erreur, ou dans hello du relais, la raison pour laquelle le Bluetooth manque.</summary>
    [JsonPropertyName("m")] public string? Message { get; set; }
    /// <summary>Durée du scan (s), ou port TCP dans la réponse de découverte.</summary>
    [JsonPropertyName("x")] public double? Number { get; set; }
    [JsonPropertyName("v")] public int? Version { get; set; }

    public static RelayMessage Hello(string name, string? problem = null) =>
        new() { T = RelayOp.Hello, Name = name, Version = RelayProtocol.Version, Message = problem };
    public static RelayMessage Ping() => new() { T = RelayOp.Ping };
    public static RelayMessage Error(string message) => new() { T = RelayOp.Error, Message = message };
    public static RelayMessage Scan(double timeoutS) => new() { T = RelayOp.Scan, Number = timeoutS };
    public static RelayMessage StopScan() => new() { T = RelayOp.StopScan };
    public static RelayMessage Connect(string address) => new() { T = RelayOp.Connect, Address = address };
    public static RelayMessage Disconnect(string address) => new() { T = RelayOp.Disconnect, Address = address };
    public static RelayMessage Write(string address, string charUuid, byte[] data) =>
        new() { T = RelayOp.Write, Address = address, Char = charUuid, Data = data };
    public static RelayMessage Found(string address, string name, string[] services, int rssi) =>
        new() { T = RelayOp.Found, Address = address, Name = name, Services = services, Rssi = rssi };
    public static RelayMessage ScanDone() => new() { T = RelayOp.ScanDone };
    public static RelayMessage Connected(string address, string[] services) =>
        new() { T = RelayOp.Connected, Address = address, Services = services };
    public static RelayMessage Disconnected(string address) => new() { T = RelayOp.Disconnected, Address = address };
    public static RelayMessage Notify(string address, string charUuid, byte[] data) =>
        new() { T = RelayOp.Notify, Address = address, Char = charUuid, Data = data };
    public static RelayMessage Read(string address, string charUuid, byte[] data) =>
        new() { T = RelayOp.Read, Address = address, Char = charUuid, Data = data };
    public static RelayMessage Relay(string name, int port) =>
        new() { T = RelayOp.Relay, Name = name, Number = port, Version = RelayProtocol.Version };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayMessage))]
public partial class RelayJson : JsonSerializerContext;
