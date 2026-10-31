using System;
using System.Globalization;
using Godot;
using Velom3d.Rides;

namespace Velom3d.Screens;

/// <summary>
/// Édition d'un entraînement : nom, unité (watts ou % de FTP), et la liste des blocs. Chaque bloc
/// a une durée, une puissance fixe, libre ou en rampe, et une cadence libre ou imposée.
/// Les valeurs se règlent avec − / + ou se tapent directement. Tout est enregistré à chaque changement.
/// Mise en page : WorkoutEditorScreen.tscn, et BlockCard.tscn pour chaque bloc.
/// </summary>
public partial class WorkoutEditorScreen : Screen
{
    /// <summary>Entraînement à modifier, à renseigner avant l'ouverture.</summary>
    public string WorkoutId { get; set; } = "";
    /// <summary>Carte d'un bloc (BlockCard.tscn).</summary>
    [Export] public PackedScene BlockScene { get; set; } = null!;

    Workout _w = null!;
    int _ftpW;
    WorkoutChart _chart = null!;
    Label _summary = null!;
    Button _wattsChip = null!, _percentChip = null!, _deleteButton = null!;
    VBoxContainer _blocks = null!;
    Label _empty = null!;
    ScrollContainer _scroll = null!;

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

        var name = GetNode<LineEdit>("%NameEdit");
        name.Text = _w.Name;
        name.TextChanged += text => { _w.Name = text.Trim(); Save(); };
        GetNode<Button>("%BackButton").Pressed += OnBack;

        _wattsChip = GetNode<Button>("%WattsChip");
        _wattsChip.Pressed += () => SetUnit(PowerUnit.Watts);
        _percentChip = GetNode<Button>("%PercentChip");
        _percentChip.Pressed += () => SetUnit(PowerUnit.PercentFtp);
        _summary = GetNode<Label>("%Summary");

        _chart = GetNode<WorkoutChart>("%Chart");
        _chart.Workout = _w;
        _chart.FtpW = _ftpW;

        _scroll = GetNode<ScrollContainer>("%Scroll");
        _blocks = GetNode<VBoxContainer>("%Blocks");
        _empty = GetNode<Label>("%Empty");
        GetNode<Button>("%AddButton").Pressed += AddBlock;
        _deleteButton = GetNode<Button>("%DeleteButton");
        _deleteButton.Pressed += OnDeletePressed;

        RebuildBlocks();
    }

    // --- Blocs ---

    /// <summary>Reconstruit la liste des blocs (changement de structure) sans perdre la position du défilement.</summary>
    void RebuildBlocks()
    {
        int scroll = _scroll.ScrollVertical;
        foreach (var child in _blocks.GetChildren()) child.QueueFree();
        for (int i = 0; i < _w.Blocks.Count; i++) _blocks.AddChild(BlockCard(i));
        _empty.Visible = _w.Blocks.Count == 0;
        Callable.From(() => _scroll.ScrollVertical = scroll).CallDeferred();
        Refresh();
    }

    Control BlockCard(int index)
    {
        var b = _w.Blocks[index];
        var card = BlockScene.Instantiate<PanelContainer>();
        card.GetNode<Label>("%Title").Text = $"Bloc {index + 1}";
        var up = card.GetNode<Button>("%UpButton");
        up.Disabled = index == 0;
        up.Pressed += () => Move(index, -1);
        var down = card.GetNode<Button>("%DownButton");
        down.Disabled = index == _w.Blocks.Count - 1;
        down.Pressed += () => Move(index, +1);
        card.GetNode<Button>("%DuplicateButton").Pressed += () => DuplicateBlock(index);
        card.GetNode<Button>("%RemoveButton").Pressed += () => Remove(index);

        foreach (var (kind, node) in new[] { (BlockPower.Fixed, "%FixedChip"), (BlockPower.Free, "%FreeChip"), (BlockPower.Ramp, "%RampChip") })
        {
            var chip = card.GetNode<Button>(node);
            chip.SetPressedNoSignal(b.Kind == kind);
            chip.Pressed += () => SetKind(b, kind);
        }
        var freeCadence = card.GetNode<Button>("%FreeCadenceChip");
        freeCadence.SetPressedNoSignal(b.FreeCadence);
        freeCadence.Pressed += () => { b.CadenceRpm = 0; Save(); RebuildBlocks(); };
        var fixedCadence = card.GetNode<Button>("%FixedCadenceChip");
        fixedCadence.SetPressedNoSignal(!b.FreeCadence);
        fixedCadence.Pressed += () => { if (b.FreeCadence) b.CadenceRpm = 90; Save(); RebuildBlocks(); };

        Field(card.GetNode<Control>("%DurationField"), null, () => b.DurationS, v => b.DurationS = v, 15, 60,
            WorkoutBlock.MinDurationS, WorkoutBlock.MaxDurationS, v => Ui.Duration(v), "", ParseDuration);
        var (min, max, step, big) = _w.PowerRange;
        string unit = _w.Unit == PowerUnit.Watts ? "W" : "% FTP";
        var power = card.GetNode<Control>("%PowerField");
        var powerEnd = card.GetNode<Control>("%PowerEndField");
        power.Visible = b.Kind != BlockPower.Free;
        powerEnd.Visible = b.Kind == BlockPower.Ramp;
        card.GetNode<Control>("%FreeNote").Visible = b.Kind == BlockPower.Free;
        if (b.Kind != BlockPower.Free)
            Field(power, b.Kind == BlockPower.Ramp ? "Puissance au début" : "Puissance", () => b.Power, v => b.Power = v,
                step, big, min, max, Int, unit, ParseInt);
        if (b.Kind == BlockPower.Ramp)
            Field(powerEnd, null, () => b.PowerEnd, v => b.PowerEnd = v, step, big, min, max, Int, unit, ParseInt);
        var cadence = card.GetNode<Control>("%CadenceField");
        cadence.Visible = !b.FreeCadence;
        if (!b.FreeCadence)
            Field(cadence, null, () => b.CadenceRpm, v => b.CadenceRpm = v, 5, 0,
                WorkoutBlock.MinCadenceRpm, WorkoutBlock.MaxCadenceRpm, Int, "tr/min", ParseInt);
        return card;
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

    static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Branche une ligne « libellé  [−−] [−] saisie [+] [++]  unité » de la carte (sans grand pas : pas de [−−] [++]) ;
    /// la saisie est appliquée à la validation ou en quittant le champ. <paramref name="title"/> null garde le libellé de la scène.</summary>
    void Field(Control row, string? title, Func<int> get, Action<int> set, int step, int bigStep, int min, int max,
        Func<int, string> format, string unit, Func<string, int?> parse)
    {
        if (title is not null) row.GetNode<Label>("Title").Text = title;
        var edit = row.GetNode<LineEdit>("Edit");
        void Apply(int v)
        {
            set(Math.Clamp(v, min, max));
            edit.Text = format(get());
            Save();
        }
        var bigMinus = row.GetNode<Button>("BigMinus");
        var bigPlus = row.GetNode<Button>("BigPlus");
        bigMinus.Visible = bigPlus.Visible = bigStep > 0;
        bigMinus.Text = $"−{BigLabel(bigStep, unit)}";
        bigPlus.Text = $"+{BigLabel(bigStep, unit)}";
        bigMinus.Pressed += () => Apply(get() - bigStep);
        bigPlus.Pressed += () => Apply(get() + bigStep);
        row.GetNode<Button>("Minus").Pressed += () => Apply(get() - step);
        row.GetNode<Button>("Plus").Pressed += () => Apply(get() + step);
        edit.Text = format(get());
        edit.TextSubmitted += text => Apply(parse(text) ?? get());
        edit.FocusExited += () => Apply(parse(edit.Text) ?? get());
        row.GetNode<Label>("Unit").Text = unit;
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

    internal void OnDeletePressed() => Confirm("Supprimer cet entraînement ?", "Les sorties déjà faites avec lui restent dans l'historique.", "Oui, supprimer", () =>
    {
        Workouts.Delete(_w.Id);
        Navigator.GoTo(this, Navigator.Workouts);
    });

    protected override void OnBack() => Navigator.GoTo(this, Navigator.Workouts);
}
