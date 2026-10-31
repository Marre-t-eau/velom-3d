# Serveur de synchronisation Velom3d

Petit serveur à héberger soi-même (ASP.NET Core 8, base SQLite dans un seul fichier). Il garde une
copie des **réglages**, des **entraînements planifiés** et des **sorties** (en-tête et fichier de
données) de chaque compte, pour les retrouver sur tous ses appareils.

L'appli marche sans lui : la synchronisation est désactivée par défaut, et les fichiers du
téléphone font foi (voir « Règles » plus bas). Dans l'appli : *Réglages > Synchronisation*, activer,
saisir l'adresse du serveur, puis *Créer un compte* ou *Se connecter*. Ensuite l'appli synchronise
à chaque retour au menu, ou avec *Synchroniser maintenant*.

## Lancer

Avec Docker (depuis la racine du dépôt) :

```
docker compose -f Server/docker-compose.yml up -d
```

Sans Docker (SDK .NET 8) :

```
dotnet run --project Server --urls http://0.0.0.0:8080
```

Le serveur écoute en HTTP. Pour un serveur accessible depuis Internet, mettez un proxy HTTPS devant,
par exemple [Caddy](https://caddyserver.com) (certificat automatique) :

```
velom3d.mon-domaine.fr {
    reverse_proxy localhost:8080
}
```

et réglez `Velom3d__TrustProxy=true` pour que le serveur voie la vraie adresse des clients. Sur un
réseau local, `http://192.168.1.10:8080` marche aussi (les mots de passe passent alors en clair sur le réseau).

## Réglages

Dans `appsettings.json` ou en variables d'environnement (`Velom3d__Nom`) :

| Réglage | Par défaut | Rôle |
|---|---|---|
| `Velom3d__Database` | `data/velom3d.db` (`/data/velom3d.db` dans Docker) | fichier de la base |
| `Velom3d__AllowRegistration` | `true` | `false` : plus de nouveaux comptes (serveur fermé, une fois les vôtres créés) |
| `Velom3d__AuthRequestsPerMinute` | `20` | essais de connexion par minute et par adresse IP |
| `Velom3d__TrustProxy` | `false` | `true` derrière un proxy (lit `X-Forwarded-For`) |
| `ASPNETCORE_HTTP_PORTS` | `8080` dans Docker | port d'écoute |

Sauvegarde : copier le fichier de la base (`sqlite3 velom3d.db ".backup copie.db"` serveur allumé).

## Règles de synchronisation

- **Les données locales sont prioritaires.** Un fichier modifié ou supprimé sur l'appareil depuis sa
  dernière synchronisation est envoyé et remplace la version du serveur, même si un autre appareil
  l'a changé entre-temps.
- Un fichier que l'appareil n'a pas touché prend la version du serveur (modifiée ou supprimée sur
  un autre appareil).
- Premier passage d'un appareil sur un compte : un fichier présent des deux côtés garde la version
  la plus récente (`UpdatedAt`), celle de l'appareil à égalité. Une même sortie reste celle de l'appareil.
- Le fichier de données d'une sortie est envoyé (compressé) avant son en-tête. Si un autre appareil
  voit l'en-tête avant les données (envoi coupé), il réessaie à la synchronisation suivante.
- Rien n'est jamais supprimé sur l'appareil sans une suppression faite sur un autre appareil du compte.
  Se déconnecter ou supprimer son compte laisse les fichiers locaux intacts.

Code côté appli : `Sync/Core/SyncClient.cs` (C# pur, sans Godot) ; état local dans `user://sync.json`
(adresse, compte, jeton de connexion, empreinte de chaque fichier à la dernière synchronisation).

## API

Toutes les réponses sont en JSON (camelCase) ; les erreurs ont la forme `{"error": "message en français"}`.
Les routes marquées 🔒 demandent `Authorization: Bearer <jeton>`.

| Route | Rôle |
|---|---|
| `GET /api/info` | `{"name":"velom3d","version":1,"registration":true}` |
| `POST /api/account/register` | `{"username","password"}` → `{"username","token"}` (201). Nom : 3 à 32 caractères `A-Z a-z 0-9 . - _`, sans distinction de casse ; mot de passe : 8 caractères au moins |
| `POST /api/account/login` | même corps → `{"username","token"}` |
| `POST /api/account/logout` 🔒 | révoque le jeton |
| `POST /api/account/password` 🔒 | `{"password","newPassword"}` ; déconnecte les autres appareils |
| `DELETE /api/account` 🔒 | `{"password"}` ; supprime le compte et toutes ses données |
| `GET /api/sync?since=N` 🔒 | changements après le n° `N` : `{"changes":[…],"cursor","more"}` (200 par page) |
| `POST /api/sync` 🔒 | `{"changes":[…]}` (200 au plus) → `{"accepted"}` |
| `PUT /api/rides/{id}/data` 🔒 | fichier de données CSV de la sortie, brut ou `Content-Encoding: gzip` |
| `GET /api/rides/{id}/data` 🔒 | le CSV (gzip si `Accept-Encoding: gzip`), 404 s'il n'est pas encore là |

Un changement : `{"kind","id","updatedAt","deleted","content","seq"}`. `kind` vaut `profile`
(id `profile`, le fichier `profile.json`), `workout` (`workouts/{id}.json`) ou `ride` (`rides/{id}.json`) ;
`content` est le fichier JSON tel qu'il est sur l'appareil. Les ids ne contiennent que
`A-Z a-z 0-9 - _` (64 caractères au plus).

Base : tables `users` (mot de passe en PBKDF2-SHA256), `tokens` (empreinte SHA-256 des jetons
seulement), `documents` (dernier état de chaque fichier, suppressions comprises) et `ride_data`
(CSV compressés).

## Tests

```
dotnet test UnitTests/Velom3d.Server.Tests
```

Serveur en mémoire et plusieurs « appareils » (dossiers temporaires) : comptes, envoi et réception,
modifications et suppressions dans les deux sens, priorité aux données locales, premier passage
d'un appareil, comptes séparés, sortie reçue avant ses données, session révoquée.
