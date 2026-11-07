# Format des données enregistrées par Velom3d

Tout est stocké sur le téléphone, dans le dossier privé de l'appli (`user://` pour Godot). Rien n'est envoyé en ligne, sauf si le joueur active la synchronisation avec un serveur (section 5) : les fichiers du téléphone restent alors la référence. Les fichiers sont en texte : JSON pour les en-têtes et les réglages, CSV pour les données. Les CSV utilisent la virgule comme séparateur et le point comme décimale.

```
user://
├── rides/
│   ├── profile.json              réglages du cycliste
│   ├── 20261013-160000.json      en-tête d'une sortie : réglages, capteurs, bilan
│   └── 20261013-160000.csv       données de la sortie : une ligne par valeur reçue
└── workouts/
    ├── exemple-sweet-spot.json   un entraînement structuré par fichier
    └── w20261012-091530123.json
sync.json                         synchronisation facultative (section 5)
```

L'identifiant d'une sortie est l'heure de départ en UTC, au format `aaaaMMjj-HHmmss`. Pendant la sortie, les nouvelles lignes sont ajoutées au CSV toutes les 30 s et à chaque passage en arrière-plan, et l'en-tête est réécrit (`.tmp` puis remplacement, jamais tronqué).

## 1. `{id}.csv` : le fichier de données

C'est la seule source de données de la sortie, sans aucun traitement. Chaque valeur reçue d'un capteur est écrite telle que le capteur l'envoie, sans moyenne ni rééchantillonnage. Le fichier note aussi tout ce que fait l'appli : départ, consignes, changements de vitesse, pauses, reprises, fin et commandes envoyées au trainer.

| Colonne | Contenu |
|---|---|
| `t` | secondes depuis le départ (`StartedAt` de l'en-tête), heure réelle, au millième, pauses comprises |
| `type` | ce que représente la valeur (tableaux ci-dessous) |
| `sensor` | numéro du capteur dans l'en-tête (`Sensors[].Index` : 1, 2, 3…), 0 = l'appli |
| `value` | la valeur, en nombre (point décimal) ou en texte |

```
t,type,sensor,value
0,start,0,TargetPower
0,grade,0,0
0,target_power,0,220
0.021,command,1,request_control
0.022,set_power,1,220
0.087,response,1,set_power ok
0.25,speed,1,31.2
0.25,cadence,1,88
0.25,power,1,221
0.25,hr,1,141
0.251,power,2,224
0.251,crank_revs,2,1532
0.251,crank_time,2,40211
0.4,hr,3,142
0.4,rr,3,0.4229
1800.003,pause,0,
1932.512,resume,0,
3720.1,finish,0,
```

### Valeurs des capteurs (sensor ≥ 1)

| type | Unité | Source |
|---|---|---|
| `power` | W | home trainer (FTMS) ou capteur de puissance |
| `cadence` | tr/min | home trainer (FTMS) |
| `speed` | km/h | vitesse mesurée par le home trainer |
| `resistance` | sans unité | niveau de résistance du trainer (échelle propre au trainer, voir sa fiche) |
| `distance` | m | distance comptée par le trainer |
| `energy` | kcal | énergie comptée par le trainer |
| `elapsed` | s | temps compté par le trainer |
| `avg_speed`, `avg_cadence`, `avg_power` | | moyennes calculées par le trainer, s'il les envoie |
| `hr` | bpm | ceinture cardio, ou FC relayée par le trainer |
| `rr` | s | intervalle entre deux battements (ceinture cardio), une ligne par intervalle |
| `balance` | % | équilibre gauche/droite (capteur de puissance) |
| `crank_revs`, `crank_time` | tours, 1/1024 s | compteurs du pédalier (capteur de puissance), qui donnent la cadence |
| `wheel_revs`, `wheel_time` | tours, 1/2048 s | compteurs de roue (capteur de puissance) |
| `response` | texte | réponse du trainer à une commande : `set_power ok`, `set_simulation refus 3`… |
| `status`, `status_power`, `status_resistance`, `status_grade`… | | changement d'état annoncé par le trainer |
| `raw_{uuid}` | hexadécimal | trame que l'appli ne sait pas encore lire, gardée telle quelle |

### Commandes envoyées au trainer (sensor = n° du trainer)

| type | Valeur |
|---|---|
| `set_power` | consigne ERG en W |
| `set_resistance` | niveau de résistance |
| `set_grade`, `set_wind`, `set_crr`, `set_cw` | paramètres de simulation : pente (%), vent (m/s), pneus (Crr), traînée (kg/m) |
| `command` | autre commande : `request_control`, `start`, `reset`… |

### Événements de l'appli (sensor = 0)

| type | Valeur |
|---|---|
| `start` | mode : `TargetPower`, `Simulation` ou `Workout` |
| `grade` | pente simulée (%) |
| `target_power` | consigne affichée au cycliste (W), 0 = libre |
| `target_cadence` | cadence demandée (tr/min), 0 = libre |
| `gear` | braquet virtuel, par exemple `52 × 17` |
| `pause`, `resume`, `finish` | |

### Ce qui est reconstruit

Les graphiques et le bilan utilisent une mesure par seconde. Elle n'est pas enregistrée : l'appli la recalcule à chaque ouverture en rejouant le fichier dans l'ordre, avec la même physique qu'en direct (masse, Crr et CdA de l'en-tête) et la même règle de choix entre capteurs. Le capteur dédié l'emporte sur le trainer tant qu'il émet, puis le trainer reprend après 3 s de silence. Les valeurs reconstruites sont :
- la puissance, la cadence et la FC retenues ;
- la vitesse et la distance virtuelles ;
- les pauses ;
- le bilan (TSS, IF, NP, zones).

## 2. `{id}.json` : en-tête de la sortie

| Champ | Contenu |
|---|---|
| `Id` | identifiant de la sortie, qui sert aussi de nom de fichier |
| `StartedAt` | heure de départ, l'origine de la colonne `t` |
| `EndedAt` | heure de l'appui sur Terminer |
| `Mode` | `TargetPower` (ERG), `Simulation` ou `Workout` |
| `TargetPowerW` | consigne ERG de départ |
| `FtpW` | FTP au moment de la sortie |
| `MassKg`, `RiderMassKg`, `BikeMassKg` | masse totale, poids du cycliste, poids du vélo |
| `Crr`, `CdA`, `WheelCircumferenceMm` | résistance des pneus, aéro (m²), roue |
| `VirtualGears` | transmission virtuelle, par exemple `52/36 × 11-30 (12 v.)`, vide sinon |
| `WorkoutName` | entraînement suivi, vide sinon |
| `FtpTestW` | FTP mesurée si la sortie était un test FTP, 0 sinon |
| `Sensors` | capteurs utilisés, voir ci-dessous |
| `Summary` | bilan, pour la liste de l'historique (recalculé à l'ouverture) |

Chaque élément de `Sensors` contient :
- `Index`, le numéro repris dans la colonne `sensor` ;
- `Name` et `Address` (adresse Bluetooth) ;
- `Kind` : `Trainer`, `PowerMeter`, `HeartRate` ou `SpeedCadence` ;
- `Services`, les services Bluetooth annoncés (`1826` FTMS, `1818` puissance, `180d` cardio…) ;
- `Specs`, la fiche lue sur l'appareil à la connexion. Un champ reste vide (null) si l'appareil ne l'expose pas.

La fiche `Specs` contient :

| Champ | Contenu |
|---|---|
| `Manufacturer`, `Model`, `Serial`, `Hardware`, `Firmware`, `Software` | informations de l'appareil |
| `PowerMinW`, `PowerMaxW`, `PowerStepW` | plage de consigne de puissance acceptée par le trainer |
| `ResistanceMin`, `ResistanceMax`, `ResistanceStep` | plage de résistance |
| `InclineMinPct`, `InclineMaxPct`, `InclineStepPct` | plage de pente simulée |
| `Location` | emplacement : pédale gauche, poitrine… |
| `Features` | fonctions annoncées, en clair : `puissance`, `consigne de puissance (ERG)`, `simulation (pente, vent, pneus)`, `équilibre gauche/droite`… |

```json
{
  "Id": "20261013-160000",
  "StartedAt": "2026-10-13T18:00:00+02:00",
  "EndedAt": "2026-10-13T19:02:00+02:00",
  "Mode": "TargetPower",
  "FtpW": 250, "MassKg": 83, "RiderMassKg": 75, "BikeMassKg": 8,
  "Crr": 0.004, "CdA": 0.4, "WheelCircumferenceMm": 2105,
  "Sensors": [
    {
      "Index": 1, "Name": "KICKR CORE 1A2B", "Address": "AA:BB:CC:DD:EE:FF", "Kind": "Trainer",
      "Services": ["1826", "1818", "180a"],
      "Specs": {
        "Manufacturer": "Wahoo Fitness", "Model": "KICKR CORE", "Firmware": "4.2.1",
        "PowerMinW": 0, "PowerMaxW": 1800, "PowerStepW": 1,
        "ResistanceMin": 0, "ResistanceMax": 100, "ResistanceStep": 1,
        "InclineMinPct": -10, "InclineMaxPct": 16, "InclineStepPct": 0.1,
        "Features": ["cadence", "puissance", "consigne de puissance (ERG)", "simulation (pente, vent, pneus)"]
      }
    },
    { "Index": 3, "Name": "HRM-Pro", "Kind": "HeartRate", "Specs": { "Manufacturer": "Garmin", "Location": "poitrine" } }
  ],
  "Summary": { "DurationS": 3600, "AvgPowerW": 182.4, "Tss": 60.9, "...": "..." }
}
```

## 3. `profile.json` : réglages

Le fichier contient :
- `FtpW` et `FtpTestedAt` ;
- `RiderMassKg`, `BikeMassKg` ;
- `VirtualGears`, `Chainrings`, `Cogs`, `WheelCircumferenceMm` ;
- `LastMode`, `LastTargetPowerW` ;
- `UpdatedAt`, l'heure du dernier enregistrement (départage deux appareils lors d'une première synchronisation).

## 4. Entraînements structurés : `workouts/{id}.json`

Un fichier par entraînement contient `Id`, `Name`, `Unit` (`Watts` ou `PercentFtp`), `UpdatedAt` et `Blocks`. Chaque bloc a les champs suivants :
- `DurationS`, de 5 s à 4 h ;
- `Kind` : `Fixed` (ERG constant), `Ramp` (de `Power` à `PowerEnd`) ou `Free` ;
- `Power`, `PowerEnd` ;
- `CadenceRpm` (0 = libre) ;
- `FtpTest` : bloc d'effort du test FTP, dont la moyenne × 0,95 donne la nouvelle FTP.

Une sortie faite en suivant un entraînement porte son nom dans `WorkoutName`. Les consignes réellement données sont dans le fichier de données (`target_power`, `target_cadence`, `set_power`).

## Anciennes sorties

Les sorties enregistrées avant ce format ont un CSV avec une ligne par seconde (colonnes `time_s,power_w,cadence_rpm,…`). Elles restent lisibles, mais n'ont ni fiche capteur, ni valeurs brutes, ni pauses.

## 5. Synchronisation facultative : `sync.json`

Désactivée par défaut (*Réglages > Synchronisation*). Une fois activée et connectée à un compte,
l'appli envoie au serveur une copie de `profile.json`, des entraînements et des sorties (en-tête et
CSV compressé), et reçoit celles des autres appareils du compte. Les fichiers sont envoyés tels
quels. Le serveur se trouve dans le dossier `Server/` du dépôt, auto-hébergeable ; ses règles et son
API sont décrites dans `Server/README.md`.

`user://sync.json` n'est jamais envoyé. Il contient :
- `Enabled`, `ServerUrl`, `Username`, `Token` (jeton de connexion, vide si déconnecté) ;
- `Account` (adresse + nom) et `Cursor` (dernier changement du serveur reçu) ;
- `Known` : pour chaque fichier (`profile/profile`, `workout/{id}`, `ride/{id}`), son empreinte
  SHA-256 à la dernière synchronisation (plus la taille du CSV pour une sortie). Un fichier dont
  l'empreinte a changé a été modifié sur l'appareil, et sa version l'emporte sur celle du serveur ;
- `Pending` : sorties reçues dont le CSV n'était pas encore sur le serveur, réessayées ensuite ;
- `LastSyncAt`, `LastError`.

Les deux exemples d'entraînement ont des ids fixes (`exemple-sweet-spot`, `exemple-pma`) et la date
du 1er janvier 2026, pour ne pas être en double sur les appareils d'un même compte.
