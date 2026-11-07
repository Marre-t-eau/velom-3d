using System;
using System.Collections.Generic;

namespace Velom3d.Rides;

/// <summary>Une semaine d'un plan : un titre, un but, et ses séances dans l'ordre conseillé.</summary>
public sealed class PlanWeek
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    /// <summary>Semaine allégée : le corps assimile le travail des semaines précédentes.</summary>
    public bool Recovery { get; init; }
    public List<Workout> Sessions { get; init; } = new();
}

/// <summary>Où en est une séance du plan, d'après les sorties enregistrées.</summary>
public enum SessionState { Todo, Next, Done }

/// <summary>
/// Plan d'entraînement intégré : plusieurs semaines de séances en % de FTP, toujours proposé et
/// non modifiable (comme les tests FTP). Une séance est faite quand une sortie l'a suivie, depuis
/// le début du plan (<see cref="RiderProfile.PlanStartedAt"/>), sur au moins 80 % de sa durée.
/// </summary>
public sealed class TrainingPlan
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<PlanWeek> Weeks { get; init; } = new();

    /// <summary>Part de la durée prévue à rouler pour qu'une séance compte comme faite.</summary>
    public const double DoneRatio = 0.8;

    public IEnumerable<Workout> Sessions
    {
        get
        {
            foreach (var week in Weeks)
                foreach (var s in week.Sessions) yield return s;
        }
    }

    public int SessionCount
    {
        get
        {
            int n = 0;
            foreach (var w in Weeks) n += w.Sessions.Count;
            return n;
        }
    }

    public bool Contains(string workoutId) => Find(workoutId) is not null;

    /// <summary>Séance d'un plan intégré (pour revenir à l'écran du plan après la sortie).</summary>
    public static bool IsPlanSession(string workoutId) => workoutId.StartsWith("plan-", StringComparison.Ordinal);

    public Workout? Find(string workoutId)
    {
        foreach (var s in Sessions) if (s.Id == workoutId) return s;
        return null;
    }

    /// <summary>Séances faites depuis <paramref name="startedAt"/> (null : plan pas commencé, rien n'est fait).</summary>
    public HashSet<string> DoneSessions(IEnumerable<RideInfo> rides, DateTimeOffset? startedAt)
    {
        var done = new HashSet<string>();
        if (startedAt is not { } start) return done;
        foreach (var r in rides)
        {
            if (r.StartedAt < start || r.WorkoutId.Length == 0) continue;
            if (Find(r.WorkoutId) is { } s && r.Summary.DurationS >= s.TotalDurationS * DoneRatio) done.Add(s.Id);
        }
        return done;
    }

    /// <summary>Première séance pas encore faite, dans l'ordre du plan ; null si tout est fait.</summary>
    public Workout? NextSession(HashSet<string> done)
    {
        foreach (var s in Sessions) if (!done.Contains(s.Id)) return s;
        return null;
    }

    public SessionState StateOf(Workout session, HashSet<string> done, Workout? next) =>
        done.Contains(session.Id) ? SessionState.Done : next?.Id == session.Id ? SessionState.Next : SessionState.Todo;

    // ------------------------------------------------------------------ Plan endurance

    public const string EnduranceId = "plan-endurance";

    /// <summary>
    /// Plan endurance de 8 semaines, 3 séances par semaine, presque tout en zone 2 (56-75 % de FTP) :
    /// une séance d'endurance, une séance avec des blocs tempo / sweet spot (endurance musculaire),
    /// une sortie longue qui s'allonge de 1 h 15 à 2 h. Semaines 4 et 8 allégées.
    /// </summary>
    public static TrainingPlan Endurance()
    {
        var weeks = new List<PlanWeek>();
        void Week(int n, string title, string goal, bool recovery, params Workout[] sessions)
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Id = $"{EnduranceId}-s{n}-{i + 1}";
                sessions[i].Name = $"S{n}.{i + 1} · {sessions[i].Name}";
                sessions[i].UpdatedAt = Workout.ExamplesDate;
            }
            weeks.Add(new PlanWeek { Number = n, Title = title, Goal = goal, Recovery = recovery, Sessions = [.. sessions] });
        }

        Week(1, "Semaine 1 · Mise en route", "Prendre l'habitude de rouler en zone 2, sans forcer.", false,
            Steady("Endurance 45 min", 45, 65),
            Tempo("Tempo 3 × 8 min", 3, 8, 78, 4),
            Long("Sortie longue 1 h 15", 75, 65));
        Week(2, "Semaine 2 · Allonger", "Un peu plus long à la même intensité.", false,
            Steady("Endurance 50 min", 50, 67),
            Tempo("Tempo 3 × 10 min", 3, 10, 80, 4),
            Long("Sortie longue 1 h 30", 90, 65));
        Week(3, "Semaine 3 · Construire", "La sortie longue passe 1 h 45 : restez bien en zone 2.", false,
            Steady("Endurance 1 h", 60, 68),
            Tempo("Tempo 3 × 12 min", 3, 12, 82, 5),
            Long("Sortie longue 1 h 45", 105, 67));
        Week(4, "Semaine 4 · Récupération", "Semaine allégée : le corps assimile le travail.", true,
            Steady("Endurance douce 40 min", 40, 60),
            Cadence("Vélocité 45 min", 45, 62, 6),
            Steady("Endurance 1 h", 60, 63));
        Week(5, "Semaine 5 · Endurance musculaire", "Blocs sweet spot plus longs, sortie longue de 2 h.", false,
            Steady("Endurance 1 h", 60, 70),
            Tempo("Sweet spot 2 × 20 min", 2, 20, 85, 8),
            Long("Sortie longue 2 h", 120, 67));
        Week(6, "Semaine 6 · Tenir la durée", "Finir la sortie longue un cran au-dessus.", false,
            Steady("Endurance 1 h 10", 70, 70),
            Tempo("Sweet spot 3 × 15 min", 3, 15, 86, 6),
            Long("Sortie longue 2 h, fin tempo", 120, 68, finishMin: 20, finishPct: 76));
        Week(7, "Semaine 7 · Pic de charge", "La semaine la plus chargée du plan.", false,
            Steady("Endurance 1 h 15", 75, 70),
            Tempo("Sweet spot 2 × 25 min", 2, 25, 87, 8),
            Long("Sortie longue 2 h + tempo", 120, 69, tempoBlocks: 2, tempoPct: 80));
        Week(8, "Semaine 8 · Assimilation", "Semaine légère, puis refaites un test FTP pour mesurer les progrès.", true,
            Steady("Endurance douce 45 min", 45, 62),
            Tempo("Rappel 3 × 5 min", 3, 5, 88, 5),
            Steady("Endurance 1 h 15", 75, 65));

        return new TrainingPlan
        {
            Id = EnduranceId,
            Name = "Plan endurance · 8 semaines",
            Description = "3 séances par semaine, surtout en zone 2 : une endurance, une avec des blocs tempo, "
                          + "une sortie longue de 1 h 15 à 2 h. Semaines 4 et 8 allégées. Puissances en % de FTP.",
            Weeks = weeks,
        };
    }

    // Échauffement 10 min et retour au calme 5 min autour de chaque séance.
    const int WarmUpS = 600, CoolDownS = 300;

    static Workout Base(string name, int warmTo) => new()
    {
        Name = name, Unit = PowerUnit.PercentFtp,
        Blocks = [new() { DurationS = WarmUpS, Kind = BlockPower.Ramp, Power = 50, PowerEnd = warmTo }],
    };

    static Workout End(Workout w, int from)
    {
        w.Blocks.Add(new() { DurationS = CoolDownS, Kind = BlockPower.Ramp, Power = Math.Min(from, 60), PowerEnd = 45 });
        return w;
    }

    /// <summary>Zone 2 en blocs de 15 min au plus ; un bloc sur deux à 95 tr/min pour varier le pédalage.</summary>
    static void Zone2(Workout w, int seconds, int pct)
    {
        bool fast = false;
        while (seconds > 0)
        {
            int d = Math.Min(seconds, 900);
            // Pas de bloc ridicule à la fin : on allonge le précédent.
            if (seconds - d is > 0 and < 300) d = seconds;
            w.Blocks.Add(new() { DurationS = d, Power = pct, CadenceRpm = fast ? 95 : 0 });
            fast = !fast;
            seconds -= d;
        }
    }

    /// <summary>Endurance continue à <paramref name="pct"/> % de FTP.</summary>
    static Workout Steady(string name, int minutes, int pct)
    {
        var w = Base(name, pct);
        Zone2(w, minutes * 60 - WarmUpS - CoolDownS, pct);
        return End(w, pct);
    }

    /// <summary>Blocs tempo / sweet spot à 90 tr/min séparés de récupération, le reste en zone 2.</summary>
    static Workout Tempo(string name, int reps, int minutes, int pct, int restMin)
    {
        var w = Base(name, 70);
        w.Blocks.Add(new() { DurationS = 300, Power = 65 });
        for (int i = 0; i < reps; i++)
        {
            w.Blocks.Add(new() { DurationS = minutes * 60, Power = pct, CadenceRpm = 90 });
            w.Blocks.Add(new() { DurationS = restMin * 60, Power = 58 });
        }
        w.Blocks.Add(new() { DurationS = 600, Power = 65 });
        return End(w, 65);
    }

    /// <summary>Sortie longue en zone 2, avec éventuellement des blocs tempo au milieu ou une fin plus soutenue.</summary>
    static Workout Long(string name, int minutes, int pct, int finishMin = 0, int finishPct = 0, int tempoBlocks = 0, int tempoPct = 0)
    {
        var w = Base(name, pct);
        int body = minutes * 60 - WarmUpS - CoolDownS - finishMin * 60;
        if (tempoBlocks > 0)
        {
            // Blocs tempo de 15 min répartis dans la sortie, séparés par de la zone 2.
            int z2 = (body - tempoBlocks * 900) / (tempoBlocks + 1);
            for (int i = 0; i < tempoBlocks; i++)
            {
                Zone2(w, z2, pct);
                w.Blocks.Add(new() { DurationS = 900, Power = tempoPct, CadenceRpm = 90 });
            }
            Zone2(w, body - tempoBlocks * 900 - tempoBlocks * z2, pct);
        }
        else Zone2(w, body, pct);
        if (finishMin > 0) w.Blocks.Add(new() { DurationS = finishMin * 60, Power = finishPct, CadenceRpm = 90 });
        return End(w, finishMin > 0 ? finishPct : pct);
    }

    /// <summary>Endurance facile avec des accélérations de cadence de 1 min à 110 tr/min.</summary>
    static Workout Cadence(string name, int minutes, int pct, int spins)
    {
        var w = Base(name, pct);
        int body = minutes * 60 - WarmUpS - CoolDownS;
        int gap = (body - spins * 60) / (spins + 1);
        for (int i = 0; i < spins; i++)
        {
            w.Blocks.Add(new() { DurationS = gap, Power = pct });
            w.Blocks.Add(new() { DurationS = 60, Power = pct, CadenceRpm = 110 });
        }
        w.Blocks.Add(new() { DurationS = body - spins * (gap + 60), Power = pct });
        return End(w, pct);
    }
}
