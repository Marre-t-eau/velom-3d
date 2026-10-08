namespace Velom3d.Sensors;

/// <summary>
/// Identifiants Bluetooth SIG utilisés par Velom3d, en forme courte 16 bits minuscule.
/// Les backends normalisent toujours les UUID vers cette forme avant d'appeler le hub.
/// </summary>
public static class BleUuids
{
    // Services
    public const string FitnessMachine = "1826";      // FTMS : home trainers connectés
    public const string CyclingPower = "1818";        // capteurs de puissance
    public const string HeartRate = "180d";           // ceintures cardio
    public const string CyclingSpeedCadence = "1816"; // vitesse/cadence (pas encore décodé)

    // Caractéristiques
    public const string IndoorBikeData = "2ad2";              // notify : vitesse, cadence, puissance, FC
    public const string FitnessMachineFeature = "2acc";       // read
    public const string FitnessMachineControlPoint = "2ad9";  // write + indicate : ERG, pente
    public const string FitnessMachineStatus = "2ada";        // notify
    public const string CyclingPowerMeasurement = "2a63";     // notify
    public const string HeartRateMeasurement = "2a37";        // notify

    const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";

    /// <summary>"00002ad2-0000-1000-8000-00805f9b34fb" -> "2ad2". Laisse les UUID propriétaires intacts.</summary>
    public static string ToShort(string uuid)
    {
        var u = uuid.ToLowerInvariant();
        if (u.Length == 36 && u.StartsWith("0000") && u.EndsWith(BaseSuffix))
            return u.Substring(4, 4);
        return u;
    }

    public static string ToLong(string shortUuid) => "0000" + shortUuid.ToLowerInvariant() + BaseSuffix;
}
