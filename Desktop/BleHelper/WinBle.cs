using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Velom3d.Relay;
using Velom3d.Sensors;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace Velom3d.BleHelper;

/// <summary>
/// Bluetooth Low Energy de Windows, avec le même comportement que le plugin Android
/// (android-plugin/…/Velom3dBlePlugin.kt) : scan filtré sur les services utiles, à la connexion
/// abonnement aux mesures puis lecture de la fiche de l'appareil, et écritures une à la fois.
/// </summary>
public sealed class WinBle : IDisposable
{
    static readonly string[] ScanServices = [BleUuids.FitnessMachine, BleUuids.CyclingPower, BleUuids.HeartRate, BleUuids.CyclingSpeedCadence];

    static readonly HashSet<string> Subscribe =
    [
        BleUuids.IndoorBikeData, BleUuids.FitnessMachineControlPoint, BleUuids.FitnessMachineStatus,
        BleUuids.CyclingPowerMeasurement, BleUuids.HeartRateMeasurement, "2a5b",
    ];

    static readonly HashSet<string> Read =
    [
        BleUuids.ManufacturerName, BleUuids.ModelNumber, BleUuids.SerialNumber, BleUuids.HardwareRevision,
        BleUuids.FirmwareRevision, BleUuids.SoftwareRevision, BleUuids.FitnessMachineFeature,
        BleUuids.SupportedInclinationRange, BleUuids.SupportedResistanceLevelRange, BleUuids.SupportedPowerRange,
        BleUuids.CyclingPowerFeature, BleUuids.SensorLocation, BleUuids.BodySensorLocation,
    ];

    sealed class Seen
    {
        public string Name = "";
        public readonly HashSet<string> Services = new();
        public long LastSentMs = long.MinValue / 2;
    }

    sealed class Link : IDisposable
    {
        public required string Address;
        public required BluetoothLEDevice Device;
        public readonly List<GattDeviceService> Services = new();
        public readonly Dictionary<string, GattCharacteristic> Chars = new();
        public readonly SemaphoreSlim Gate = new(1, 1); // une opération GATT à la fois
        public bool Ready;

        public void Dispose()
        {
            foreach (var s in Services) s.Dispose();
            Services.Clear();
            Chars.Clear();
            Device.Dispose();
        }
    }

    readonly object _lock = new();
    readonly ConcurrentDictionary<string, Seen> _seen = new();
    readonly Dictionary<string, Link> _links = new();
    BluetoothLEAdvertisementWatcher? _watcher;
    Timer? _scanTimer;

    /// <summary>Envoi vers le jeu (RelayServer.Send, utilisable depuis n'importe quel fil).</summary>
    public Action<RelayMessage> Send { get; set; } = _ => { };

    /// <summary>Raison pour laquelle le Bluetooth n'est pas utilisable, ou null s'il l'est.</summary>
    public async Task<string?> CheckAdapterAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) return "Ce PC n'a pas de Bluetooth. Utilisez le relais par téléphone.";
            if (!adapter.IsLowEnergySupported) return "Le Bluetooth de ce PC ne gère pas le Bluetooth Low Energy. Utilisez le relais par téléphone.";
            var radio = await adapter.GetRadioAsync();
            if (radio is { State: not RadioState.On })
                return "Le Bluetooth de ce PC est désactivé : activez-le dans les paramètres de Windows, puis relancez le jeu.";
            return null;
        }
        catch (Exception e)
        {
            return "Bluetooth de Windows inaccessible : " + e.Message;
        }
    }

    public void Handle(RelayMessage m)
    {
        string address = m.Address ?? "";
        switch (m.T)
        {
            case RelayOp.Scan: StartScan(m.Number is > 0 and < 120 ? m.Number.Value : 10.0); break;
            case RelayOp.StopScan: StopScan(notify: true); break;
            case RelayOp.Connect: _ = ConnectAsync(address); break;
            case RelayOp.Disconnect: Disconnect(address, notify: true); break;
            case RelayOp.Write when m.Char is not null && m.Data is not null: _ = WriteAsync(address, m.Char, m.Data); break;
        }
    }

    /// <summary>Le jeu est arrivé ou parti : on repart de zéro (trainer libéré).</summary>
    public void Reset()
    {
        StopScan(notify: false);
        List<string> addresses;
        lock (_lock) addresses = _links.Keys.ToList();
        foreach (var a in addresses) Disconnect(a, notify: false);
    }

    public void Dispose() => Reset();

    // --- Scan ---

    void StartScan(double timeoutS)
    {
        StopScan(notify: false);
        _seen.Clear();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += OnAdvertisement;
        watcher.Stopped += (w, args) =>
        {
            if (args.Error != BluetoothError.Success)
                Send(RelayMessage.Error($"La recherche Bluetooth a échoué ({args.Error})."));
        };
        lock (_lock) _watcher = watcher;
        try
        {
            watcher.Start();
        }
        catch (Exception e)
        {
            Send(RelayMessage.Error("Recherche Bluetooth impossible : " + e.Message));
            StopScan(notify: true);
            return;
        }
        _scanTimer = new Timer(_ => StopScan(notify: true), null, TimeSpan.FromSeconds(Math.Max(1.0, timeoutS)), Timeout.InfiniteTimeSpan);
    }

    void StopScan(bool notify)
    {
        BluetoothLEAdvertisementWatcher? watcher;
        lock (_lock)
        {
            watcher = _watcher;
            _watcher = null;
            _scanTimer?.Dispose();
            _scanTimer = null;
        }
        if (watcher is null) return;
        watcher.Received -= OnAdvertisement;
        try { watcher.Stop(); } catch (Exception) { }
        if (notify) Send(RelayMessage.ScanDone());
    }

    void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // Le nom arrive souvent dans la réponse au scan, les services dans l'annonce : on cumule.
        var address = FormatAddress(args.BluetoothAddress);
        var seen = _seen.GetOrAdd(address, _ => new Seen());
        bool useful;
        lock (seen)
        {
            if (!string.IsNullOrEmpty(args.Advertisement.LocalName)) seen.Name = args.Advertisement.LocalName;
            foreach (var uuid in args.Advertisement.ServiceUuids) seen.Services.Add(BleUuids.ToShort(uuid.ToString()));
            useful = seen.Services.Any(s => ScanServices.Contains(s));
            long now = Environment.TickCount64;
            // Plusieurs annonces par seconde : une remontée par seconde suffit (comme sur Android).
            if (!useful || now - seen.LastSentMs < 1000) return;
            seen.LastSentMs = now;
        }
        string[] services;
        string name;
        lock (seen)
        {
            services = seen.Services.ToArray();
            name = seen.Name;
        }
        Send(RelayMessage.Found(address, name, services, args.RawSignalStrengthInDBm));
    }

    // --- Connexion ---

    async Task ConnectAsync(string address)
    {
        lock (_lock)
            if (_links.ContainsKey(address)) return;
        if (!TryParseAddress(address, out ulong raw))
        {
            Fail(address, "Adresse Bluetooth invalide : " + address);
            return;
        }
        Link? link = null;
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(raw);
            if (device is null)
            {
                Fail(address, $"Appareil {address} introuvable.");
                return;
            }
            link = new Link { Address = address, Device = device };
            lock (_lock) _links[address] = link;

            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success)
            {
                Drop(link, $"Services Bluetooth illisibles sur {address} ({services.Status}).");
                return;
            }
            foreach (var service in services.Services)
            {
                link.Services.Add(service);
                var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics) link.Chars.TryAdd(BleUuids.ToShort(c.Uuid.ToString()), c);
            }

            foreach (var (uuid, c) in link.Chars)
            {
                if (!Subscribe.Contains(uuid)) continue;
                var props = c.CharacteristicProperties;
                GattClientCharacteristicConfigurationDescriptorValue mode =
                    props.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : props.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                    : GattClientCharacteristicConfigurationDescriptorValue.None;
                if (mode == GattClientCharacteristicConfigurationDescriptorValue.None) continue;
                string charUuid = uuid;
                c.ValueChanged += (_, e) => Send(RelayMessage.Notify(address, charUuid, e.CharacteristicValue.ToArray()));
                var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
                if (status != GattCommunicationStatus.Success)
                    Send(RelayMessage.Error($"Abonnement refusé par {address} ({uuid}, {status})."));
            }

            // Fiche de l'appareil (fabricant, plages de puissance…), lue avant d'annoncer la connexion.
            foreach (var (uuid, c) in link.Chars)
            {
                if (!Read.Contains(uuid) || !c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;
                var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (r.Status == GattCommunicationStatus.Success) Send(RelayMessage.Read(address, uuid, r.Value.ToArray()));
            }

            lock (_lock)
                if (!_links.TryGetValue(address, out var current) || current != link) return; // déconnecté entre-temps
            device.ConnectionStatusChanged += (d, _) =>
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                    Drop(link, $"Connexion perdue avec {address}.");
            };
            link.Ready = true;
            Send(RelayMessage.Connected(address, link.Services.Select(s => BleUuids.ToShort(s.Uuid.ToString())).ToArray()));
        }
        catch (Exception e)
        {
            if (link is not null) Drop(link, $"Connexion impossible à {address} : {e.Message}");
            else Fail(address, $"Connexion impossible à {address} : {e.Message}");
        }
    }

    void Fail(string address, string message)
    {
        Send(RelayMessage.Error(message));
        Send(RelayMessage.Disconnected(address));
    }

    /// <summary>Retire une connexion perdue (si c'est toujours la connexion en cours) et prévient le jeu.</summary>
    void Drop(Link link, string message)
    {
        lock (_lock)
        {
            if (!_links.TryGetValue(link.Address, out var current) || current != link) return;
            _links.Remove(link.Address);
        }
        link.Dispose();
        Fail(link.Address, message);
    }

    void Disconnect(string address, bool notify)
    {
        Link? link;
        lock (_lock)
        {
            if (!_links.Remove(address, out link)) return;
        }
        link.Dispose();
        if (notify) Send(RelayMessage.Disconnected(address));
    }

    async Task WriteAsync(string address, string charUuid, byte[] data)
    {
        Link? link;
        lock (_lock) _links.TryGetValue(address, out link);
        if (link is not { Ready: true } || !link.Chars.TryGetValue(BleUuids.ToShort(charUuid), out var c))
        {
            if (link is not null) Send(RelayMessage.Error($"Caractéristique {charUuid} absente sur {address}."));
            return;
        }
        await link.Gate.WaitAsync();
        try
        {
            var option = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
            var result = await c.WriteValueWithResultAsync(data.AsBuffer(), option);
            if (result.Status != GattCommunicationStatus.Success)
                Send(RelayMessage.Error($"Écriture refusée par {address} ({result.Status})."));
        }
        catch (Exception e)
        {
            Send(RelayMessage.Error($"Écriture impossible sur {address} : {e.Message}"));
        }
        finally
        {
            link.Gate.Release();
        }
    }

    static string FormatAddress(ulong raw) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((raw >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    static bool TryParseAddress(string text, out ulong raw)
    {
        raw = 0;
        var parts = text.Split(':');
        if (parts.Length != 6) return false;
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out var b)) return false;
            raw = (raw << 8) | b;
        }
        return true;
    }
}
