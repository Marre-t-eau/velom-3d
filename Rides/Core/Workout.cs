using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Velom3d.Rides;

/// <summary>Comment un bloc fixe la puissance.</summary>
public enum BlockPower
{
    /// <summary>Puissance imposée (ERG) tout le bloc.</summary>
    Fixed,
    /// <summary>Pas de consigne : le cycliste roule comme il veut (trainer en simulation sur le plat).</summary>
    Free,
    /// <summary>La consigne passe progressivement de <see cref="WorkoutBlock.Power"/> à <see cref="WorkoutBlock.PowerEnd"/>.</summary>
    Ramp,
}

/// <summary>Unité des puissances d'un entraînement.</summary>
public enum PowerUnit
{
    Watts,
    /// <summary>Pourcentage de la FTP des Réglages, au moment de rouler.</summary>
    PercentFtp,
}

/// <summary>Un bloc d'entraînement : une durée, une puissance (fixe, libre ou en rampe), une cadence.</summary>
public sealed class WorkoutBlock
{
    public const int MinDurationS = 5, MaxDurationS = 4 * 3600;
    public const int MinCadenceRpm = 40, MaxCadenceRpm = 150;

    public int DurationS { get; set; } = 300;
    public BlockPower Kind { get; set; } = BlockPower.Fixed;
    /// <summary>Puissance du bloc (début de la rampe), en watts ou en % de FTP selon l'entraînement.</summary>
    public int Power { get; set; } = 150;
    /// <summary>Fin de la rampe (ignorée sinon).</summary>
    public int PowerEnd { get; set; } = 200;
    /// <summary>Cadence demandée ; 0 = libre.</summary>
    public int CadenceRpm { get; set; }

    [JsonIgnore] public bool FreeCadence => CadenceRpm <= 0;

    public WorkoutBlock Clone() => (WorkoutBlock)MemberwiseClone();
}

/// <summary>Un entraînement structuré : une suite de blocs.</summary>
public sealed class Workout
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public PowerUnit Unit { get; set; } = PowerUnit.Watts;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WorkoutBlock> Blocks { get; set; } = new();

    [JsonIgnore] public int TotalDurationS
    {
        get
        {
            int total = 0;
            foreach (var b in Blocks) total += b.DurationS;
            return total;
        }
    }

    /// <summary>Bornes des valeurs saisies, selon l'unité.</summary>
    public (int Min, int Max, int Step, int BigStep) PowerRange => Unit == PowerUnit.Watts ? (0, 2000, 5, 25) : (0, 300, 1, 5);

    /// <summary>Valeur saisie (watts ou %) convertie en watts avec la FTP donnée.</summary>
    public int ToWatts(int value, int ftpW) =>
        Unit == PowerUnit.Watts ? value : (int)Math.Round(value * ftpW / 100.0);

    /// <summary>Consigne en watts à <paramref name="offsetS"/> secondes du début du bloc ; null si la puissance est libre.</summary>
    public int? TargetW(WorkoutBlock block, double offsetS, int ftpW)
    {
        switch (block.Kind)
        {
            case BlockPower.Free:
                return null;
            case BlockPower.Ramp:
                double f = block.DurationS > 0 ? Math.Clamp(offsetS / block.DurationS, 0.0, 1.0) : 0.0;
                double start = ToWatts(block.Power, ftpW), end = ToWatts(block.PowerEnd, ftpW);
                return (int)Math.Round(start + (end - start) * f);
            default:
                return ToWatts(block.Power, ftpW);
        }
    }

    /// <summary>Bloc en cours à <paramref name="positionS"/> secondes du début : indice et temps écoulé dans le bloc.
    /// Indice = nombre de blocs une fois l'entraînement fini.</summary>
    public (int Index, double OffsetS) BlockAt(double positionS)
    {
        double start = 0;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (positionS < start + Blocks[i].DurationS) return (i, Math.Max(0, positionS - start));
            start += Blocks[i].DurationS;
        }
        return (Blocks.Count, 0);
    }

    /// <summary>Début du bloc <paramref name="index"/>, en secondes depuis le début de l'entraînement.</summary>
    public int StartOf(int index)
    {
        int start = 0;
        for (int i = 0; i < index && i < Blocks.Count; i++) start += Blocks[i].DurationS;
        return start;
    }

    /// <summary>TSS prévu avec la FTP donnée, en comptant les blocs libres à 50 % de FTP.</summary>
    public double EstimatedTss(int ftpW)
    {
        if (ftpW <= 0) return 0;
        double sum = 0;
        foreach (var b in Blocks)
            for (int t = 0; t < b.DurationS; t++)
            {
                double ratio = (TargetW(b, t + 0.5, ftpW) ?? ftpW * 0.5) / ftpW;
                sum += ratio * ratio;
            }
        return sum / 36.0;
    }

    /// <summary>Passe toutes les puissances dans l'autre unité, arrondies (5 W, ou 1 %).</summary>
    public void ConvertTo(PowerUnit unit, int ftpW)
    {
        if (unit == Unit || ftpW <= 0) return;
        int Convert(int v) => unit == PowerUnit.PercentFtp
            ? (int)Math.Round(v * 100.0 / ftpW)
            : (int)Math.Round(v * ftpW / 100.0 / 5.0) * 5;
        foreach (var b in Blocks)
        {
            b.Power = Convert(b.Power);
            b.PowerEnd = Convert(b.PowerEnd);
        }
        Unit = unit;
    }

    /// <summary>Remet les valeurs dans leurs bornes (fichier modifié à la main, saisie hors limites).</summary>
    public void Clamp()
    {
        var (min, max, _, _) = PowerRange;
        foreach (var b in Blocks)
        {
            b.DurationS = Math.Clamp(b.DurationS, WorkoutBlock.MinDurationS, WorkoutBlock.MaxDurationS);
            b.Power = Math.Clamp(b.Power, min, max);
            b.PowerEnd = Math.Clamp(b.PowerEnd, min, max);
            b.CadenceRpm = b.CadenceRpm <= 0 ? 0 : Math.Clamp(b.CadenceRpm, WorkoutBlock.MinCadenceRpm, WorkoutBlock.MaxCadenceRpm);
        }
    }

    /// <summary>« 220 W », « 90 % FTP », « 150 → 250 W », « libre ».</summary>
    public string DescribePower(WorkoutBlock block)
    {
        string unit = Unit == PowerUnit.Watts ? " W" : " % FTP";
        return block.Kind switch
        {
            BlockPower.Free => "libre",
            BlockPower.Ramp => $"{block.Power} → {block.PowerEnd}{unit}",
            _ => $"{block.Power}{unit}",
        };
    }

    public static string NewId(DateTimeOffset at) => "w" + at.ToUniversalTime().ToString("yyyyMMdd-HHmmssfff");

    /// <summary>Nouvel entraînement : échauffement, un bloc de travail, retour au calme.</summary>
    public static Workout CreateDefault(DateTimeOffset at, string name) => new()
    {
        Id = NewId(at),
        Name = name,
        Unit = PowerUnit.PercentFtp,
        UpdatedAt = at,
        Blocks =
        [
            new() { DurationS = 600, Kind = BlockPower.Ramp, Power = 50, PowerEnd = 75 },
            new() { DurationS = 600, Kind = BlockPower.Fixed, Power = 90, CadenceRpm = 90 },
            new() { DurationS = 300, Kind = BlockPower.Ramp, Power = 65, PowerEnd = 45 },
        ],
    };

    /// <summary>Exemples proposés au premier lancement.</summary>
    public static List<Workout> Examples(DateTimeOffset at)
    {
        var sweetSpot = new Workout
        {
            Id = NewId(at), Name = "Sweet spot 3 × 10 min", Unit = PowerUnit.PercentFtp, UpdatedAt = at,
            Blocks = [new() { DurationS = 600, Kind = BlockPower.Ramp, Power = 50, PowerEnd = 75 }],
        };
        for (int i = 0; i < 3; i++)
        {
            sweetSpot.Blocks.Add(new() { DurationS = 600, Power = 90, CadenceRpm = 90 });
            if (i < 2) sweetSpot.Blocks.Add(new() { DurationS = 300, Power = 55 });
        }
        sweetSpot.Blocks.Add(new() { DurationS = 300, Kind = BlockPower.Ramp, Power = 60, PowerEnd = 40 });

        var vo2 = new Workout
        {
            Id = NewId(at.AddMilliseconds(1)), Name = "PMA 5 × 3 min", Unit = PowerUnit.PercentFtp, UpdatedAt = at,
            Blocks = [new() { DurationS = 600, Kind = BlockPower.Ramp, Power = 50, PowerEnd = 80 }],
        };
        for (int i = 0; i < 5; i++)
        {
            vo2.Blocks.Add(new() { DurationS = 180, Power = 115, CadenceRpm = 100 });
            vo2.Blocks.Add(new() { DurationS = 180, Kind = BlockPower.Free });
        }
        vo2.Blocks.Add(new() { DurationS = 300, Power = 50 });
        return [sweetSpot, vo2];
    }
}

/// <summary>
/// Où en est l'entraînement : avance avec le temps de sortie (pauses exclues), peut sauter au
/// bloc suivant, donne la consigne de puissance et de cadence du moment.
/// </summary>
public sealed class WorkoutProgress
{
    public Workout Workout { get; }
    public int FtpW { get; }
    /// <summary>Temps écoulé dans l'entraînement (s), blocs passés compris.</summary>
    public double PositionS { get; private set; }

    public WorkoutProgress(Workout workout, int ftpW)
    {
        Workout = workout;
        FtpW = ftpW;
    }

    public int BlockIndex => Workout.BlockAt(PositionS).Index;
    public bool IsFinished => BlockIndex >= Workout.Blocks.Count;
    public WorkoutBlock? Current => IsFinished ? null : Workout.Blocks[BlockIndex];
    /// <summary>Temps restant dans le bloc en cours (s).</summary>
    public double BlockRemainingS => IsFinished ? 0 : Workout.StartOf(BlockIndex + 1) - PositionS;
    /// <summary>Consigne en watts ; null si la puissance est libre ou l'entraînement fini.</summary>
    public int? TargetW
    {
        get
        {
            var (i, offset) = Workout.BlockAt(PositionS);
            return i < Workout.Blocks.Count ? Workout.TargetW(Workout.Blocks[i], offset, FtpW) : null;
        }
    }
    /// <summary>Cadence demandée ; 0 si libre ou fini.</summary>
    public int TargetCadenceRpm => Current is { FreeCadence: false } b ? b.CadenceRpm : 0;

    public void Advance(double dt)
    {
        if (dt > 0) PositionS += dt;
    }

    /// <summary>Passe directement au début du bloc suivant.</summary>
    public void SkipBlock()
    {
        if (!IsFinished) PositionS = Workout.StartOf(BlockIndex + 1);
    }
}
