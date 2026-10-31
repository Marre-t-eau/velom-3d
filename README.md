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
                                            ├─ Réglages (cycliste, vélo, vitesses virtuelles, synchronisation)
                                            ├─ Sortie libre ──Terminer──► Bilan
                                            ├─ Mes sorties ──► Bilan d'une sortie
                                            └─ Capteurs (retour à la connexion)
```

- **Entraînements** (`Screens/Workouts/`) : la liste des séances structurées, avec leur profil
  (couleur = zone de puissance), leur durée et le TSS prévu. Deux exemples sont créés au premier
  lancement. La FTP est rappelée en haut (avec la date du dernier test), et un **test FTP**
  intégré est toujours proposé en premier (en watts fixes, comme le test rampe, pour ne pas
  dépendre de l'ancienne FTP) : échauffement, trois accélérations, 20 min à fond en
  puissance libre, retour au calme. Si l'effort a été roulé au moins à 90 %, la nouvelle FTP
  (95 % de la moyenne) est enregistrée dans les Réglages à la fin et annoncée dans une fenêtre.
  Second test au choix, le **test rampe** : paliers de 1 min en ERG à partir de 100 W,
  +20 W chaque minute, jusqu'à ne plus tenir (cadence sous 40 tr/min pendant 5 s, ou « J'arrête
  le test ») ; la FTP vaut 75 % de la meilleure minute (au moins 3 min de paliers).
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
- **Vue 3D** (`World/`) : pendant la sortie, un cycliste roule en fond d'écran sur un circuit
  plat de 5 km qui tourne en rond (arbustes, arbres, deux bois, quatre hameaux, arche de départ),
  caméra derrière lui comme dans Zwift. Mesures compactes en haut (puissance, cadence, cardio,
  vitesse, distance) ; toucher la puissance affiche la puissance moyenne pendant 5 s. Il avance de la distance calculée par l'appli, pédale à la
  cadence mesurée et s'arrête en pause. Tout est généré dans Godot en formes simples, sans fichier 3D.
  La map (route, décor, repères) est un fichier JSON : `Maps/boucle-5km.json`, format décrit dans
  `docs/format-des-maps.md`. Une map peut être générée (`ProceduralLoop`), importée d'une trace
  GPX réelle (`GpxImport`) ou écrite à la main.
  Lecture des maps et placement du décor dans `World/Core/` (C# pur, testé par `dotnet test`) ; le monde
  (`RideWorld`) est un nœud à part, réutilisable par d'autres écrans de sortie : il suffit de
  l'ajouter et de lui donner `Follow(distance, vitesse, cadence)` à chaque image.
  Pour le téléphone : décor instancié par tronçons de 500 m (seuls les proches sont dessinés),
  éclairage par sommet, une seule ombre (le cycliste), brouillard à 600 m, rendu 3D limité à
  ~1280 px de large puis agrandi.
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

Mise en page des écrans : chaque écran a sa scène (`.tscn`), à ouvrir et modifier dans l'éditeur
Godot (boutons, textes, marges, tailles de police). Le script C# du même nom ne fait que la logique :
il retrouve les nœuds par leur nom unique (`%Nom`, clic droit > *Accès comme nom unique*), branche
les signaux et met les valeurs à jour. Ce qui se répète selon les données a sa petite scène, créée
par le code pour chaque élément : `DeviceRow.tscn` (appareil), `RideCard.tscn` (sortie),
`WorkoutCard.tscn` (entraînement), `BlockCard.tscn` (bloc dans l'éditeur), `ConfirmPopup.tscn`
(fenêtre Non / Oui). Les styles communs (cartes, boutons à bascule verts, textes grisés, contour
lisible sur la 3D, taille de police par défaut) sont dans le thème `Screens/Common/Theme.tres`,
appliqué à tout le projet (*Projet > Paramètres > GUI > Thème*) : on choisit une variante dans
l'inspecteur, *Theme > Type Variation* (`MutedLabel`, `ChoiceButton`, `OverlayPanel`…).

Les sorties sont enregistrées sur l'appareil, dans `user://rides/`, deux fichiers par sortie
(détail dans `docs/format-des-donnees.md`) :
- `{id}.csv`, le fichier de données, sans aucun traitement : une ligne par valeur reçue,
  `t,type,sensor,value` (secondes depuis le départ, pauses comprises ; `power`, `cadence`, `hr`,
  `rr`, `resistance`, `speed`, `crank_revs`… ; n° du capteur dans l'en-tête, 0 pour l'appli).
  L'appli y note aussi ses événements et ses commandes : départ, consignes, vitesses, pause,
  reprise, fin, commandes envoyées au trainer (`set_power`, `set_grade`…).
- `{id}.json`, l'en-tête : réglages du jour (FTP, poids du cycliste et du vélo, Crr, CdA, roue),
  capteurs numérotés avec leur fiche lue à la connexion (fabricant, modèle, plages de puissance,
  de résistance et de pente, fonctions), et le bilan pour la liste.

Les mesures seconde par seconde (graphiques, bilan) sont reconstruites en rejouant le fichier de
données avec la même physique (`Rides/Core/RideEvents.cs`). Les sorties enregistrées avant ce
format (une ligne par seconde) restent lisibles. `profile.json` garde les réglages. Les
entraînements sont dans `user://workouts/{id}.json`. Pendant une sortie, enregistrement toutes les 30 s et à la mise en
arrière-plan. Le calcul (`Rides/Core/`) ne dépend pas de Godot et est testé par `dotnet test`.

## Synchronisation (facultative)

*Réglages > Synchronisation* : activer, saisir l'adresse d'un serveur, puis créer un compte ou se
connecter. Les réglages, les entraînements et les sorties sont alors copiés sur le serveur et
retrouvés sur les autres appareils du compte ; l'appli synchronise à chaque retour au menu, en
arrière-plan, ou avec *Synchroniser maintenant*. Sans serveur, rien ne change : tout reste sur
l'appareil, et ces fichiers restent la référence. Une modification faite sur l'appareil l'emporte
sur celle du serveur ; un fichier non modifié ici prend la version d'un autre appareil.

Le serveur est dans `Server/` (ASP.NET Core, base SQLite), à héberger soi-même, avec Docker ou
`dotnet run --project Server` : installation, réglages, règles et API dans `Server/README.md`.
Côté appli : `Sync/Core/` (C# pur, sans Godot), état local dans `user://sync.json`.

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
dotnet test UnitTests/Velom3d.Server.Tests      # serveur de synchronisation, de bout en bout
dotnet build Velom3d.csproj && godot --headless --import --path . && godot --headless --path . res://Tests/GodotSmokeTest.tscn
```
