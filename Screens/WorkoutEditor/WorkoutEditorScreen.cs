using System;
using System.Globalization;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>
/// Édition d'un entraînement : nom, unité (watts ou % de FTP), et la liste des blocs. Chaque bloc
/// a une durée, une puissance fixe, libre ou en rampe, et une cadence libre ou imposée.
/// Les valeurs se règlent avec − / + ou se tapent directement. Tout est enregistré à chaque changement.
/// </summary>
public partial class WorkoutEditorScreen : Screen
{
    /// <summary>Entraînement à modifier, à renseigner avant l'ouverture.</summary>
    public string WorkoutId { get; set; } = "";

    Workout _w = null!;
    int _ftpW;
    WorkoutChart _chart = null!;
    Label _summary = null!;
    Button _wattsChip = null!, _percentChip = null!, _deleteButton = null!;
    VBoxContainer _blocks = null!;
    ScrollContainer _scroll = null!;
    double _deleteArmedUntil = -1;
    static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Entraînement en cours de modification (pour les tests).</summary>
    internal Workout Workout => _w;

    public override void _Ready()
    {
        if (Workouts.Load(WorkoutId) is not { } w)
        {
            Navigator.GoTo(this, Navigator.Workouts);
            return;
        }
        _w = w;
        _ftpW = Store.LoadProfile().FtpW;
        var root = Ui.BuildPage(this, 14);

        var header = Ui.Row();
        root.AddChild(header);
        var name = new LineEdit
        {
            Text = _w.Name, PlaceholderText = "Nom de l'entraînement", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 72),
        };
        name.AddThemeFontSizeOverride("font_size", 28);
        name.TextChanged += text => { _w.Name = text.Trim(); Save(); };
        header.AddChild(name);
        header.AddChild(Ui.Button("Retour", OnBack, 200, 72));

        var units = Ui.Row(12);
        root.AddChild(units);
        var unitLabel = Ui.Label("Puissances en", 20, Ui.Muted);
        unitLabel.VerticalAlignment = VerticalAlignment.Center;
        unitLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        units.AddChild(unitLabel);
        _wattsChip = Ui.Chip(units, "watts", () => SetUnit(PowerUnit.Watts));
        _percentChip = Ui.Chip(units, "% de FTP", () => SetUnit(PowerUnit.PercentFtp));
        _summary = Ui.Label("", 20, Ui.Muted);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        units.AddChild(_summary);

        _chart = new WorkoutChart { Workout = _w, FtpW = _ftpW };
        root.AddChild(_chart);

        var content = Ui.AddScroll(root, 14);
        _scroll = (ScrollContainer)content.GetParent();
        _blocks = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _blocks.AddThemeConstantOverride("separation", 14);
        content.AddChild(_blocks);

        var actions = Ui.Row();
        content.AddChild(actions);
        var add = Ui.Button("Ajouter un bloc", AddBlock, 300, 80);
        add.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(add);
        _deleteButton = Ui.Button("Supprimer l'entraînement", OnDeletePressed, 360, 80, 20);
        _deleteButton.Modulate = Ui.Danger;
        actions.AddChild(_deleteButton);

        RebuildBlocks();
    }

    public override void _Process(double delta)
    {
        if (_deleteButton is not null)
            _deleteButton.Text = Now <= _deleteArmedUntil ? "Toucher encore pour supprimer" : "Supprimer l'entraînement";
    }

    // --- Blocs ---

    /// <summary>Reconstruit la liste des blocs (changement de structure) sans perdre la position du défilement.</summary>
    void RebuildBlocks()
    {
        int scroll = _scroll.ScrollVertical;
        foreach (var child in _blocks.GetChildren()) child.QueueFree();
        for (int i = 0; i < _w.Blocks.Count; i++) _blocks.AddChild(BlockCard(i));
        if (_w.Blocks.Count == 0)
            _blocks.AddChild(Ui.Label("Pas encore de bloc : touchez « Ajouter un bloc ».", 22, Ui.Muted));
        Callable.From(() => _scroll.ScrollVertical = scroll).CallDeferred();
        Refresh();
    }

    Control BlockCard(int index)
    {
        var b = _w.Blocks[index];
        var panel = Ui.Panel(Ui.Card);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);

        var top = Ui.Row(10);
        box.AddChild(top);
        var title = Ui.Label($"Bloc {index + 1}", 26);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.VerticalAlignment = VerticalAlignment.Center;
        top.AddChild(title);
        var up = Ui.Button("Monter", () => Move(index, -1), 140, 64, 20);
        up.Disabled = index == 0;
        top.AddChild(up);
        var down = Ui.Button("Descendre", () => Move(index, +1), 160, 64, 20);
        down.Disabled = index == _w.Blocks.Count - 1;
        top.AddChild(down);
        top.AddChild(Ui.Button("Dupliquer", () => DuplicateBlock(index), 160, 64, 20));
        var remove = Ui.Button("Supprimer", () => Remove(index), 160, 64, 20);
        remove.Modulate = Ui.Danger;
        top.AddChild(remove);

        var kinds = Ui.Flow(box);
        AddLabel(kinds, "Puissance");
        foreach (var (kind, text) in new[] { (BlockPower.Fixed, "Fixe"), (BlockPower.Free, "Libre"), (BlockPower.Ramp, "Rampe") })
            Ui.Chip(kinds, text, () => SetKind(b, kind)).SetPressedNoSignal(b.Kind == kind);
        AddLabel(kinds, "   Cadence");
        Ui.Chip(kinds, "Libre", () => { b.CadenceRpm = 0; Save(); RebuildBlocks(); }).SetPressedNoSignal(b.FreeCadence);
        Ui.Chip(kinds, "Imposée", () => { if (b.FreeCadence) b.CadenceRpm = 90; Save(); RebuildBlocks(); })
            .SetPressedNoSignal(!b.FreeCadence);

        Field(box, "Durée", () => b.DurationS, v => b.DurationS = v, 15, 60,
            WorkoutBlock.MinDurationS, WorkoutBlock.MaxDurationS, v => Ui.Duration(v), "", ParseDuration);
        var (min, max, step, big) = _w.PowerRange;
        string unit = _w.Unit == PowerUnit.Watts ? "W" : "% FTP";
        if (b.Kind == BlockPower.Fixed)
            Field(box, "Puissance", () => b.Power, v => b.Power = v, step, big, min, max, Int, unit, ParseInt);
        else if (b.Kind == BlockPower.Ramp)
        {
            Field(box, "Puissance au début", () => b.Power, v => b.Power = v, step, big, min, max, Int, unit, ParseInt);
            Field(box, "Puissance à la fin", () => b.PowerEnd, v => b.PowerEnd = v, step, big, min, max, Int, unit, ParseInt);
        }
        else
            box.AddChild(Ui.Label("Pas de consigne : le trainer passe en simulation sur le plat, vous roulez comme vous voulez.", 18, Ui.Muted));
        if (!b.FreeCadence)
            Field(box, "Cadence", () => b.CadenceRpm, v => b.CadenceRpm = v, 5, 0,
                WorkoutBlock.MinCadenceRpm, WorkoutBlock.MaxCadenceRpm, Int, "tr/min", ParseInt);
        return panel;
    }

    void SetKind(WorkoutBlock b, BlockPower kind)
    {
        // Une rampe toute neuve monte un peu à partir de la puissance du bloc.
        if (kind == BlockPower.Ramp && b.Kind != BlockPower.Ramp) b.PowerEnd = b.Power + _w.PowerRange.Step * 10;
        b.Kind = kind;
        _w.Clamp();
        Save();
        RebuildBlocks();
    }

    static void AddLabel(Container parent, string text)
    {
        var label = Ui.Label(text, 20, Ui.Muted);
        label.CustomMinimumSize = new Vector2(0, 64);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        parent.AddChild(label);
    }

    static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Ligne « libellé  [−−] [−] saisie [+] [++]  unité » ; la saisie est appliquée à la validation ou en quittant le champ.</summary>
    void Field(Container parent, string title, Func<int> get, Action<int> set, int step, int bigStep, int min, int max,
        Func<int, string> format, string unit, Func<string, int?> parse)
    {
        var row = Ui.Row(10);
        parent.AddChild(row);
        var label = Ui.Label(title, 22);
        label.CustomMinimumSize = new Vector2(250, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);
        var edit = new LineEdit { CustomMinimumSize = new Vector2(150, 64), Alignment = HorizontalAlignment.Center, SelectAllOnFocus = true };
        edit.AddThemeFontSizeOverride("font_size", 26);
        void Apply(int v)
        {
            set(Math.Clamp(v, min, max));
            edit.Text = format(get());
            Save();
        }
        if (bigStep > 0) row.AddChild(Ui.Button($"−{BigLabel(bigStep, unit)}", () => Apply(get() - bigStep), 110, 64, 20));
        row.AddChild(Ui.Button("−", () => Apply(get() - step), 80, 64, 28));
        edit.Text = format(get());
        edit.TextSubmitted += text => Apply(parse(text) ?? get());
        edit.FocusExited += () => Apply(parse(edit.Text) ?? get());
        row.AddChild(edit);
        row.AddChild(Ui.Button("+", () => Apply(get() + step), 80, 64, 28));
        if (bigStep > 0) row.AddChild(Ui.Button($"+{BigLabel(bigStep, unit)}", () => Apply(get() + bigStep), 110, 64, 20));
        var unitLabel = Ui.Label(unit, 20, Ui.Muted);
        unitLabel.VerticalAlignment = VerticalAlignment.Center;
        unitLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        row.AddChild(unitLabel);
    }

    /// <summary>Libellé du grand pas : « 25 », ou « 1 min » pour une durée (sans unité affichée).</summary>
    static string BigLabel(int step, string unit) => unit.Length > 0 ? Int(step) : step % 60 == 0 ? $"{step / 60} min" : $"{step} s";

    static int? ParseInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>« 1:30 », « 1:02:00 », ou un nombre de minutes (« 5 », « 2,5 »).</summary>
    internal static int? ParseDuration(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return null;
        if (!text.Contains(':'))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
                ? (int)Math.Round(minutes * 60) : null;
        int total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return total;
    }

    internal void AddBlock()
    {
        // Le nouveau bloc reprend le dernier (on enchaîne souvent des blocs semblables).
        var block = _w.Blocks.Count > 0 ? _w.Blocks[^1].Clone()
            : new WorkoutBlock { Power = _w.Unit == PowerUnit.Watts ? 150 : 75 };
        _w.Blocks.Add(block);
        Save();
        RebuildBlocks();
        Callable.From(() => _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue).CallDeferred();
    }

    void DuplicateBlock(int index)
    {
        _w.Blocks.Insert(index + 1, _w.Blocks[index].Clone());
        Save();
        RebuildBlocks();
    }

    void Remove(int index)
    {
        _w.Blocks.RemoveAt(index);
        Save();
        RebuildBlocks();
    }

    void Move(int index, int delta)
    {
        int to = index + delta;
        if (to < 0 || to >= _w.Blocks.Count) return;
        (_w.Blocks[index], _w.Blocks[to]) = (_w.Blocks[to], _w.Blocks[index]);
        Save();
        RebuildBlocks();
    }

    internal void SetUnit(PowerUnit unit)
    {
        _w.ConvertTo(unit, _ftpW);
        _w.Clamp();
        Save();
        RebuildBlocks();
    }

    void Save()
    {
        _w.UpdatedAt = DateTimeOffset.Now;
        Workouts.Save(_w);
        Refresh();
    }

    void Refresh()
    {
        _wattsChip.SetPressedNoSignal(_w.Unit == PowerUnit.Watts);
        _percentChip.SetPressedNoSignal(_w.Unit == PowerUnit.PercentFtp);
        _summary.Text = $"{Ui.Duration(_w.TotalDurationS)} · {_w.Blocks.Count} bloc(s) · TSS prévu {_w.EstimatedTss(_ftpW):0}"
                        + (_w.Unit == PowerUnit.PercentFtp ? $" · FTP {_ftpW} W" : "");
        _chart.QueueRedraw();
    }

    void OnDeletePressed()
    {
        if (Now > _deleteArmedUntil)
        {
            _deleteArmedUntil = Now + 3.0;
            return;
        }
        Workouts.Delete(_w.Id);
        Navigator.GoTo(this, Navigator.Workouts);
    }

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Workouts);
}
