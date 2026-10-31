using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Premier écran (mise en page : DeviceConnectionScreen.tscn) : recherche et connexion du home trainer, du capteur de puissance et de la
/// ceinture cardio, par le Bluetooth du téléphone. Pensé pour le tactile (grandes cibles,
/// liste qui défile au doigt). Sans plugin Bluetooth natif (éditeur, ordinateur), l'écran
/// l'indique et ne cherche rien, sauf en mode debug (capteurs simulés, voir SensorBackends). « Continuer » mène au menu principal.
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

    SensorHub Sensors => SensorHub.Instance;

    /// <summary>Ligne affichée pour un appareil (pour les tests).</summary>
    internal Control? RowFor(string address) => _rows.GetValueOrDefault(address)?.Panel;

    public override void _Ready()
    {
        FindNodes();
        Sensors.DevicesChanged += Refresh;
        Sensors.StatusMessage += ShowStatus;
        // Retour depuis le menu : on garde le transport, sinon les appareils seraient déconnectés.
        if (Sensors.Backend is null && SensorBackends.Create() is { } backend) Sensors.UseBackend(backend);
        if (Sensors.Backend is null)
        {
            _scanButton.Disabled = true;
            ShowStatus(OS.GetName() == "Android"
                ? "Le plugin Bluetooth n'est pas dans cette version de l'appli. À l'export : fichiers velom3d_ble-*.aar "
                  + "dans addons/velom3d_ble/bin/, extension « Velom3d BLE » activée et « Use Gradle Build » coché."
                : $"Bluetooth indisponible ici : lancez le jeu sur un téléphone Android, ou en mode debug "
                  + $"({SensorBackends.DebugEnvVar}=1 ou {SensorBackends.DebugArg}) pour des capteurs simulés.");
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
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMGoBackRequest) GetTree().Quit();
    }

    public override void _Process(double delta)
    {
        _continueButton.Text = Sensors.Devices.Sorted.Any(d => d.State == LinkState.Connected) ? "Continuer" : "Continuer sans capteur";
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
            Navigator.GoTo(this, Navigator.Menu);
        };
        _status = GetNode<Label>("%Status");
        _list = GetNode<VBoxContainer>("%List");
        _empty = GetNode<Label>("%Empty");
    }

    /// <summary>Carte verte bordée quand l'appareil est connecté (styles du thème).</summary>
    static void SetConnected(PanelContainer panel, bool connected)
    {
        StringName variation = connected ? "ActivePanel" : "";
        if (panel.ThemeTypeVariation != variation) panel.ThemeTypeVariation = variation;
    }
}
