using System.Collections.Generic;
using Godot;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Premier écran : recherche et connexion du home trainer, du capteur de puissance et de la
/// ceinture cardio. Pensé pour le tactile (grandes cibles, liste qui défile au doigt).
/// Sans plugin Bluetooth natif (éditeur, ordinateur), il bascule sur des appareils de démo.
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

    readonly Dictionary<DeviceKind, (PanelContainer Panel, Label Name, Label Value)> _slots = new();
    Button _scanButton = null!;
    CheckButton _demoToggle = null!;
    Label _status = null!;
    VBoxContainer _list = null!;
    Label _empty = null!;

    SensorHub Sensors => SensorHub.Instance;
    bool NativeAvailable => NativeBleBackend.IsAvailable;

    public override void _Ready()
    {
        BuildUi();
        Sensors.DevicesChanged += Refresh;
        Sensors.StatusMessage += ShowStatus;
        UseDemo(!NativeAvailable);
    }

    public override void _ExitTree()
    {
        Sensors.DevicesChanged -= Refresh;
        Sensors.StatusMessage -= ShowStatus;
    }

    void UseDemo(bool demo)
    {
        _demoToggle.SetPressedNoSignal(demo);
        Sensors.UseBackend(demo ? new SimulatedBackend() : new NativeBleBackend());
        ShowStatus(demo
            ? NativeAvailable ? "Mode démo : appareils simulés." : "Bluetooth natif indisponible ici : appareils de démo."
            : "");
        Sensors.StartScan();
    }

    public override void _Process(double delta)
    {
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

    void Refresh()
    {
        _scanButton.Text = Sensors.IsScanning ? "Arrêter la recherche" : "Rechercher";

        foreach (var child in _list.GetChildren())
            if (child != _empty) child.QueueFree();

        var devices = Sensors.Devices.Sorted;
        _empty.Visible = devices.Count == 0;
        _empty.Text = Sensors.IsScanning
            ? "Recherche en cours… Pédalez quelques tours pour réveiller le home trainer."
            : "Aucun appareil trouvé. Vérifiez qu'ils sont allumés et que le Bluetooth est activé, puis relancez la recherche.";
        foreach (var d in devices) _list.AddChild(DeviceRow(d));
    }

    Control DeviceRow(DeviceEntry d)
    {
        var panel = new PanelContainer();
        SetPanelColor(panel, d.State == LinkState.Connected ? CardConnected : Card);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        panel.AddChild(row);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddChild(MakeLabel(d.DisplayName, 24));
        var detail = MakeLabel($"{DeviceList.KindLabel(d.Kind)}  ·  signal {SignalLabel(d.Rssi)}", 18);
        detail.Modulate = Muted;
        text.AddChild(detail);
        row.AddChild(text);

        var button = new Button
        {
            Text = d.State switch
            {
                LinkState.Connected => "Déconnecter",
                LinkState.Connecting => "Connexion…",
                _ => "Connecter",
            },
            Disabled = d.State == LinkState.Connecting,
            CustomMinimumSize = new Vector2(220, 72),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        button.AddThemeFontSizeOverride("font_size", 22);
        string address = d.Address;
        button.Pressed += () =>
        {
            if (Sensors.Devices.Get(address)?.State == LinkState.Connected) Sensors.DisconnectDevice(address);
            else Sensors.ConnectDevice(address);
        };
        row.AddChild(button);
        return panel;
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
        if (!_demoToggle.ButtonPressed) ShowStatus("");
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
        _demoToggle = new CheckButton
        {
            Text = "Mode démo",
            Disabled = !NativeAvailable,
            TooltipText = NativeAvailable ? "Utiliser des appareils simulés" : "Pas de Bluetooth natif sur cette plateforme",
        };
        _demoToggle.AddThemeFontSizeOverride("font_size", 22);
        _demoToggle.Toggled += UseDemo;
        actions.AddChild(_demoToggle);

        _status = MakeLabel("", 20);
        _status.Modulate = Warning;
        _status.Visible = false;
        root.AddChild(_status);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_list);
        _empty = MakeLabel("", 20);
        _empty.Modulate = Muted;
        _list.AddChild(_empty);
    }

    static Label MakeLabel(string text, int size)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
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
