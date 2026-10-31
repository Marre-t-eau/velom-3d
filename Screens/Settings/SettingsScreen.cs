using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Velom3d.Rides;
using Velom3d.Sync;

namespace Velom3d.Screens;

/// <summary>
/// Réglages du cycliste et du vélo : FTP, poids, roue, et vitesses virtuelles (plateaux et
/// cassette, choisis parmi les transmissions courantes ou saisis à la main).
/// Chaque changement est enregistré tout de suite. Synchronisation facultative avec un serveur
/// (adresse modifiable, compte). Mise en page : SettingsScreen.tscn.
/// </summary>
public partial class SettingsScreen : Screen
{
    RiderProfile _profile = null!;
    Label _ftp = null!, _riderMass = null!, _bikeMass = null!, _gearSummary = null!;
    CheckButton _gearsToggle = null!;
    Control _gearOptions = null!;
    readonly List<(Button Chip, int Mm)> _wheelChips = new();
    readonly List<(Button Chip, int[] Teeth)> _chainringChips = new();
    readonly List<(Button Chip, int[] Teeth)> _cassetteChips = new();
    CheckButton _syncToggle = null!;
    Control _syncOptions = null!, _loginForm = null!, _account = null!;
    LineEdit _serverUrl = null!, _username = null!, _password = null!;
    Label _accountLabel = null!, _syncStatus = null!;
    Button _login = null!, _register = null!, _syncNow = null!, _logout = null!;
    bool _syncBusy;

    /// <summary>Profil en cours de modification (pour les tests).</summary>
    internal RiderProfile Profile => _profile;

    public override void _Ready()
    {
        _profile = Store.LoadProfile();
        GetNode<Button>("%BackButton").Pressed += OnBack;

        _ftp = Stepper("%FtpStepper", d => _profile.FtpW = Math.Clamp(_profile.FtpW + d * 5, 50, 600));
        _riderMass = Stepper("%RiderMassStepper", d => _profile.RiderMassKg = Math.Clamp(_profile.RiderMassKg + d, 30, 200));
        _bikeMass = Stepper("%BikeMassStepper", d => _profile.BikeMassKg = Math.Clamp(_profile.BikeMassKg + d * 0.5, 4, 30));

        // Les choix viennent des listes de Gearing : leurs boutons sont créés ici.
        var wheels = GetNode<Container>("%Wheels");
        foreach (var (label, mm) in Gearing.Wheels)
            _wheelChips.Add((Ui.Chip(wheels, $"{label}  {mm} mm", () => { _profile.WheelCircumferenceMm = mm; Changed(); }), mm));

        _gearsToggle = GetNode<CheckButton>("%GearsToggle");
        _gearsToggle.Toggled += on => { _profile.VirtualGears = on; Changed(); };
        _gearOptions = GetNode<Control>("%GearOptions");

        var rings = GetNode<Container>("%Chainrings");
        foreach (var p in Gearing.Chainrings)
            _chainringChips.Add((Ui.Chip(rings, p.Label, () => SetChainrings(p.Teeth)), p.Teeth));
        Custom("%ChainringsEdit", "%ChainringsError", Gearing.ParseChainrings, SetChainrings,
            "1 à 3 plateaux de 8 à 60 dents, séparés par « / ».");

        var cassettes = GetNode<Container>("%Cassettes");
        foreach (var (speeds, list) in Gearing.Cassettes)
        {
            var row = Ui.Flow(cassettes);
            var label = Ui.Label($"{speeds} v.", 20, Ui.Muted);
            label.CustomMinimumSize = new Vector2(70, 64);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(label);
            foreach (var c in list)
                _cassetteChips.Add((Ui.Chip(row, c.Label, () => SetCassette(c.Teeth)), c.Teeth));
        }
        Custom("%CassetteEdit", "%CassetteError", Gearing.ParseCassette, SetCassette,
            "5 à 14 pignons de 8 à 60 dents, séparés par des virgules.");

        _gearSummary = GetNode<Label>("%GearSummary");
        ReadySync();
        Refresh();
    }

    // ---- Synchronisation ----

    void ReadySync()
    {
        _syncToggle = GetNode<CheckButton>("%SyncToggle");
        _syncOptions = GetNode<Control>("%SyncOptions");
        _serverUrl = GetNode<LineEdit>("%ServerUrlEdit");
        _loginForm = GetNode<Control>("%SyncLoginForm");
        _username = GetNode<LineEdit>("%UsernameEdit");
        _password = GetNode<LineEdit>("%PasswordEdit");
        _login = GetNode<Button>("%LoginButton");
        _register = GetNode<Button>("%RegisterButton");
        _account = GetNode<Control>("%SyncAccount");
        _accountLabel = GetNode<Label>("%SyncAccountLabel");
        _syncNow = GetNode<Button>("%SyncNowButton");
        _logout = GetNode<Button>("%LogoutButton");
        _syncStatus = GetNode<Label>("%SyncStatus");

        var state = Sync.State;
        _serverUrl.Text = state.ServerUrl;
        _username.Text = state.Username;
        _syncToggle.Toggled += on =>
        {
            Sync.State.Enabled = on;
            Sync.SaveState();
            RefreshSync();
            if (on && Sync.State.LoggedIn) SyncNow();
        };
        _serverUrl.TextChanged += text => { Sync.State.ServerUrl = text.Trim(); Sync.SaveState(); };
        _login.Pressed += () => SignIn(register: false);
        _register.Pressed += () => SignIn(register: true);
        _password.TextSubmitted += _ => SignIn(register: false);
        _syncNow.Pressed += SyncNow;
        _logout.Pressed += async () =>
        {
            await Busy("Déconnexion…", Sync.LogoutAsync);
            if (IsInstanceValid(this)) _syncStatus.Text = "Déconnecté. Vos données restent sur cet appareil.";
        };
        RefreshSync();
    }

    async void SignIn(bool register)
    {
        if (_syncBusy) return;
        string url = _serverUrl.Text, user = _username.Text, password = _password.Text;
        bool ok = await Busy(register ? "Création du compte…" : "Connexion…",
            () => register ? Sync.RegisterAsync(url, user, password) : Sync.LoginAsync(url, user, password));
        if (!ok || !IsInstanceValid(this)) return;
        _password.Text = "";
        _serverUrl.Text = Sync.State.ServerUrl;
        SyncNow();
    }

    async void SyncNow()
    {
        if (_syncBusy) return;
        SyncReport? report = null;
        bool ok = await Busy("Synchronisation…", async () => report = await Sync.SyncAsync());
        if (!ok || !IsInstanceValid(this)) return;
        // Les réglages ont pu arriver d'un autre appareil.
        _profile = Store.LoadProfile();
        Refresh();
        _syncStatus.Text = Describe(report!) + " " + LastSync();
    }

    /// <summary>Fait une opération réseau en grisant les boutons ; false (et le message affiché) si elle échoue.</summary>
    async System.Threading.Tasks.Task<bool> Busy(string message, Func<System.Threading.Tasks.Task> work)
    {
        _syncBusy = true;
        RefreshSync();
        _syncStatus.Text = message;
        try
        {
            await work();
            return true;
        }
        catch (SyncException e)
        {
            if (IsInstanceValid(this)) _syncStatus.Text = e.Message;
            return false;
        }
        finally
        {
            _syncBusy = false;
            if (IsInstanceValid(this)) RefreshSync();
        }
    }

    void RefreshSync()
    {
        var state = Sync.State;
        _syncToggle.SetPressedNoSignal(state.Enabled);
        _syncOptions.Visible = state.Enabled;
        _loginForm.Visible = !state.LoggedIn;
        _account.Visible = state.LoggedIn;
        _serverUrl.Editable = !state.LoggedIn && !_syncBusy;
        _accountLabel.Text = $"Connecté : {state.Username}";
        foreach (var b in new[] { _login, _register, _syncNow, _logout }) b.Disabled = _syncBusy;
        if (!_syncBusy && _syncStatus.Text.Length == 0)
            _syncStatus.Text = state.LastError.Length > 0 ? state.LastError : LastSync();
    }

    static string Describe(SyncReport r) =>
        r.Sent + r.Received + r.Deleted == 0 ? "Tout est à jour."
        : $"{r.Sent} envoyé(s), {r.Received} reçu(s)" + (r.Deleted > 0 ? $", {r.Deleted} supprimé(s)." : ".");

    static string LastSync() => Sync.State.LastSyncAt is { } at
        ? $"Dernière synchronisation : {at.ToLocalTime():dd/MM/yyyy HH:mm}."
        : "";

    /// <summary>Branche les boutons − / + d'une ligne de réglage ; renvoie le label de la valeur.</summary>
    Label Stepper(string node, Action<int> change)
    {
        var row = GetNode<Control>(node + "/Row");
        row.GetNode<Button>("Minus").Pressed += () => { change(-1); Changed(); };
        row.GetNode<Button>("Plus").Pressed += () => { change(+1); Changed(); };
        return row.GetNode<Label>("Value");
    }

    /// <summary>Saisie libre ; appliquée dès qu'elle est valable.</summary>
    void Custom(string editNode, string errorNode, Func<string, int[]?> parse, Action<int[]> apply, string rule)
    {
        var edit = GetNode<LineEdit>(editNode);
        var err = GetNode<Label>(errorNode);
        edit.TextChanged += text =>
        {
            var teeth = parse(text);
            err.Text = text.Trim().Length == 0 || teeth is not null ? "" : rule;
            err.Visible = err.Text.Length > 0;
            if (teeth is not null) apply(teeth);
        };
    }

    void SetChainrings(int[] teeth)
    {
        _profile.Chainrings = teeth;
        Changed();
    }

    void SetCassette(int[] teeth)
    {
        _profile.Cogs = teeth;
        Changed();
    }

    void Changed()
    {
        Store.SaveProfile(_profile);
        Refresh();
    }

    void Refresh()
    {
        _ftp.Text = $"{_profile.FtpW} W";
        _riderMass.Text = $"{_profile.RiderMassKg:0} kg";
        _bikeMass.Text = $"{_profile.BikeMassKg:0.0} kg";
        foreach (var (chip, mm) in _wheelChips) chip.SetPressedNoSignal(mm == _profile.WheelCircumferenceMm);
        _gearsToggle.SetPressedNoSignal(_profile.VirtualGears);
        _gearOptions.Modulate = _profile.VirtualGears ? Colors.White : new Color(1, 1, 1, 0.45f);
        foreach (var (chip, teeth) in _chainringChips) chip.SetPressedNoSignal(Same(teeth, _profile.Chainrings));
        foreach (var (chip, teeth) in _cassetteChips) chip.SetPressedNoSignal(Same(teeth, _profile.Cogs));

        var d = _profile.CreateDrivetrain();
        double easiest = (double)d.Chainrings[^1] / d.Cogs[^1] * d.WheelCircumferenceM;
        double hardest = (double)d.Chainrings[0] / d.Cogs[0] * d.WheelCircumferenceM;
        _gearSummary.Text = $"{d.Describe()} : de {easiest:0.0} à {hardest:0.0} m par tour de pédale, "
                            + $"soit {easiest * 90 / 60 * 3.6:0} à {hardest * 90 / 60 * 3.6:0} km/h à 90 tr/min.";
    }

    static bool Same(int[] a, int[] b) => a.OrderBy(t => t).SequenceEqual(b.OrderBy(t => t));

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Menu);
}
