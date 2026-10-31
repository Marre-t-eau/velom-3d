using System.Linq;
using Godot;
using Velom3d.Relay;
using Velom3d.Sensors;

namespace Velom3d.Screens;

/// <summary>
/// Mode relais du téléphone (mise en page : RelayScreen.tscn) : le Bluetooth du téléphone sert au
/// PC, sur le réseau local (voir <see cref="RelayHost"/>). L'écran montre l'adresse à saisir sur le
/// PC si la découverte automatique échoue, le PC connecté, les capteurs et leurs mesures.
/// « Arrêter le relais » déconnecte tout et revient à la connexion des capteurs.
/// </summary>
public partial class RelayScreen : Screen
{
    RelayHost _host = null!;
    Label _state = null!;
    Label _address = null!;
    Label _values = null!;
    Label _devices = null!;
    Label _status = null!;
    PanelContainer _panel = null!;

    /// <summary>Le relais (pour les tests).</summary>
    internal RelayHost Host => _host;

    static SensorHub Hub => SensorHub.Instance;

    public override void _Ready()
    {
        _state = GetNode<Label>("%State");
        _address = GetNode<Label>("%Address");
        _values = GetNode<Label>("%Values");
        _devices = GetNode<Label>("%Devices");
        _status = GetNode<Label>("%Status");
        _panel = GetNode<PanelContainer>("%StatePanel");
        GetNode<Button>("%StopButton").Pressed += OnBack;
        Hub.StatusMessage += ShowStatus;

        _host = new RelayHost { Name = "RelayHost" };
        _host.Changed += Refresh;
        AddChild(_host);
        // Le PC peut piloter le trainer pendant une heure : l'écran ne doit pas se mettre en veille.
        DisplayServer.ScreenSetKeepOn(true);
        Refresh();
    }

    public override void _ExitTree()
    {
        Hub.StatusMessage -= ShowStatus;
        DisplayServer.ScreenSetKeepOn(false);
    }

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Connection);

    public override void _Process(double delta)
    {
        var connected = Hub.Devices.Sorted.Where(d => d.State == LinkState.Connected).ToList();
        _devices.Text = connected.Count == 0
            ? (_host.ClientName is null ? "" : "Aucun capteur connecté : le PC les recherche et les connecte.")
            : "Capteurs utilisés par le PC :\n" + string.Join("\n", connected.Select(d => $"•  {d.DisplayName}  ({DeviceList.KindLabel(d.Kind)})"));
        _values.Text = connected.Count == 0 ? ""
            : $"{Hub.Power} W  ·  {Hub.Cadence:0} tr/min" + (Hub.HeartRate > 0 ? $"  ·  {Hub.HeartRate} bpm" : "");
    }

    void Refresh()
    {
        bool active = _host.ClientName is not null;
        StringName variation = active ? "ActivePanel" : "";
        if (_panel.ThemeTypeVariation != variation) _panel.ThemeTypeVariation = variation;
        if (_host.StartError is { } error)
        {
            _state.Text = "Relais impossible";
            _address.Text = error;
            return;
        }
        _state.Text = active ? $"PC connecté : {_host.ClientName}" : "En attente du PC…";
        var ips = RelayHost.LocalAddresses();
        _address.Text = ips.Count == 0
            ? "Pas de réseau : connectez le téléphone au même Wi-Fi que le PC."
            : $"Adresse de ce téléphone, si le PC ne le trouve pas tout seul : {string.Join("  ou  ", ips)}";
        if (_host.DiscoveryProblem is not null)
            _address.Text += "\nDécouverte automatique indisponible : saisissez cette adresse sur le PC.";
        if (!active && _host.LastLoss is { Length: > 0 } loss) ShowStatus("Le PC s'est déconnecté. " + loss);
        else if (active) ShowStatus("");
    }

    void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = text.Length > 0;
    }
}
