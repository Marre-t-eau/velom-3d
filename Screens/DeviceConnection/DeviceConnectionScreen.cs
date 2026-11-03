using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Relay;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Premier écran (mise en page : DeviceConnectionScreen.tscn) : recherche et connexion du home trainer, du capteur de puissance et de la
/// ceinture cardio, par le Bluetooth du téléphone ou du PC. Pensé pour le tactile (grandes cibles,
/// liste qui défile au doigt). Sans Bluetooth (éditeur, PC sans Bluetooth), l'écran
/// l'indique et ne cherche rien, sauf en mode debug (capteurs simulés, voir SensorBackends). « Continuer » mène au menu principal.
/// Ouvert depuis le menu d'une sortie (<see cref="ReturnToRide"/>), il se pose par-dessus la sortie,
/// qui reste en mémoire : le bouton principal devient « Revenir à la sortie ».
///
/// Sur PC, le cadre « relais » permet d'utiliser le Bluetooth d'un téléphone du même réseau
/// (mode « Relais pour PC » sur le téléphone, voir RelayScreen) : téléphones trouvés tout seuls,
/// ou adresse IP à saisir. Sur le téléphone, le bouton « Relais pour PC » ouvre ce mode.
/// </summary>
public partial class DeviceConnectionScreen : Control
{
    /// <summary>Une ligne de la liste des appareils (DeviceRow.tscn).</summary>
    [Export] public PackedScene RowScene { get; set; } = null!;

    static readonly Color Muted = new(0.62f, 0.66f, 0.72f);

    static readonly (DeviceKind Kind, string Node)[] Slots =
        [(DeviceKind.Trainer, "%TrainerSlot"), (DeviceKind.PowerMeter, "%PowerSlot"), (DeviceKind.HeartRate, "%HeartRateSlot")];

    sealed record Row(PanelContainer Panel, Label Name, Label Detail, Button Button);

    readonly Dictionary<DeviceKind, (PanelContainer Panel, Label Name, Label Value)> _slots = new();
    readonly Dictionary<string, Row> _rows = new();
    Button _scanButton = null!;
    Button _continueButton = null!;
    Label _status = null!;
    VBoxContainer _list = null!;
    Label _empty = null!;
    PanelContainer _relayPanel = null!;
    Label _source = null!;
    Button _findPhonesButton = null!;
    LineEdit _relayAddress = null!;
    Button _localBleButton = null!;
    HFlowContainer _phones = null!;
    Control _manualRow = null!;
    Label _relayHint = null!;
    RelayBackend? _watchedRelay;

    const string RelayConfig = "user://relay.cfg";

    static bool IsPhone => OS.GetName() is "Android" or "iOS";

    /// <summary>Pas de capteurs possibles sans téléphone : pas de Bluetooth ici, ou relais perdu.</summary>
    bool NeedsPhone => !IsPhone && Sensors.Backend is null or RelayBackend { IsLost: true }
        or RelayBackend { IsLocalHelper: true, Problem: not null };
    bool _autoSearched;

    SensorHub Sensors => SensorHub.Instance;

    /// <summary>Ouvert pendant une sortie : appelé par « Revenir à la sortie » (null = premier écran, vers le menu).
    /// À renseigner avant l'ajout à l'arbre.</summary>
    public Action? ReturnToRide { get; set; }

    /// <summary>Bouton principal (pour les tests).</summary>
    internal Button ContinueButton => _continueButton;

    /// <summary>Ligne affichée pour un appareil (pour les tests).</summary>
    internal Control? RowFor(string address) => _rows.GetValueOrDefault(address)?.Panel;

    public override void _Ready()
    {
        FindNodes();
        Sensors.DevicesChanged += Refresh;
        Sensors.StatusMessage += ShowStatus;
        // Retour depuis le menu : on garde le transport, sinon les appareils seraient déconnectés.
        if (Sensors.Backend is null && SensorBackends.Create() is { } backend) Sensors.UseBackend(backend);
        _relayPanel.Visible = !IsPhone;
        // Sur PC, le mode relais sert aussi à tester à deux PC (l'un en mode debug prête ses capteurs simulés).
        var relayMode = GetNode<Button>("%RelayModeButton");
        relayMode.Visible = ReturnToRide is null && (IsPhone || Sensors.Backend is SimulatedBackend);
        relayMode.Text = RelayHost.Current is null ? "Relais pour PC" : "Relais pour PC : actif";
        WatchRelay();
        RefreshSource();
        if (!IsPhone)
        {
            _relayAddress.Text = LastRelay();
            // Sans Bluetooth sur ce PC, on cherche tout de suite les téléphones : le joueur choisit le sien.
            if (NeedsPhone) AutoFindPhones();
        }
        if (Sensors.Backend is null)
        {
            _scanButton.Disabled = true;
            ShowStatus(IsPhone
                ? "Le plugin Bluetooth n'est pas dans cette version de l'appli. À l'export : fichiers velom3d_ble-*.aar "
                  + "dans addons/velom3d_ble/bin/, extension « Velom3d BLE » activée et « Use Gradle Build » coché."
                : "");
            Refresh();
            return;
        }
        if (Sensors.Backend is SimulatedBackend { VaryEffort: true })
            ShowStatus("Mode debug : capteurs simulés, puissance, cadence et cardio générés automatiquement.");
        Sensors.StartScan();
    }

    public override void _ExitTree()
    {
        Sensors.DevicesChanged -= Refresh;
        Sensors.StatusMessage -= ShowStatus;
        if (_watchedRelay is not null && IsInstanceValid(_watchedRelay)) _watchedRelay.StateChanged -= RefreshSource;
    }

    public override void _Notification(int what)
    {
        // Pendant une sortie, Retour est géré par l'écran de sortie (retour à la sortie).
        if (what == NotificationWMGoBackRequest && ReturnToRide is null) GetTree().Quit();
    }

    public override void _Process(double delta)
    {
        _continueButton.Text = ReturnToRide is not null ? "Revenir à la sortie"
            : Sensors.Devices.Sorted.Any(d => d.State == LinkState.Connected) ? "Continuer" : "Continuer sans capteur";
        foreach (var (kind, _) in Slots)
        {
            var (panel, name, value) = _slots[kind];
            var device = Sensors.Devices.ConnectedFor(kind);
            name.Text = device?.DisplayName ?? "Non connecté";
            name.Modulate = device is null ? Muted : Colors.White;
            value.Text = device is null ? "--" : kind switch
            {
                DeviceKind.Trainer => $"{Sensors.Speed:0.0} km/h",
                DeviceKind.PowerMeter => $"{Sensors.Power} W  ·  {Sensors.Cadence:0} tr/min",
                _ => $"{Sensors.HeartRate} bpm",
            };
            SetConnected(panel, device is not null);
        }
    }

    /// <summary>Met les lignes à jour sur place : les recréer à chaque annonce Bluetooth
    /// (plusieurs par seconde) annulerait le geste de défilement en cours.</summary>
    void Refresh()
    {
        _scanButton.Text = Sensors.IsScanning ? "Arrêter la recherche" : "Rechercher";

        var devices = Sensors.Devices.Sorted;
        var present = new HashSet<string>();
        foreach (var d in devices) present.Add(d.Address);
        foreach (var gone in new List<string>(_rows.Keys))
        {
            if (present.Contains(gone)) continue;
            _rows[gone].Panel.QueueFree();
            _rows.Remove(gone);
        }

        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            if (!_rows.TryGetValue(d.Address, out var row))
            {
                row = CreateRow(d.Address);
                _rows[d.Address] = row;
                _list.AddChild(row.Panel);
            }
            UpdateRow(row, d);
            _list.MoveChild(row.Panel, i + 1); // l'index 0 est le message « liste vide »
        }

        _empty.Visible = devices.Count == 0;
        _empty.Text = Sensors.Backend is null ? ""
            : Sensors.IsScanning
                ? "Recherche en cours… Pédalez quelques tours pour réveiller le home trainer."
                : "Aucun appareil trouvé. Vérifiez qu'ils sont allumés et que le Bluetooth est activé, puis relancez la recherche.";
    }

    Row CreateRow(string address)
    {
        var panel = RowScene.Instantiate<PanelContainer>();
        var button = panel.GetNode<Button>("%ConnectButton");
        button.Pressed += () =>
        {
            if (Sensors.Devices.Get(address)?.State == LinkState.Connected) Sensors.DisconnectDevice(address);
            else Sensors.ConnectDevice(address);
        };
        return new Row(panel, panel.GetNode<Label>("%Name"), panel.GetNode<Label>("%Detail"), button);
    }

    void UpdateRow(Row row, DeviceEntry d)
    {
        SetConnected(row.Panel, d.State == LinkState.Connected);
        row.Name.Text = d.DisplayName;
        row.Detail.Text = $"{DeviceList.KindLabel(d.Kind)}  ·  signal {SignalLabel(d.Rssi)}";
        row.Button.Text = d.State switch
        {
            LinkState.Connected => "Déconnecter",
            LinkState.Connecting => "Connexion…",
            _ => "Connecter",
        };
        row.Button.Disabled = d.State == LinkState.Connecting;
    }

    static string SignalLabel(int rssi) => rssi switch
    {
        0 => "inconnu",
        >= -60 => "fort",
        >= -75 => "moyen",
        _ => "faible",
    };

    void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = text.Length > 0;
        if (text.Length > 0) GD.Print(text);
    }

    void OnScanPressed()
    {
        ShowStatus("");
        if (Sensors.IsScanning) Sensors.StopScan();
        else Sensors.StartScan();
    }

    // --- Source des capteurs sur PC : Bluetooth de ce PC, ou téléphone relais ---

    /// <summary>Change de transport (les appareils de l'ancien sont déconnectés) et relance la recherche.</summary>
    void SwitchBackend(SensorBackend backend)
    {
        ShowStatus("");
        Sensors.UseBackend(backend);
        WatchRelay();
        RefreshSource();
        _scanButton.Disabled = false;
        Sensors.StartScan(); // pour un relais : part dès que la connexion est établie
    }

    /// <summary>Utilise le Bluetooth d'un téléphone en mode relais.</summary>
    internal void UseRelay(string host, int port, string name)
    {
        var config = new ConfigFile();
        config.SetValue("relay", "address", port == RelayProtocol.TcpPort ? host : $"{host}:{port}");
        config.Save(RelayConfig);
        SwitchBackend(new RelayBackend(host, port, name));
    }

    static string LastRelay()
    {
        var config = new ConfigFile();
        return config.Load(RelayConfig) == Error.Ok ? config.GetValue("relay", "address", "").AsString() : "";
    }

    void WatchRelay()
    {
        if (_watchedRelay is not null && IsInstanceValid(_watchedRelay)) _watchedRelay.StateChanged -= RefreshSource;
        _watchedRelay = Sensors.Backend as RelayBackend;
        if (_watchedRelay is not null) _watchedRelay.StateChanged += RefreshSource;
    }

    void RefreshSource()
    {
        if (!IsInstanceValid(this)) return;
        _source.Text = Sensors.Backend switch
        {
            null => "Capteurs : pas de Bluetooth sur ce PC",
            SimulatedBackend => "Capteurs simulés (mode debug)",
            RelayBackend { IsLocalHelper: true } r => r.IsLost || r.Problem is not null ? "Bluetooth de ce PC indisponible"
                : r.IsRelayConnected ? "Capteurs via le Bluetooth de ce PC" : "Démarrage du Bluetooth de ce PC…",
            RelayBackend r => r.IsLost ? $"Téléphone {r.DisplayName} déconnecté"
                : r.IsRelayConnected ? $"Capteurs via le téléphone {r.DisplayName} ({r.Host})" : $"Connexion au téléphone {r.Host}…",
            _ => "Capteurs via le Bluetooth de cet appareil",
        };
        // « Revenir au Bluetooth du PC » : seulement quand on est passé par un téléphone et que ce PC a du Bluetooth.
        _localBleButton.Visible = WindowsBle.Available == true && Sensors.Backend is RelayBackend { IsLocalHelper: false };
        // Sans Bluetooth utilisable, rien à rechercher : on garde seulement les cases des capteurs.
        bool canSearch = Sensors.Backend is not null and not RelayBackend { IsLost: true } and not RelayBackend { Problem: not null };
        _scanButton.Visible = canSearch;
        _list.GetParent<Control>().Visible = canSearch;
        bool onPhone = Sensors.Backend is RelayBackend { IsLocalHelper: false, IsLost: false };
        _relayHint.Visible = !onPhone;
        if (onPhone)
        {
            _manualRow.Visible = false;
            foreach (var child in _phones.GetChildren()) child.QueueFree();
        }
        // Le Bluetooth du PC vient de se révéler absent : on cherche les téléphones.
        if (NeedsPhone) AutoFindPhones();
    }

    void AutoFindPhones()
    {
        if (_autoSearched) return;
        _autoSearched = true;
        FindPhones();
    }

    void OnRelayConnectPressed()
    {
        if (!RelayDiscovery.TryParseAddress(_relayAddress.Text, out var host, out var port))
        {
            ShowStatus("Adresse invalide : saisissez celle affichée sur le téléphone, par exemple 192.168.1.20.");
            return;
        }
        UseRelay(host, port, host);
    }

    void OnLocalBlePressed()
    {
        if (WindowsBle.Create() is { } backend) SwitchBackend(backend);
        else ShowStatus($"{WindowsBle.HelperExe} est introuvable à côté du jeu : réexportez le jeu (voir README).");
    }

    /// <summary>Cherche les téléphones en mode relais sur le réseau et en fait des boutons.</summary>
    async void FindPhones()
    {
        _findPhonesButton.Disabled = true;
        _findPhonesButton.Text = "Recherche…";
        foreach (var child in _phones.GetChildren()) child.QueueFree();
        List<RelayInfo> found;
        try
        {
            found = await RelayDiscovery.FindAsync(TimeSpan.FromSeconds(2.0));
        }
        catch (Exception e)
        {
            found = [];
            GD.PushWarning("Recherche des téléphones : " + e.Message);
        }
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        _findPhonesButton.Disabled = false;
        _findPhonesButton.Text = "Chercher les téléphones";
        foreach (var phone in found)
        {
            var button = new Button { Text = $"{phone.Name}  ·  {phone.Host}", CustomMinimumSize = new Vector2(0, 52) };
            button.Pressed += () => UseRelay(phone.Host, phone.Port, phone.Name);
            _phones.AddChild(button);
        }
        // L'adresse à saisir ne sert que si la recherche n'a rien donné.
        _manualRow.Visible = found.Count == 0;
        if (found.Count == 0)
        {
            ShowStatus("Aucun téléphone trouvé. Sur le téléphone : même Wi-Fi que le PC, Velom3d ouvert, « Relais pour PC » activé. "
                + "Sinon, saisissez l'adresse affichée sur le téléphone.");
            return;
        }
    }

    void FindNodes()
    {
        foreach (var (kind, node) in Slots)
        {
            var panel = GetNode<PanelContainer>(node);
            _slots[kind] = (panel, panel.GetNode<Label>("Box/Name"), panel.GetNode<Label>("Box/Value"));
        }
        _scanButton = GetNode<Button>("%ScanButton");
        _scanButton.Pressed += OnScanPressed;
        _continueButton = GetNode<Button>("%ContinueButton");
        _continueButton.Pressed += () =>
        {
            Sensors.StopScan();
            if (ReturnToRide is { } back) back();
            else Navigator.GoTo(this, Navigator.Menu);
        };
        _status = GetNode<Label>("%Status");
        _list = GetNode<VBoxContainer>("%List");
        _empty = GetNode<Label>("%Empty");
        _relayPanel = GetNode<PanelContainer>("%RelayPanel");
        _source = GetNode<Label>("%Source");
        _findPhonesButton = GetNode<Button>("%FindPhonesButton");
        _findPhonesButton.Pressed += () => { ShowStatus(""); FindPhones(); };
        _relayAddress = GetNode<LineEdit>("%RelayAddress");
        _relayAddress.TextSubmitted += _ => OnRelayConnectPressed();
        GetNode<Button>("%RelayConnectButton").Pressed += OnRelayConnectPressed;
        _localBleButton = GetNode<Button>("%LocalBleButton");
        _localBleButton.Pressed += OnLocalBlePressed;
        _phones = GetNode<HFlowContainer>("%Phones");
        _manualRow = GetNode<Control>("%ManualRow");
        _relayHint = GetNode<Label>("%RelayHint");
        GetNode<Button>("%RelayModeButton").Pressed += () =>
        {
            Sensors.StopScan();
            Navigator.GoTo(this, Navigator.Relay);
        };
    }

    /// <summary>Carte violette bordée quand l'appareil est connecté (styles du thème).</summary>
    static void SetConnected(PanelContainer panel, bool connected)
    {
        StringName variation = connected ? "ActivePanel" : "";
        if (panel.ThemeTypeVariation != variation) panel.ThemeTypeVariation = variation;
    }
}
