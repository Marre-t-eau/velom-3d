# Velom3d

Simulateur de vélo d'intérieur open source, en Godot 4.7 .NET (C#), rendu Mobile.

## Connexion des capteurs

Le premier écran (`Screens/DeviceConnection/`) recherche les appareils Bluetooth Low Energy
et s'y connecte : home trainer (FTMS), capteur de puissance, ceinture cardio.

```
 capteurs BLE ──► backend ──(octets bruts)──► SensorHub (autoload "Sensors") ──► écrans du jeu
                  ├─ NativeBleBackend   (plugin Android « Velom3dBle »)
                  └─ SimulatedBackend   (faux appareils : mode debug et tests automatiques)
```

- `Sensors/Core/` : C# pur, sans Godot (décodage BLE, commandes FTMS, fusion des mesures,
  liste des appareils, faux trainer). Testé par `dotnet test`.
- `Sensors/SensorHub.cs` : autoload `Sensors`. Mesures (`Power`, `Cadence`, `HeartRate`,
  `Speed`), pilotage du trainer (`SetGrade()`, `SetTargetPower()`), liste `Devices`.
- `Sensors/Backends/NativeBleBackend.cs` : parle au singleton natif `Velom3dBle`.
- `android-plugin/` : sources Kotlin du plugin Android (scan, connexion, notifications, écriture).
- `addons/velom3d_ble/` : greffon d'éditeur qui ajoute le plugin aux exports Android.

Le transport est une classe `SensorBackend` à deux implémentations, choisie par `SensorBackends` :

- `NativeBleBackend` : le vrai Bluetooth (Android). Utilisé par défaut.
- `SimulatedBackend` : **mode debug**. Un faux home trainer, des pédales et une ceinture cardio
  génèrent puissance, cadence et fréquence cardiaque (effort variable) et obéissent aux commandes
  ERG et simulation. À activer sur ordinateur avec la variable d'environnement
  `VELOM3D_DEBUG_SENSORS=1`, ou l'argument `--debug-sensors`
  (`godot --path . -- --debug-sensors`, ou *Projet > Paramètres > Éditeur > Run > Main Run Args*).

Sans mode debug et sans plugin natif (ordinateur, éditeur), l'écran indique que le Bluetooth est
indisponible.

## Écrans

```
Connexion des capteurs ──Continuer──► Menu ─┬─ Entraînements ─┬─ Modifier ──► Édition des blocs
                                            │                └─ Démarrer ──► Séance ──Terminer──► Bilan
                                            ├─ Réglages (cycliste, vélo, vitesses virtuelles)
                                            ├─ Sortie libre ──Terminer──► Bilan
                                            ├─ Mes sorties ──► Bilan d'une sortie
                                            └─ Capteurs (retour à la connexion)
```

- **Entraînements** (`Screens/Workouts/`) : la liste des séances structurées, avec leur profil
  (couleur = zone de puissance), leur durée et le TSS prévu. Deux exemples sont créés au premier
  lancement. La FTP est rappelée en haut (avec la date du dernier test), et un **test FTP**
  intégré est toujours proposé en premier : échauffement, trois accélérations, 20 min à fond en
  puissance libre, retour au calme. Si l'effort a été roulé au moins à 90 %, la nouvelle FTP
  (95 % de la moyenne) est enregistrée dans les Réglages à la fin et annoncée dans une fenêtre.
- **Édition** (`Screens/WorkoutEditor/`) : nom, puissances en watts ou en % de FTP (la FTP des
  Réglages, au moment de rouler ; changer d'unité convertit les valeurs), et les blocs. Chaque bloc
  a une durée, une puissance *fixe*, *libre* ou en *rampe* (début → fin), et une cadence *libre* ou
  *imposée*. Valeurs réglables avec − / + ou tapées (durée en `m:ss` ou en minutes). Blocs à
  monter, descendre, dupliquer, supprimer. Enregistrement à chaque changement.
- **Séance** (même écran que la sortie libre, `FreeRideScreen` avec un `Workout`) : bloc en cours
  (temps restant, consigne, cadence demandée, profil avec la position), et la liste des blocs à
  venir avec leur durée, leurs watts, leur cadence et dans combien de temps ils commencent ; les
  blocs passés disparaissent. Bloc fixe ou rampe : consigne ERG (une mise à jour par seconde dans
  une rampe). Bloc libre ou séance finie : le trainer passe en simulation sur le plat, sauf si le
  cycliste active « ERG » (watts réglables de ±5 ou ±10 W, enregistrés comme cible). Bouton
  *Bloc suivant* pour sauter un bloc ; la cadence s'affiche en orange à plus de 5 tr/min de la cible.
- **Sortie libre** (`Screens/FreeRide/`) : parcours plat, deux modes.
  *Puissance imposée* : le trainer impose les watts (ERG), réglables de ±5 ou ±10 W en roulant
  (départ à la dernière consigne utilisée).
  *Simulation réaliste* : le trainer reçoit pente 0 %, Crr 0,004 (pneus route) et
  ½·ρ·CdA = 0,25 kg/m (CdA 0,40 m², mains aux cocottes) ; il durcit avec la vitesse.
  Dans les deux cas, la vitesse et la distance viennent du même modèle physique
  (`Rides/Core/BikePhysics.cs`), avec le poids du cycliste + 8 kg de vélo.
- **Vitesses virtuelles** (*Réglages* depuis le menu, `Screens/Settings/`, calcul dans `Rides/Core/Gearing.cs`) :
  en simulation, on change de plateau et de pignon dans l'appli (boutons, ou flèches haut/bas
  au clavier). Préréglages : plateaux 53/39, 52/36, 50/34, 54/41, 50/37, 48/35, 46/33, 48/31,
  1× 40/42/44 ; cassettes 11 v. (11-25 à 11-36), 12 v. (10-28 à 11-36), 13 v. (9-36 à 10-44,
  denture régulière calculée) ; saisie libre des dentures. L'appli calcule la puissance que
  demande la vitesse obtenue (cadence × développement) avec le même modèle que la vitesse
  affichée, et la donne au trainer en consigne ERG, mise à jour en continu : la résistance suit
  la vitesse engagée et la cadence, quel que soit le pignon réel du vélo.
- **Bilan** (`Screens/RideSummary/`) : durée, distance, puissances moyenne / normalisée / max,
  TSS, facteur d'intensité, calories (travail en kJ ÷ 4,184 ÷ 24 % de rendement), cadence,
  vitesse, FC ; temps par zone de puissance (Coggan) ; courbes puissance, cadence, vitesse
  (avec la pente quand elle varie) et fréquence cardiaque. La cible (consigne ERG envoyée,
  cadence demandée) est tracée en blanc sur les courbes de puissance et de cadence, en
  entraînement comme en sortie libre à puissance imposée.
- **Mes sorties** (`Screens/RideHistory/`) : toutes les sorties, la plus récente en haut.

Les sorties sont enregistrées sur l'appareil, dans `user://rides/` : `{id}.json` (en-tête et
résumé) et `{id}.csv` (une ligne par seconde, lisible dans un tableur), plus `profile.json`
(FTP, poids, roue, vitesses virtuelles, dernier mode). Le CSV garde à chaque seconde la consigne
ERG et la cadence demandée (`target_power_w`, `target_cadence_rpm`, 0 sans cible). Les
entraînements sont dans `user://workouts/{id}.json`. Pendant une sortie, enregistrement toutes les 30 s et à la mise en
arrière-plan. Le calcul (`Rides/Core/`) ne dépend pas de Godot et est testé par `dotnet test`.

## Export Android avec le Bluetooth natif

1. Construire le plugin (JDK 17 et SDK Android requis) :
   ```
   cd android-plugin && ./gradlew assemble
   ```
   Les AAR sont copiés dans `addons/velom3d_ble/bin/`. La CI les publie aussi en artefact.
2. Dans Godot : *Projet > Installer le modèle de build Android*, puis dans le préréglage
   d'export Android, cocher *Use Gradle Build*.
3. Exporter. Au premier scan, l'appli demande l'autorisation « Appareils à proximité ».

Fermer Zwift ou toute autre appli avant : un trainer n'accepte souvent qu'une connexion à la fois.

iOS : pas encore de plugin. Un plugin iOS (CoreBluetooth) qui expose le même singleton
`Velom3dBle`, avec les mêmes méthodes et signaux, fonctionnera sans toucher au code C#.

## Tests

```
dotnet test UnitTests/Velom3d.Sensors.Tests
dotnet build Velom3d.csproj && godot --headless --import --path . && godot --headless --path . res://Tests/GodotSmokeTest.tscn
```
