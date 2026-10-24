using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Velom3d.Sensors;

/// <summary>
/// Fiche d'un appareil, lue une fois à la connexion : fabricant, modèle, versions, plages
/// acceptées (puissance, résistance, pente) et fonctions annoncées. Un champ que l'appareil
/// n'expose pas reste vide (null ou liste vide).
/// </summary>
public sealed class DeviceSpecs
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? Hardware { get; set; }
    public string? Firmware { get; set; }
    public string? Software { get; set; }
    public int? PowerMinW { get; set; }
    public int? PowerMaxW { get; set; }
    public int? PowerStepW { get; set; }
    public double? ResistanceMin { get; set; }
    public double? ResistanceMax { get; set; }
    public double? ResistanceStep { get; set; }
    public double? InclineMinPct { get; set; }
    public double? InclineMaxPct { get; set; }
    public double? InclineStepPct { get; set; }
    /// <summary>Emplacement du capteur (« pédale gauche », « poitrine »…).</summary>
    public string? Location { get; set; }
    /// <summary>Fonctions annoncées (FTMS, puissance), en clair.</summary>
    public List<string> Features { get; set; } = new();

    static readonly string[] FtmsMachineFeatures =
    [
        "vitesse moyenne", "cadence", "distance totale", "inclinaison", "dénivelé", "allure",
        "nombre de pas", "niveau de résistance", "nombre de foulées", "énergie dépensée",
        "fréquence cardiaque", "MET", "temps écoulé", "temps restant", "puissance",
        "force sur la courroie et puissance", "mémoire des données utilisateur",
    ];

    static readonly string[] FtmsTargetFeatures =
    [
        "consigne de vitesse", "consigne d'inclinaison", "consigne de résistance", "consigne de puissance (ERG)",
        "consigne de fréquence cardiaque", "consigne d'énergie", "consigne de pas", "consigne de foulées",
        "consigne de distance", "consigne de durée", "durée sur 2 zones cardio", "durée sur 3 zones cardio",
        "durée sur 5 zones cardio", "simulation (pente, vent, pneus)", "circonférence de roue",
        "spin down", "consigne de cadence",
    ];

    static readonly string[] CyclingPowerFeatures =
    [
        "équilibre gauche/droite", "couple accumulé", "tours de roue", "tours de pédalier",
        "valeurs extrêmes (force ou couple)", "angles extrêmes", "points morts haut et bas", "énergie accumulée",
        "indicateur de compensation", "compensation du zéro", "masquage du contenu", "plusieurs emplacements",
        "réglage de la longueur de manivelle", "réglage de la longueur de chaîne", "réglage du poids de chaîne",
        "réglage de l'écart", "mesure par force ou par couple", "sens de la mesure instantanée",
        "date de calibration usine", "compensation du zéro améliorée",
    ];

    static readonly string[] SensorLocations =
    [
        "autre", "dessus de la chaussure", "dans la chaussure", "hanche", "roue avant", "manivelle gauche",
        "manivelle droite", "pédale gauche", "pédale droite", "moyeu avant", "patte arrière", "base arrière",
        "roue arrière", "moyeu arrière", "poitrine", "araignée de pédalier", "plateau",
    ];

    static readonly string[] BodyLocations = ["autre", "poitrine", "poignet", "doigt", "main", "lobe de l'oreille", "pied"];

    /// <summary>Range une caractéristique lue dans la fiche. Les inconnues sont ignorées.</summary>
    public void Apply(string charUuid, ReadOnlySpan<byte> d)
    {
        switch (BleUuids.ToShort(charUuid))
        {
            case BleUuids.ManufacturerName: Manufacturer = Text(d); break;
            case BleUuids.ModelNumber: Model = Text(d); break;
            case BleUuids.SerialNumber: Serial = Text(d); break;
            case BleUuids.HardwareRevision: Hardware = Text(d); break;
            case BleUuids.FirmwareRevision: Firmware = Text(d); break;
            case BleUuids.SoftwareRevision: Software = Text(d); break;
            case BleUuids.SupportedPowerRange when d.Length >= 6:
                PowerMinW = BinaryPrimitives.ReadInt16LittleEndian(d);
                PowerMaxW = BinaryPrimitives.ReadInt16LittleEndian(d[2..]);
                PowerStepW = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
                break;
            case BleUuids.SupportedResistanceLevelRange when d.Length >= 6:
                ResistanceMin = BinaryPrimitives.ReadInt16LittleEndian(d) * 0.1;
                ResistanceMax = BinaryPrimitives.ReadInt16LittleEndian(d[2..]) * 0.1;
                ResistanceStep = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) * 0.1;
                break;
            case BleUuids.SupportedInclinationRange when d.Length >= 6:
                InclineMinPct = BinaryPrimitives.ReadInt16LittleEndian(d) * 0.1;
                InclineMaxPct = BinaryPrimitives.ReadInt16LittleEndian(d[2..]) * 0.1;
                InclineStepPct = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) * 0.1;
                break;
            case BleUuids.FitnessMachineFeature when d.Length >= 8:
                AddBits(BinaryPrimitives.ReadUInt32LittleEndian(d), FtmsMachineFeatures);
                AddBits(BinaryPrimitives.ReadUInt32LittleEndian(d[4..]), FtmsTargetFeatures);
                break;
            case BleUuids.CyclingPowerFeature when d.Length >= 4:
                AddBits(BinaryPrimitives.ReadUInt32LittleEndian(d), CyclingPowerFeatures);
                break;
            case BleUuids.SensorLocation when d.Length >= 1:
                Location = d[0] < SensorLocations.Length ? SensorLocations[d[0]] : $"code {d[0]}";
                break;
            case BleUuids.BodySensorLocation when d.Length >= 1:
                Location = d[0] < BodyLocations.Length ? BodyLocations[d[0]] : $"code {d[0]}";
                break;
        }
    }

    void AddBits(uint bits, string[] names)
    {
        for (int i = 0; i < names.Length; i++)
            if ((bits & (1u << i)) != 0 && !Features.Contains(names[i])) Features.Add(names[i]);
    }

    static string Text(ReadOnlySpan<byte> d) => Encoding.UTF8.GetString(d).TrimEnd('\0', ' ');
}
