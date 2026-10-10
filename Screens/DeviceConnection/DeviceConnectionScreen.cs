using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Premier écran : recherche et connexion du home trainer, du capteur de puissance et de la
/// ceinture cardio, par le Bluetooth du téléphone. Pensé pour le tactile (grandes cibles,
/// liste qui défile au doigt). Sans plugin Bluetooth natif (éditeur, ordinateur), l'écran
/// l'indique et ne cherche rien. « Continuer » mène au menu principal.
/// </summary>
public partial class DeviceConnectionScreen : Control
{
    static readonly Color Background = new(0.07f, 0.08f, 0.10f);
    static readonly Color Card = new(0.13f, 0.15f, 0.18f);
    static readonly Color CardConnected = new(0.10f, 0.24f, 0.17f);
    static readonly Color Muted = new(0.62f, 0.66f, 0.72f);
    static readonly Color Accent = new(0.22f, 0.78f, 0.47f);
    static readonly Color Warning = new(1.0f, 0.72f, 0.30f);

    static readonly DeviceKind[] SlotKinds = [DeviceKind.Trainer, DeviceKind.PowerMeter, DeviceKind.HeartRate];

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
        BuildUi();
        Sensors.DevicesChanged += Refresh;
        Sensors.StatusMessage += ShowStatus;
        // Retour depuis le menu : on garde le transport, sinon les appareils seraient déconnectés.
        if (NativeBleBackend.IsAvailable && Sensors.Backend is not NativeBleBackend) Sensors.UseBackend(new NativeBleBackend());
        // Sans plugin natif, seul un transport déjà branché (tests automatiques) peut servir.
        if (Sensors.Backend is null)
        {
            _scanButton.Disabled = true;
            ShowStatus(OS.GetName() == "Android"
                ? "Le plugin Bluetooth n'est pas dans cette version de l'appli. À l'export : fichiers velom3d_ble-*.aar "
                  + "dans addons/velom3d_ble/bin/, extension « Velom3d BLE » activée et « Use Gradle Build » coché."
                : "Bluetooth indisponible ici : lancez le jeu sur un téléphone Android.");
            Refresh();
            return;
        }
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
        foreach (var kind in SlotKinds)
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
            SetPanelColor(panel, device is null ? Card : CardConnected);
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
        // Pass partout : un glissé qui commence sur une ligne ou un bouton doit faire défiler la liste.
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 16);
        panel.AddChild(row);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        var name = MakeLabel("", 24);
        text.AddChild(name);
        var detail = MakeLabel("", 18);
        detail.Modulate = Muted;
        text.AddChild(detail);
        row.AddChild(text);

        var button = new Button
        {
            CustomMinimumSize = new Vector2(220, 72),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Pass,
        };
        button.AddThemeFontSizeOverride("font_size", 22);
        button.Pressed += () =>
        {
            if (Sensors.Devices.Get(address)?.State == LinkState.Connected) Sensors.DisconnectDevice(address);
            else Sensors.ConnectDevice(address);
        };
        row.AddChild(button);
        return new Row(panel, name, detail, button);
    }

    void UpdateRow(Row row, DeviceEntry d)
    {
        SetPanelColor(row.Panel, d.State == LinkState.Connected ? CardConnected : Card);
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

    void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 18);
        margin.AddChild(root);

        root.AddChild(MakeLabel("Connexion des capteurs", 40));
        var hint = MakeLabel("Allumez votre home trainer et vos capteurs, puis touchez « Connecter ».", 20);
        hint.Modulate = Muted;
        root.AddChild(hint);

        var slots = new HBoxContainer();
        slots.AddThemeConstantOverride("separation", 16);
        root.AddChild(slots);
        foreach (var kind in SlotKinds)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var box = new VBoxContainer();
            panel.AddChild(box);
            var title = MakeLabel(DeviceList.KindLabel(kind), 18);
            title.Modulate = Muted;
            box.AddChild(title);
            var name = MakeLabel("", 22);
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.ClipText = true;
            box.AddChild(name);
            var value = MakeLabel("--", 30);
            box.AddChild(value);
            slots.AddChild(panel);
            _slots[kind] = (panel, name, value);
        }

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 16);
        root.AddChild(actions);
        _scanButton = new Button { Text = "Rechercher", CustomMinimumSize = new Vector2(320, 72) };
        _scanButton.AddThemeFontSizeOverride("font_size", 24);
        _scanButton.Pressed += OnScanPressed;
        actions.AddChild(_scanButton);
        _continueButton = new Button { Text = "Continuer", CustomMinimumSize = new Vector2(320, 72), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _continueButton.AddThemeFontSizeOverride("font_size", 24);
        _continueButton.Pressed += () =>
        {
            Sensors.StopScan();
            Navigator.GoTo(this, Navigator.Menu);
        };
        actions.AddChild(_continueButton);

        _status = MakeLabel("", 20);
        _status.Modulate = Warning;
        _status.Visible = false;
        root.AddChild(_status);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            // Au doigt : un petit mouvement suffit à faire défiler plutôt qu'à appuyer.
            ScrollDeadzone = 12,
        };
        root.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        _list.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_list);
        _empty = MakeLabel("", 20);
        _empty.Modulate = Muted;
        _list.AddChild(_empty);
    }

    static Label MakeLabel(string text, int size)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Pass };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    static void SetPanelColor(PanelContainer panel, Color color)
    {
        if (panel.GetThemeStylebox("panel") is StyleBoxFlat { } current && current.BgColor == color
            && panel.HasThemeStyleboxOverride("panel")) return;
        var style = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 14, ContentMarginBottom = 14 };
        style.SetCornerRadiusAll(14);
        if (color == CardConnected) { style.BorderColor = Accent; style.SetBorderWidthAll(2); }
        panel.AddThemeStyleboxOverride("panel", style);
    }
}
