# Format des maps de Velom3d

Une map est un fichier JSON, dans `Maps/` pour celles livrées avec l'appli. Il contient la route (une liste de points), le décor posé à côté de la route et quelques repères. Toutes les sources produisent ce même fichier :

| Source | `source` | Comment le fichier est fait |
|---|---|---|
| Générée | `procedural` | `ProceduralLoop.Generate(...)` : boucle sinueuse et plate, décor généré avec une graine. `Maps/boucle-5km.json` vient de là. |
| Route réelle | `gpx` | `GpxImport.ToMap(gpx, id)` : une trace GPX (Strava, Komoot, Garmin, OpenStreetMap…) devient des mètres, et le décor est généré le long de la route. |
| Faite à la main | `handmade` | Écrite ou modifiée dans un éditeur de texte. Quelques points suffisent avec `"smooth": true`. |

Les commentaires `//` et les virgules en fin de liste sont acceptés à la lecture.

## Exemple

```json
{
  "format": "velom3d-map",
  "version": 1,
  "id": "petit-col",
  "name": "Petit col",
  "description": "Aller simple qui monte de 20 m.",
  "source": "handmade",
  "loop": false,
  "smooth": true,
  "roadWidthM": 7,
  "road": [
    [0, 0, 0],
    [0, -500, 5],
    [300, -800, 15],
    [300, -1200, 20]
  ],
  "props": [
    { "kind": "house", "x": 20, "z": -250, "rotation": 90, "scale": 1 },
    { "kind": "conifer", "x": -12, "z": -600, "rotation": 0, "scale": 1.2 }
  ],
  "landmarks": [
    { "kind": "startArch", "distanceM": 0 }
  ]
}
```

## Champs

| Champ | Contenu |
|---|---|
| `format` | toujours `velom3d-map` : un autre fichier est refusé |
| `version` | version du format (1). L'appli refuse une version plus récente qu'elle |
| `id` | identifiant court, en général le nom du fichier sans `.json` |
| `name`, `description` | textes affichés |
| `source` | `procedural`, `gpx` ou `handmade` (pour information) |
| `loop` | `true` : la route revient au départ et on enchaîne les tours. `false` : aller simple, arrêt au bout. Sur une boucle, le dernier point ne répète pas le premier |
| `smooth` | `true` : la route est une courbe lisse qui passe par chaque point (Catmull-Rom). `false` : lignes droites entre les points |
| `roadWidthM` | largeur de l'asphalte, en mètres (7 par défaut) |
| `road` | points de la route, dans le sens de la marche : `[x, z]` ou `[x, z, altitude]`, en mètres |
| `props` | décor : `kind`, position `x` et `z` (m), `rotation` autour de la verticale (degrés), `scale` (1 = taille normale) |
| `landmarks` | repères posés à une distance du départ (`distanceM`). Pour l'instant : `startArch`, l'arche de départ avec le damier |

**Repère.** `x` va vers l'est et `z` vers le sud : le nord est vers `-z`. L'unité est le mètre et l'origine est libre ; un import GPX la place au premier point.

**Décor** (`kind`) : `bush` (arbuste), `leafyTree` (feuillu), `conifer` (sapin), `house` (maison, façade vers `-z` avant rotation), `farmhouse` (longère). Un type inconnu est ignoré, ce qui laisse une map plus récente s'ouvrir quand même.

**Altitude.** Elle est lue et donne la pente (`Course.GradePctAt`). Le rendu 3D et la résistance du home trainer restent plats pour l'instant ; ce sera la prochaine étape.

## Dans le code

- `World/Core/CourseMap.cs` : lecture (`FromJson`), écriture (`ToJson`, un point ou un élément de décor par ligne), construction de la route (`BuildCourse`) et du décor.
- `World/Core/Course.cs` : la route prête à rouler, redécoupée tous les 5 m : position, direction, altitude, pente.
- `World/RideWorld.cs` : charge `res://Maps/boucle-5km.json` par défaut, ou une autre map passée au constructeur. Si le fichier manque ou est abîmé, la boucle est régénérée.
- Le test `Default_map_file_is_what_the_generator_gives` vérifie que `Maps/boucle-5km.json` est bien la sortie du générateur.
