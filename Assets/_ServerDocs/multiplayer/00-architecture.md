# 00 — Architecture actuelle de Novgov (état au 2026-10-03)

Document de référence : ce que le jeu est aujourd'hui et où se trouve chaque morceau. L'historique
détaillé des sessions passées est dans l'historique git (les anciens comptes rendus 09 à 15 ont été
fusionnés ici le 2026-10-03).

## 1. Le jeu en deux modes

Écran de démarrage (`StartupMenuScreen.uxml`, `GameManagerUI`) :

- **JOUER SOLO** : partie hors ligne sur une carte par défaut (`Resources/DefaultCityData.json`),
  contre l'ordinateur (`TacticalAIPlanner`). Placement des unités avec le dock « QG Renforts »
  (`UnitSpawnerUI`), puis tours : tracer des trajectoires, FIN DE TOUR, exécution, jusqu'à la
  victoire. Menu PAUSE (`InGameMenuController`) : reprendre, recommencer, menu principal.
- **JOUER EN LIGNE** : la ville réelle du joueur (tuiles OpenStreetMap), découpée en **quartiers**
  (= tuiles Slippy Map zoom 17, `CityGenerator.ZONE_ZOOM`). **Aucune IA en multijoueur.**

## 2. Parcours en ligne

1. **Compte** (`AuthScreen.uxml`, `SupabaseAuthClient`) — connexion, création, ou reconnexion
   automatique (jeton de rafraîchissement mémorisé).
2. **Une seule fois** : « Où installer votre QG ? » (`LocationPromptScreen.uxml`) puis la position
   GPS fixe le quartier du QG (`ZoneManager.InitializeHomeZoneFromGps`, mémorisé). Dans l'Éditeur,
   les comptes de test ont un quartier fixe (`EditorTestPlayers`, voir `07-tests.md`).
3. **Écran CONQUÊTE** (`ConquestScreen.uxml`) — en-tête (MENU, profil + PA, RAPPORTS), alerte
   « quartier assiégé » avec DÉFENDRE, deux onglets :
   - **CARTE** (`Novgov.UI.ZoneMapController`) : vraie carte OSM 3×3 (quartier courant + 8 voisins),
     statuts LIBRE / À VOUS / ⚔ pseudo / PROTÉGÉ / hors de portée. Toucher une case ouvre sa fiche :
     PRENDRE CE QUARTIER, M'Y RENDRE, LANCER UN SIÈGE. Sons : `UiSfx`.
   - **GESTION** : aide « Comment ça marche ? », SIÈGES, CASERNE, MES QUARTIERS, CLASSEMENT,
     déconnexion du compte.
   Vocabulaire du joueur : `QuartierText` (« quartier au Nord de votre QG », jamais de coordonnées).

## 3. Règles de jeu en ligne

- **Prendre un quartier libre** : gratuit, instantané, sans combat. Portée : sans aucun quartier, le
  premier peut être n'importe où ; ensuite, il doit toucher (4 directions) un quartier possédé.
- **Revenu** : chaque quartier rapporte 10 PA × niveau de bâtiment toutes les 5 min (versé par
  l'instance `game-server-1` uniquement). Bonus quotidien de 50 PA. PA dépensés à la CASERNE
  (recrutement) et dans MES QUARTIERS (amélioration du bâtiment, niveaux 1 à 3).
- **Siège** = prendre le quartier d'un autre joueur. `start_siege()` (RPC) crée le siège (échéance
  6 h) et prévient le défenseur. La bataille est une **vraie partie au tour par tour** entre les deux
  joueurs dès qu'ils sont en ligne en même temps (le premier arrivé attend l'autre) : déploiement,
  puis à chaque tour planification → FIN DE TOUR → simulation serveur → rejeu synchronisé, jusqu'à
  l'élimination d'un camp (ou 60 tours). Victoire de l'attaquant : le quartier change de mains, les
  PA du défenseur sont pillés. Égalité : le défenseur garde. Quartier protégé 6 h ensuite. Sans
  bataille avant l'échéance : résolution automatique avec les troupes des casernes des deux joueurs.
- Joueur absent pendant une bataille : ses unités tiennent leur position et ripostent (pas d'IA).
- Classement ELO (`profiles.rating`) : batailles de siège, +8 par quartier pris.

## 4. Serveur de jeu (Unity headless, `Assets/Scripts/Server/`)

Pool de 3 instances (`game-server-1/2/3`, ports 7777/7778/7779) sur le VPS `novgov.com`. Un seul
combat à la fois par instance (scène et NavMesh partagés, `matchInProgress`).

| Fichier | Rôle |
|---|---|
| `GameServerBootstrap.cs`, `JwtValidator.cs`, `PlayerConnection.cs` | Écoute TCP, authentification JWT, connexion d'un joueur, signal de vie |
| `MatchSessionManager.cs` | Aiguillage des demandes (`conquest`, `siege_battle`), revenu des quartiers, registre `server_instances` |
| `MatchSessionManager_Conquest.cs` | Capture d'un quartier libre ; outils de persistance des quartiers ; chargement d'un quartier sur le serveur |
| `MatchSessionManager_SiegeBattle.cs` | Salle d'attente d'un siège, démarrage de la bataille, conséquences en fin de partie |
| `MatchSessionManager_MatchLive.cs` | Moteur de partie au tour par tour entre deux joueurs |
| `MatchSessionManager_Deployment.cs` | Phase de déploiement (plafonds : 6 unités de combat, 2 mortiers, 8 barricades) |
| `MatchSessionManager_CombatLive.cs`, `_CombatRealEngine.cs` | Ordres reçus, simulation d'un tour au vrai moteur Unity, snapshots par équipe (brouillard de guerre) |
| `MatchSessionManager_Siege.cs` | Données des sièges, résolution automatique à l'échéance, `ApplySiegeOutcome` |
| `MatchSessionManager_Persistence.cs` | PostgREST (service_role), matches, classement |
| `UnitTypeStats.cs` | Caractéristiques des unités, partagées client/serveur |

## 5. Client en ligne (`Assets/Scripts/Network/`, `Assets/Scripts/UI/`)

- `MultiplayerMatchController` : états (connexion, Conquête, attente, déploiement, partie, fin),
  écrans de gestion, salle d'attente du siège (ANNULER), partie (ordres, rejeu des snapshots).
- `GameServerClient` / `NetMessage` : TCP, messages JSON préfixés par leur longueur (voir `03`).
- `GameManagerUI` : démarrage, compte → QG, rechargement de scène vers Solo ou Conquête
  (`ReloadSceneThen`).
- `UIScreenManager` : un écran UXML par état (`Assets/Resources/UI/`), échelle de l'UI
  proportionnelle au petit côté de l'écran (référence 1080 px).

## 6. Données (Supabase auto-hébergé, `schema.sql`)

`profiles` (pseudo, rating, action_points), `zones` (quartiers : propriétaire, niveau, bouclier,
bâtiment QG), `zone_sieges` (pending / battle / resolving / resolved), `player_roster` (caserne),
`notifications` (rapports), `matches`/`match_participants`, `server_instances`. Écritures sensibles
uniquement par fonctions SECURITY DEFINER (`buy_unit`, `upgrade_building`, `start_siege`,
`claim_daily_bonus`, `pillage_action_points`) ou par le serveur de jeu (service_role).

## 7. Documents

`01` déploiement VPS · `02` authentification · `03` protocole réseau · `06` sécurité ·
`07` tests · `08` limites connues · `schema.sql` · `docker-compose.yml` · `bootstrap-db.sh`.
Règles techniques du moteur : `Assets/Docs/` (NavMesh/pathfinding, import Mixamo, organisation des scripts).
