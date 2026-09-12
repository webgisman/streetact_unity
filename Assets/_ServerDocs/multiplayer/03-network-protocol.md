# Protocole réseau téléphone ↔ serveur

## Transport : TCP, pas UDP

Le jeu n'est **pas** un tick temps réel synchronisé où chaque milliseconde de position compte
(comme un FPS). C'est un flux request/response : le téléphone envoie un ensemble d'intentions
pour son tour, le serveur simule la phase d'exécution (plusieurs secondes, comme le fait déjà
`TacticalPathManager.ExecuterTourCoroutine` en solo), puis renvoie un log d'événements
horodaté que le client rejoue.

TCP est le bon choix ici :
- Livraison fiable et ordonnée native (important : on ne veut pas qu'un événement de mort
  arrive avant l'événement de tir qui l'a causé).
- Traverse mieux les réseaux mobiles/NAT opérateur que de l'UDP brut.
- Client ET serveur sont tous les deux du code Unity/.NET — pas besoin du handshake HTTP d'un
  WebSocket, qui n'a d'intérêt que si un navigateur devait un jour parler au serveur.

Port : `7777/tcp` (conservé de ton choix initial).

## Format de trame

Chaque message est encodé en JSON UTF-8, précédé d'un entier 4 octets little-endian indiquant
sa longueur en octets (framing classique, évite d'avoir à parser un flux JSON en streaming) :

```
[ 4 octets: longueur N ] [ N octets: JSON UTF-8 ]
```

Chaque JSON a un champ `type` qui détermine le reste du schéma.

## Messages Client → Serveur

### `auth`
Premier message obligatoire après connexion TCP.
```json
{ "type": "auth", "access_token": "<jwt>" }
```

### `join_matchmaking`
`mode` détermine la file d'attente : les joueurs ne sont appariés qu'avec d'autres joueurs
ayant demandé le même mode.
```json
{ "type": "join_matchmaking", "mode": "deathmatch" }
```
`mode` = `"deathmatch"` (élimination, historique), `"zone_control"` (capture et tenue d'une
zone centrale — voir `04-unity-headless-server.md`), `"conquest"` (attaque d'une Zone de Conquête
précise, `zone_tile_x`/`zone_tile_y` — pas un appariement entre deux joueurs, résolu immédiatement
contre une garnison IA ou par capture instantanée, voir §12 de `08-known-issues-and-todo.md`) ou
`"practice_ai"` (2026-09-02, entraînement immédiat contre l'IA sur la carte par défaut, pour
patienter en attendant un vrai adversaire — même mécanique serveur que `conquest`, jamais de
véritable file d'attente, voir `08-known-issues-and-todo.md` §15).

### `submit_turn`
Correspond directement au contenu de `UnitAI.tacticalPath` pour chaque unité du joueur au
moment où il appuie sur "FIN TOUR" (`TacticalPathManager.LancerExecutionTour`).
```json
{
  "type": "submit_turn",
  "match_id": "uuid",
  "turn_number": 4,
  "orders": [
    {
      "unit_id": "Fantassin_1_1",
      "path": [
        { "x": 12.4, "y": 0.0, "z": -5.2, "action": 0 },
        { "x": 15.1, "y": 0.0, "z": -3.8, "action": 2 }
      ]
    }
  ]
}
```
`action` = valeur brute de `TacticalPathManager.NodeAction` (0=Continuer, 2=Guetter,
4=Escalade, 11=TirMortier, etc. — voir l'enum complet dans le code).

**Chaque point de `path` est un vrai checkpoint indépendant** (2026-09-02, correctif "prend le
raccourci" — voir `08-known-issues-and-todo.md` §16) : le serveur relie chaque paire de points
consécutifs par le VRAI chemin de la grille A* (celui qui contourne les bâtiments, pas une ligne
droite), et exécute la commande (`action`) de chaque point EXACTEMENT à l'arrivée à ce point précis
— jamais reportée à la fin du tableau `path` entier. Un ordre à N points déclenche donc jusqu'à N
recherches de chemin réelles côté serveur (`TacticalResolver.ExpandOrder`) ; plafonné à
`MaxOrderPathNodes = 40` points par unité par tour (voir `MatchSessionManager.cs`) pour borner ce
coût — un client modifié soumettant plus de points voit sa soumission entière rejetée (repli sur
"aucun ordre" pour cette unité, pas un troncage silencieux).

### `heartbeat`
Envoyé toutes les ~5s **en continu tant que la connexion TCP est ouverte** (pas seulement pendant
la planification) par `GameServerClient.Update()`. Le serveur applique un `ReceiveTimeout` fini sur
le socket après l'authentification (voir `GameServerBootstrap.HandleHandshake`) — sans ce heartbeat
régulier, un joueur simplement silencieux pendant sa réflexion serait pris pour un client
déconnecté dès que ce timeout expire.
```json
{ "type": "heartbeat" }
```

### `submit_deployment`
Placement manuel choisi par le joueur pendant la phase de déploiement (voir
`MatchSessionManager.RunDeploymentPhasePure`, jusqu'à `DeploymentSeconds` = 300 s — 45 s à
l'origine, relevé le 2026-09-06 sur demande explicite car trop court pour un vrai joueur qui
découvre son dock — en parallèle pour les deux joueurs, pas de tour par tour ici). `unit_type` =
valeur brute de `UnitSpawnerUI.UnitType` (0=Fantassin, 1=CharLeopard, 2=VehiculeCanon, 3=Mortier,
4=BarricadeRoutiere).

Budget autorisé, appliqué par `MatchSessionManager.FilterRosterToBudget` :
- jusqu'à 6 unités de combat (`MaxDeployedCombatUnits`) + jusqu'à 8 barricades
  (`MaxDeployedBarricades`), n'importe quel mélange ;
- **et** un budget en points de 8 (`CombatPointBudget`), coût par unité donné par
  `UnitTypeStats.DeploymentCost` : Fantassin 1, VehiculeCanon 2, Mortier 2, CharLeopard 3,
  BarricadeRoutiere 0. Ajouté le 2026-09-06 : sans lui, la composition la plus lourde autorisée
  (4 CharLeopard) était toujours strictement supérieure à toute composition mixte — aucune raison
  tactique de jamais varier son déploiement.

**Filtré placement par placement, JAMAIS rejeté en bloc** (corrigé le 2026-09-08 — voir §19.11 de
`08-known-issues-and-todo.md`). Chaque placement, dans l'ordre où le client les a envoyés, est
gardé s'il est individuellement valide (type défini dans l'énum, coordonnées finies) ET qu'il tient
encore dans les plafonds ci-dessus une fois les précédents comptés ; sinon CE placement précis est
écarté, les autres restent inchangés (même type, même position que soumis). Le dock de déploiement
ne connaissant que le nombre d'unités (pas encore le budget en points, voir `05-client-integration.
md`), un joueur qui privilégie des unités lourdes peut légitimement dépasser 8 points sans jamais
avoir été prévenu par le client — l'ancien comportement (rejet de la soumission ENTIÈRE, repli sur
une escouade fixe sans rapport avec ce que le joueur avait tapé) rendait ce cas totalement
courant et se vivait comme "mes unités ont été téléportées ailleurs par une IA". Le repli fixe
(`AutoDeployTeamFallback`) ne s'applique plus que si RIEN de la soumission n'a pu être conservé.

**Aucune zone de déploiement n'est plus imposée** (`ClampToDeploymentZone` a été désactivée le
2026-09-06, sur demande explicite, pour autoriser le placement manuel n'importe où sur la carte) —
une position soumise est utilisée telle quelle, sans recadrage. Les constantes de zone
(`Team1/2DeploymentZoneCenter`, `DeploymentZoneRadius` = 22 m) restent utilisées par le SEUL repli
automatique (`AutoDeployTeamFallback`), pas par le placement manuel.
```json
{
  "type": "submit_deployment",
  "placements": [
    { "unit_type": 0, "x": -23.4, "y": 0.0, "z": -24.1 },
    { "unit_type": 3, "x": -28.0, "y": 0.0, "z": -22.5 }
  ]
}
```

### `city_verify`
Équité géométrique, 2ème étage (2026-09-12). Le partage du JSON Overpass exact (`city_data_json` sur
`match_found`, voir plus bas) élimine la cause la plus fréquente de divergence client/serveur, mais
pas toutes : un décalage de version entre le build client et le build serveur (reconstruits
séparément), ou un cache disque local corrompu côté client, peuvent encore produire une ville
différente à partir du MÊME JSON. Envoyé UNE FOIS par CHAQUE client, dès que sa carte est prête,
AVANT que le dock de déploiement ne s'ouvre réellement (voir `MultiplayerMatchController.
VerifyCityGeometryWithServer`) — `city_building_hash` (`Novgov.TacticalCore.TacticalGridBuilder.
ComputeBuildingListHash`, déterministe/insensible à l'ordre/quantifié au mm) résume la ville que CE
client vient de générer localement ; `city_building_count` est purement informatif (logs serveur).
```json
{ "type": "city_verify", "city_building_hash": -1847203991, "city_building_count": 236 }
```

## Messages Serveur → Client

### `match_found`
```json
{ "type": "match_found", "match_id": "uuid", "team_id": 1, "opponent_username": "xX_Sniper_Xx", "mode": "deathmatch" }
```
Pour une VRAIE tuile GPS (Conquête toujours, Deathmatch/Zone de Contrôle si le serveur en a
assigné une — `has_home_tile: true`), le message porte aussi `zone_tile_x`/`zone_tile_y` ET,
depuis le 2026-09-05, `city_data_json` : le JSON Overpass BRUT exact que le serveur autoritaire a
lui-même utilisé pour générer cette tuile.

**Pourquoi (équité multijoueur)** : avant ce champ, chaque client refaisait sa PROPRE requête
Overpass indépendante pour la même tuile (`CityGenerator.GenerateCity()`) — rien ne garantissait
que les deux clients (ni le client et le serveur) reçoivent exactement les mêmes bâtiments : les 3
miroirs Overpass de repli ne sont pas garantis parfaitement synchronisés entre eux, et une édition
OSM peut survenir entre deux requêtes même séparées de quelques secondes. Le serveur est maintenant
la SEULE source qui interroge Overpass pour une vraie tuile (avec son propre cache disque persistant,
voir `CityGenerator.TryReadZoneCacheFromDisk`) ; les clients appliquent ce JSON tel quel
(`CityGenerator.LoadZoneFromServerData`) au lieu de le redemander. Absent (`null`/vide) pour la
carte "Default" (`has_home_tile: false`) — elle est un bundle Resources identique des deux côtés,
inutile de la faire transiter.
```json
{ "type": "match_found", "match_id": "uuid", "team_id": 1, "opponent_username": "xX_Sniper_Xx", "mode": "conquest", "zone_tile_x": 66701, "zone_tile_y": 44060, "city_data_json": "{\"version\":0.6,\"elements\":[...]}" }
```

### `city_verify_result`
Réponse à `city_verify` (voir plus haut), envoyée par `MatchSessionManager_Deployment.
HandleCityVerify`. `success: true` : la ville de ce client concorde avec la référence du serveur
(`MatchState.AuthoritativeCityHash`), rien de plus à faire. `success: false` : la structure de
bâtiments AUTORITAIRE COMPLÈTE du serveur (celle qui a réellement servi à figer `ms.World`) est
jointe dans `city_buildings` — JAMAIS un différentiel partiel, l'objectif est une ville identique à
100 % chez ce client, pas rapiécée. Le client reconstruit alors sa ville ENTIÈRE à partir de cette
structure (`CityGenerator.ApplyAuthoritativeBuildings`) plutôt que de re-parcourir le JSON Overpass
brut ou l'algorithme normal de subdivision/portes-fenêtres — celui-ci a justement produit un
résultat différent une première fois sur ce client, rien ne garantit qu'il ne diverge pas une
seconde fois de la même façon.
```json
{ "type": "city_verify_result", "success": true }
```
```json
{ "type": "city_verify_result", "success": false, "city_buildings": [
  { "id": 0, "height": 6.2, "footprint": [{"x": -254.1, "y": -224.7}, ...],
    "doors": [{ "position": {"x": -250.0, "y": -224.75}, "entry_direction": {"x": 0, "y": -1}, "width": 1.4 }],
    "windows": [{ "id": 1, "position": {"x": -247.0, "y": -224.75}, "outward_normal": {"x": 0, "y": -1}, "floor_level": 0 }]
  }, ...
] }
```

### `heartbeat` (serveur -> client)
Signal de vie ENVOYÉ PAR LE SERVEUR à toute connexion authentifiée restée silencieuse plus de 8 s
(`PlayerConnection.KeepaliveIntervalSeconds`), quelle que soit la phase : file d'attente, génération
de tuile avant `match_found`, déploiement, Conquête, planification. Le client n'a rien à en faire —
son `switch` l'ignore — c'est l'ARRIVÉE de la trame qui réarme le `ReceiveTimeout` de 25 s de sa
socket (`GameServerClient.Connect`).

**Pourquoi c'est une propriété de la CONNEXION et pas d'une phase** (2026-09-07) : il a d'abord
existé un keepalive local à la seule phase de déploiement, si bien que trois autres fenêtres de
silence ne l'avaient pas — notamment la file d'attente, où un joueur seul était TOUJOURS déconnecté
au bout de 25 s, ce qui rendait Deathmatch et Zone de Contrôle injouables sans un second joueur
immédiat. Voir §19.1 de `08-known-issues-and-todo.md`. Toute nouvelle phase est couverte d'office.
```json
{ "type": "heartbeat" }
```

### `turn_timer`
Diffusé chaque seconde pendant la phase de planification (plafond `PlanningSeconds` = 300 s depuis
le 2026-09-07, aligné sur `DeploymentSeconds`). **Le client ne l'affiche plus** (demande explicite
du joueur, 2026-09-06 : « enlève le temps dans tous les états ») — le champ reste envoyé et
alimente `lastServerSecondsRemaining` pour d'autres usages internes.
```json
{ "type": "turn_timer", "seconds_remaining": 42 }
```

### `opponent_ghosted`
Notifie qu'un camp est passé en mode Ghost (déconnecté ou timeout) — voir
[04-unity-headless-server.md](04-unity-headless-server.md). Envoyé à L'AUTRE camp uniquement (pas
au camp ghosté lui-même), pour CHAQUE tour où il reste ghosté, pas une seule fois à l'entrée dans
cet état.

**Le comportement réel derrière ce message dépend du mode**, et le client (`OnOpponentGhosted`)
choisit son texte en conséquence depuis le 2026-09-08 (bug trouvé suite à un signalement joueur —
voir §19.12 de `08-known-issues-and-todo.md`, ce message affichait auparavant "une IA a joué vos
unités" INCONDITIONNELLEMENT, y compris pour Deathmatch/Zone de Contrôle où c'est faux) :
- `deathmatch`/`zone_control` (chemin pur, `ApplyForPlayerPure`) : le camp ghosté ne fait
  strictement RIEN — ses unités tiennent leur position, aucune décision d'IA, elles ripostent
  seulement si attaquées (comme n'importe quelle unité) ;
- `conquest`/`practice_ai` (chemin vivant, `ApplyForPlayer`) : `TacticalAIPlanner.PlanifierTourIA()`
  tourne réellement pour ce camp — recherche de cible, couverture, tir.
```json
{ "type": "opponent_ghosted", "team_id": 2, "reason": "timeout" }
```

### `deployment_result`
Diffusé aux DEUX clients une fois la phase de déploiement résolue (soumission filtrée au budget,
ou repli automatique par camp si rien n'a pu en être conservé — voir `submit_deployment`
ci-dessus) — les DEUX camps y figurent, y compris le sien propre : le client ne fait jamais
confiance à ses propres positions candidates locales et respawn exactement cette liste
(`MultiplayerMatchController.OnDeploymentResult`, `unit_id` réutilisé tel quel par
`UnitSpawnerUI.SpawnUnitAt(..., forcedName: ...)` pour que `turn_result`/`PlaySnapshotsCoroutine`
retrouve ensuite chaque unité par ce même nom).

`reason` (2026-09-08) : `"roster_trimmed"` si AU MOINS UN placement soumis par CE joueur a été
écarté individuellement (budget dépassé) alors que le reste de sa soumission a bien été conservé —
absent/null sinon (soumission entièrement acceptée, ou entièrement remplacée par le repli
automatique — dans ce dernier cas ce n'est pas un "trim" mais un remplacement total, voir §19.11).
Le client affiche un avertissement quand ce champ vaut `"roster_trimmed"`.
```json
{
  "type": "deployment_result",
  "deployed_units": [
    { "unit_id": "Fantassin_1_1", "unit_type": 0, "team_id": 1, "x": -23.4, "y": 0.0, "z": -24.1 }
  ],
  "reason": null
}
```

### `turn_result`
**Implémentation réelle : rejeu par snapshots, pas par log d'événements discret.** Plutôt que
d'instrumenter chaque `ShootAt()`/`TakeDamage()`/`IsUnitSpottedByTeam()` (invasif dans
`UnitAI_Combat.cs`, risque de régression sur le mode solo), le serveur échantillonne l'état
complet de toutes les unités (vivantes et fraîchement mortes) toutes les `snapshot_interval_ms`
pendant qu'il fait tourner la vraie simulation (`UnitAI.ExecuterOrdres()` + les mêmes conditions
d'attente que `TacticalPathManager.ExecuterTourCoroutine`). Le client rejoue en interpolant
d'un snapshot à l'autre, sans jamais recalculer de NavMesh ni de ligne de vue localement — voir
`Assets/Scripts/Server/MatchSessionManager.cs` (capture) et
`Assets/Scripts/Network/MultiplayerMatchController.cs` (lecture).
```json
{
  "type": "turn_result",
  "turn_number": 4,
  "snapshot_interval_ms": 100,
  "snapshots": [
    {
      "t": 0,
      "zone_progress_team1": 0,
      "zone_progress_team2": 0,
      "units": [
        { "unit_id": "Fantassin_1_1", "x": 12.4, "y": 0, "z": -5.2, "ry": 90.0, "health": 100, "dead": false, "shooting": false }
      ]
    },
    {
      "t": 1300,
      "zone_progress_team1": 5,
      "zone_progress_team2": 0,
      "units": [
        { "unit_id": "Fantassin_1_1", "x": 12.4, "y": 0, "z": -5.2, "ry": 90.0, "health": 0, "dead": true, "shooting": false }
      ]
    }
  ]
}
```
`zone_progress_team1`/`zone_progress_team2` (0 à 100) ne sont pertinents qu'en mode
`zone_control` — toujours à 0 en mode `deathmatch`.
La mort et les dégâts sont donc *déduits* des variations de `health`/`dead` entre deux
snapshots côté client (déclenchement de l'animation de mort via `UnitAI.ApplyNetworkDeath()`),
plutôt que transmis comme événements discrets `shot`/`death`.

### `match_over`
`winner_team` = 0 signifie match nul (anéantissement mutuel, double déconnexion, ou égalité de
progression en mode `zone_control` à la limite de tours). `your_new_rating`/`rating_delta` sont
propres à chaque destinataire (calcul ELO, K=32, voir `MatchSessionManager.UpdateRatings()`).
```json
{ "type": "match_over", "winner_team": 2, "your_new_rating": 1014, "rating_delta": 14 }
```

## Mapping avec le code existant

- `submit_turn.orders[].path[]` = exactement `UnitAI.tacticalPath` (liste de
  `TacticalPathManager.TacticalNode`), sérialisé tel quel.
- `turn_result.snapshots` = échantillonnage direct de `transform.position`, `transform.
  eulerAngles.y`, `health`, `isDead` et `IsShootingNow` pour chaque `UnitAI` de la scène,
  pendant que le serveur fait tourner **exactement** la même simulation que le mode solo
  (`ExecuterOrdres()`, `IsMovingOrActing()`, `HasActiveTargetInRange()`) — aucune duplication de
  la logique de combat/déplacement, zéro modification de `UnitAI_Combat.cs` ou
  `UnitAI_Movement.cs`.
