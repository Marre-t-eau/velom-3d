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
dotnet run --project Server
```

Il écoute alors sur le port 8080 de toutes les interfaces et affiche au démarrage l'adresse à
saisir dans l'appli (`Adresse pour l'appli : http://192.168.1.10:8080`). Sans `http://`, l'appli
prend `http://` pour une IP du réseau local et `https://` pour un nom de domaine.

Le serveur écoute en HTTP. Pour un serveur accessible depuis Internet, mettez un proxy HTTPS devant,
par exemple [Caddy](https://caddyserver.com) (certificat automatique) :

```
velom3d.mon-domaine.fr {
    reverse_proxy localhost:8080
}
```

et réglez `Velom3d__TrustProxy=true` pour que le serveur voie la vraie adresse des clients. Sur un
réseau local, `http://192.168.1.10:8080` marche aussi (les mots de passe passent alors en clair sur le réseau).

## L'appli ne trouve pas le serveur

Dans l'ordre :
1. **Depuis le navigateur du téléphone**, ouvrez `http://<IP du PC>:8080/api/info`. Il doit afficher
   `{"name":"velom3d",…}`. Sinon, c'est le réseau, pas l'appli :
   - le téléphone et le PC sont sur le même Wi-Fi (pas le réseau invité ni la 4G, et sans
     « isolation des clients » sur la box) ;
   - le pare-feu du PC laisse entrer le port 8080. Sous Windows, le réseau Wi-Fi doit être
     « Privé », et il faut autoriser le port (PowerShell administrateur) :
     `New-NetFirewallRule -DisplayName "Velom3d" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow` ;
   - le serveur écoute bien sur toutes les interfaces : `Now listening on: http://0.0.0.0:8080`
     dans son journal (pas `localhost`). Lancé depuis Visual Studio ou Rider, il prend le profil de
     `Server/Properties/launchSettings.json` (port 8080 sur toutes les interfaces) ; un port au
     hasard (`localhost:49189`…) veut dire que l'IDE a créé son propre profil ou passe par IIS
     Express : choisissez le profil « Velom3d.Server ». Le serveur prévient au démarrage s'il
     n'écoute que le PC.
2. **Si le navigateur y arrive mais pas l'appli** : l'export Android doit avoir la permission
   Internet. Elle est déclarée par le plugin Bluetooth (AAR reconstruit après cette version), ou
   cochez *Permissions > Internet* dans le préréglage d'export Android. L'appli affiche la cause
   exacte sous le bouton (« Connection refused », délai dépassé…).

## Réglages

Dans `appsettings.json` ou en variables d'environnement (`Velom3d__Nom`) :

| Réglage | Par défaut | Rôle |
|---|---|---|
| `Velom3d__Database` | `data/velom3d.db` (`/data/velom3d.db` dans Docker) | fichier de la base |
| `Velom3d__AllowRegistration` | `true` | `false` : plus de nouveaux comptes (serveur fermé, une fois les vôtres créés) |
| `Velom3d__AuthRequestsPerMinute` | `20` | essais de connexion par minute et par adresse IP |
| `Velom3d__RegistrationsPerHour` | `5` | comptes créés par heure et par adresse IP |
| `Velom3d__MaxFailedLogins` | `5` | échecs de suite sur un compte avant qu'il soit bloqué |
| `Velom3d__LockoutMaxMinutes` | `60` | blocage le plus long (1 min, puis 2, 4, 8… jusqu'à cette durée) |
| `Velom3d__SessionDays` | `90` | un appareil qui ne s'est pas synchronisé depuis ce nombre de jours doit se reconnecter |
| `Velom3d__TrustProxy` | `false` | `true` derrière un proxy (lit `X-Forwarded-For`) |
| `Velom3d__Smtp__Host` | vide | serveur d'envoi d'e-mails ; vide : pas d'e-mails (voir « Mot de passe oublié ») |
| `Velom3d__Smtp__Port` / `__Ssl` | `587` / `true` | port et STARTTLS (le port 465, TLS direct, n'est pas géré) |
| `Velom3d__Smtp__User` / `__Password` | vides | identifiants SMTP |
| `Velom3d__Smtp__From` | `User` | adresse d'expédition |
| `Velom3d__Smtp__ServerName` | `Velom3d` | nom du serveur dans l'objet des e-mails |
| `ASPNETCORE_HTTP_PORTS` | `8080` dans Docker | port d'écoute |

Sauvegarde : copier le fichier de la base (`sqlite3 velom3d.db ".backup copie.db"` serveur allumé).

## Sécurité des comptes

- **Mots de passe** : 8 caractères au moins ; les plus courants (« motdepasse », « azerty123 »,
  « 12345678 »…), ceux qui contiennent le nom d'utilisateur et les suites sont refusés. Gardés en
  PBKDF2-SHA256 (600 000 itérations, sel aléatoire) ; un mot de passe enregistré avec moins
  d'itérations (versions précédentes) est refait à la connexion suivante.
- **Contre les essais en boucle**, deux protections qui s'ajoutent :
  - par adresse IP : 20 essais par minute sur les routes de compte, 5 créations de compte par heure ;
  - par compte, quelle que soit l'adresse : après 5 échecs de suite, le compte est bloqué 1 minute,
    puis 2, 4, 8… jusqu'à 1 heure. Une connexion réussie remet le compteur à zéro, et il s'oublie
    après 24 heures sans échec. Un nom qui n'existe pas se bloque de la même façon : rien n'indique
    si un compte existe. Les appareils déjà connectés continuent de synchroniser pendant un blocage.
- **Sessions** : un jeton aléatoire par appareil (seule son empreinte est en base), qui expire après
  90 jours sans synchronisation. Dans l'appli, *Déconnecter mes autres appareils* les révoque tous
  sauf celui-ci ; changer de mot de passe ou le réinitialiser aussi.
- **HTTPS** : sur un réseau local en `http://`, le mot de passe passe en clair sur le Wi-Fi. Depuis
  Internet, mettez toujours le proxy HTTPS décrit plus haut.

## Mot de passe oublié

Trois façons, de la plus simple à la plus exceptionnelle :

1. **Code de secours.** À la création du compte, l'appli affiche une seule fois un code
   (`7K2M-Q9XD-4HTR-B8WN`) à noter. Dans *Réglages > Synchronisation > Mot de passe oublié*, ce
   code permet de choisir un nouveau mot de passe ; il est alors remplacé par un nouveau (affiché
   aussi), et tous les appareils sont déconnectés. Un compte connecté peut demander un nouveau code
   (mot de passe demandé) ; c'est utile aux comptes créés avant les codes de secours.
2. **E-mail**, si le serveur a un SMTP configuré (`Velom3d__Smtp__*`) et que le compte a une adresse
   (facultative, à la création ou plus tard dans les Réglages). *Recevoir un code par e-mail*
   envoie un code de 8 caractères valable 30 minutes, qui ne sert qu'une fois. Au plus un e-mail par
   minute et par compte ; la réponse est la même que le compte existe ou non.
   Pour Gmail : `smtp.gmail.com`, port 587, et un « mot de passe d'application » du compte Google.
3. **L'administrateur du serveur**, sur la machine du serveur :

   ```
   dotnet run --project Server -- users                 # liste des comptes
   dotnet run --project Server -- recovery-code black   # nouveau code de secours pour « black »
   dotnet run --project Server -- unlock black          # lève un blocage après trop d'essais
   ```

   Avec Docker : `docker compose -f Server/docker-compose.yml exec velom3d dotnet Velom3d.Server.dll recovery-code black`.
   Le code affiché se saisit dans *Mot de passe oublié*, comme un code de secours.

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
| `GET /api/info` | `{"name":"velom3d","version":1,"registration":true,"recovery":true,"email":false}` |
| `POST /api/account/register` | `{"username","password","email"?}` → `{"username","token","recoveryCode","email"}` (201). Nom : 3 à 32 caractères `A-Z a-z 0-9 . - _`, sans distinction de casse ; mot de passe : voir « Sécurité des comptes » |
| `POST /api/account/login` | `{"username","password"}` → `{"username","token","email","recoveryCodeMissing"}` ; 429 si le compte est bloqué |
| `POST /api/account/forgot` | `{"username"}` → 202 ; envoie un code à l'adresse du compte, s'il en a une |
| `POST /api/account/recover` | `{"username","recoveryCode","newPassword"}` (code de secours ou reçu par e-mail) → comme `login`, avec le nouveau `recoveryCode` après un code de secours ; déconnecte tous les autres appareils |
| `POST /api/account/recovery-code` 🔒 | `{"password"}` → `{"recoveryCode"}` ; l'ancien code ne marche plus |
| `POST /api/account/email` 🔒 | `{"password","email"}` ; `email` vide la retire |
| `POST /api/account/logout` 🔒 | révoque le jeton |
| `POST /api/account/logout-others` 🔒 | révoque tous les jetons du compte sauf celui-ci |
| `POST /api/account/password` 🔒 | `{"password","newPassword"}` ; déconnecte les autres appareils |
| `DELETE /api/account` 🔒 | `{"password"}` ; supprime le compte et toutes ses données |

Un mauvais mot de passe répond 401 à la connexion et 403 sur les routes 🔒 (un 401 y veut dire
« session expirée », et l'appli oublie alors son jeton).
| `GET /api/sync?since=N` 🔒 | changements après le n° `N` : `{"changes":[…],"cursor","more"}` (200 par page) |
| `POST /api/sync` 🔒 | `{"changes":[…]}` (200 au plus) → `{"accepted"}` |
| `PUT /api/rides/{id}/data` 🔒 | fichier de données CSV de la sortie, brut ou `Content-Encoding: gzip` |
| `GET /api/rides/{id}/data` 🔒 | le CSV (gzip si `Accept-Encoding: gzip`), 404 s'il n'est pas encore là |

Un changement : `{"kind","id","updatedAt","deleted","content","seq"}`. `kind` vaut `profile`
(id `profile`, le fichier `profile.json`), `workout` (`workouts/{id}.json`) ou `ride` (`rides/{id}.json`) ;
`content` est le fichier JSON tel qu'il est sur l'appareil. Les ids ne contiennent que
`A-Z a-z 0-9 - _` (64 caractères au plus).

Base : tables `users` (mot de passe en PBKDF2-SHA256, empreintes du code de secours et du code
e-mail, adresse e-mail), `tokens` (empreinte SHA-256 des jetons seulement), `auth_failures`
(échecs de connexion par nom, pour le blocage), `documents` (dernier état de chaque fichier, suppressions comprises) et `ride_data`
(CSV compressés).

## Tests

```
dotnet test UnitTests/Velom3d.Server.Tests
```

Serveur en mémoire et plusieurs « appareils » (dossiers temporaires) : comptes, envoi et réception,
modifications et suppressions dans les deux sens, priorité aux données locales, premier passage
d'un appareil, comptes séparés, sortie reçue avant ses données, session révoquée ; et pour les
comptes (`AuthTests.cs`) : mots de passe faibles, blocage, codes de secours et e-mail, mise à
niveau des anciens mots de passe, expiration des sessions, commandes d'administration.
