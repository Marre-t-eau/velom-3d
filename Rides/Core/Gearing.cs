using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Velom3d.Rides;

/// <summary>
/// Transmission virtuelle : plateaux, cassette et roue. Le cycliste change de vitesse dans
/// l'appli ; son vélo reste sur le même pignon réel. À une cadence donnée, la vitesse
/// virtuelle est cadence × plateau / pignon × circonférence de roue.
/// </summary>
public sealed class Drivetrain
{
    /// <summary>Plateaux, du plus grand au plus petit.</summary>
    public int[] Chainrings { get; }
    /// <summary>Pignons, du plus petit (vitesse la plus dure) au plus grand.</summary>
    public int[] Cogs { get; }
    public double WheelCircumferenceM { get; }

    public int FrontIndex { get; private set; }
    public int RearIndex { get; private set; }

    public Drivetrain(IEnumerable<int> chainrings, IEnumerable<int> cogs, double wheelCircumferenceM = 2.105)
    {
        Chainrings = chainrings.OrderByDescending(t => t).ToArray();
        Cogs = cogs.OrderBy(t => t).ToArray();
        if (Chainrings.Length == 0 || Cogs.Length == 0) throw new ArgumentException("Il faut au moins un plateau et un pignon.");
        WheelCircumferenceM = wheelCircumferenceM;
        // Départ sur le grand plateau, pignon du milieu : ni trop dur ni trop souple sur le plat.
        RearIndex = Cogs.Length / 2;
    }

    public int Chainring => Chainrings[FrontIndex];
    public int Cog => Cogs[RearIndex];
    public double Ratio => (double)Chainring / Cog;
    /// <summary>Distance parcourue par tour de pédale (m).</summary>
    public double Development => Ratio * WheelCircumferenceM;
    /// <summary>Numéro de la vitesse arrière, 1 = la plus souple (grand pignon).</summary>
    public int RearGearNumber => Cogs.Length - RearIndex;

    public double SpeedMpsAt(double cadenceRpm) => Math.Max(0.0, cadenceRpm) / 60.0 * Development;

    /// <summary>Vitesse plus dure (pignon plus petit). Faux en butée.</summary>
    public bool ShiftHarder()
    {
        if (RearIndex == 0) return false;
        RearIndex--;
        return true;
    }

    /// <summary>Vitesse plus souple (pignon plus grand). Faux en butée.</summary>
    public bool ShiftEasier()
    {
        if (RearIndex == Cogs.Length - 1) return false;
        RearIndex++;
        return true;
    }

    /// <summary>Passe au plateau suivant (grand → petit → grand).</summary>
    public void ShiftFront() => FrontIndex = (FrontIndex + 1) % Chainrings.Length;

    public string GearLabel => $"{Chainring} × {Cog}";

    public string Describe() => Gearing.Describe(Chainrings, Cogs);

    /// <summary>
    /// Puissance qu'il faut fournir pour tenir la vitesse que donne cette cadence dans cette
    /// vitesse : v·F(v), avec les mêmes pneus, air, poids et pente que la vitesse affichée.
    /// Le trainer reçoit cette valeur en consigne : la résistance suit la cadence et la vitesse
    /// engagée, comme sur la route, quel que soit le pignon réel du vélo.
    /// </summary>
    public double RequiredPowerW(BikePhysics physics, double cadenceRpm)
    {
        double v = SpeedMpsAt(cadenceRpm);
        return Math.Clamp(v * physics.ResistanceForce(v), 0.0, 2000.0);
    }
}

/// <summary>Préréglages de transmissions route et gravel, et saisie libre.</summary>
public static class Gearing
{
    public sealed record Preset(string Label, int[] Teeth);

    public static readonly Preset[] Chainrings =
    [
        new("53/39", [53, 39]),
        new("52/36", [52, 36]),
        new("50/34", [50, 34]),
        new("54/41", [54, 41]),
        new("50/37", [50, 37]),
        new("48/35", [48, 35]),
        new("46/33", [46, 33]),
        new("48/31", [48, 31]),
        new("1× 40", [40]),
        new("1× 42", [42]),
        new("1× 44", [44]),
    ];

    /// <summary>Cassettes par nombre de vitesses. Les 13 vitesses sont rares et leurs dentures
    /// varient : on les calcule en progression régulière (saisie libre pour une denture exacte).</summary>
    public static readonly (int Speeds, Preset[] Cassettes)[] Cassettes =
    [
        (11,
        [
            new("11-25", [11, 12, 13, 14, 15, 16, 17, 19, 21, 23, 25]),
            new("11-28", [11, 12, 13, 14, 15, 17, 19, 21, 23, 25, 28]),
            new("11-30", [11, 12, 13, 14, 15, 17, 19, 21, 24, 27, 30]),
            new("11-32", [11, 12, 13, 14, 16, 18, 20, 22, 25, 28, 32]),
            new("11-34", [11, 13, 15, 17, 19, 21, 23, 25, 27, 30, 34]),
            new("11-36", [11, 13, 15, 17, 19, 21, 24, 27, 30, 33, 36]),
        ]),
        (12,
        [
            new("10-28", [10, 11, 12, 13, 14, 15, 16, 17, 19, 21, 24, 28]),
            new("10-30", [10, 11, 12, 13, 14, 15, 17, 19, 21, 24, 27, 30]),
            new("10-33", [10, 11, 12, 13, 14, 15, 17, 19, 21, 24, 28, 33]),
            new("10-36", [10, 11, 12, 13, 15, 17, 19, 21, 24, 28, 32, 36]),
            new("11-30", [11, 12, 13, 14, 15, 16, 17, 19, 21, 24, 27, 30]),
            new("11-32", [11, 12, 13, 14, 15, 16, 17, 19, 22, 25, 28, 32]),
            new("11-34", [11, 12, 13, 14, 15, 17, 19, 21, 24, 27, 30, 34]),
            new("11-36", [11, 12, 13, 14, 15, 17, 19, 21, 24, 28, 32, 36]),
        ]),
        (13,
        [
            new("9-36", Progressive(9, 36, 13)),
            new("10-36", Progressive(10, 36, 13)),
            new("9-42", Progressive(9, 42, 13)),
            new("10-44", Progressive(10, 44, 13)),
        ]),
    ];

    public static readonly int[] DefaultChainrings = [50, 34];
    public static readonly int[] DefaultCogs = [11, 12, 13, 14, 15, 17, 19, 21, 24, 27, 30];

    /// <summary>Tailles de pneus route et circonférence de roue (mm).</summary>
    public static readonly (string Label, int CircumferenceMm)[] Wheels =
    [
        ("700×23", 2096), ("700×25", 2105), ("700×28", 2136), ("700×32", 2155), ("700×40", 2200),
    ];

    /// <summary>Denture régulière (progression géométrique), strictement croissante.</summary>
    public static int[] Progressive(int smallest, int largest, int count)
    {
        var teeth = new int[count];
        for (int i = 0; i < count; i++)
        {
            int t = (int)Math.Round(smallest * Math.Pow((double)largest / smallest, i / (double)(count - 1)));
            teeth[i] = i == 0 ? t : Math.Max(t, teeth[i - 1] + 1);
        }
        teeth[^1] = largest;
        return teeth;
    }

    /// <summary>« 52/36 × 11-30 (12 v.) ».</summary>
    public static string Describe(int[] chainrings, int[] cogs)
    {
        var rings = string.Join("/", chainrings.OrderByDescending(t => t));
        if (chainrings.Length == 1) rings = "1× " + rings;
        return cogs.Length == 0 ? rings : $"{rings} × {cogs.Min()}-{cogs.Max()} ({cogs.Length} v.)";
    }

    static readonly char[] Separators = [',', ';', '/', ' ', '-', '×', 'x'];

    /// <summary>Lit des dentures saisies à la main : « 50/34 », « 50 34 », « 11,12,13… ».
    /// Null si la saisie n'est pas valable (dents de 8 à 60, sans doublon).</summary>
    public static int[]? ParseTeeth(string text, int minCount, int maxCount)
    {
        var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var teeth = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) || t < 8 || t > 60) return null;
            teeth.Add(t);
        }
        if (teeth.Count < minCount || teeth.Count > maxCount || teeth.Distinct().Count() != teeth.Count) return null;
        return teeth.ToArray();
    }

    public static int[]? ParseChainrings(string text) => ParseTeeth(text, 1, 3);
    public static int[]? ParseCassette(string text) => ParseTeeth(text, 5, 14);
}
