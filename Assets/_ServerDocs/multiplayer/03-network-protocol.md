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
`MatchSessionManager.RunDeploymentPhase`, jusqu'à 45s, en parallèle pour les deux joueurs — pas de
tour par tour ici). `unit_type` = valeur brute de `UnitSpawnerUI.UnitType` (0=Fantassin,
1=CharLeopard, 2=VehiculeCanon, 3=Mortier, 4=BarricadeRoutiere). Budget autorisé : jusqu'à 4 unités
de combat (n'importe quel mélange) + jusqu'à 8 barricades — une soumission qui dépasse ce budget,
ou contient un type hors de l'enum, est rejetée EN BLOC côté serveur (repli automatique pour tout
ce camp, voir `deployment_result` ci-dessous) plutôt que partiellement acceptée. Chaque position est
de toute façon recadrée dans la zone de déploiement légale du camp (cercle de 22m autour du même
point d'ancrage que l'auto-déploiement) avant d'être utilisée — jamais rejetée pour une simple
imprécision de tap, mais impossible de déployer au contact immédiat de l'adversaire.
```json
{
  "type": "submit_deployment",
  "placements": [
    { "unit_type": 0, "x": -23.4, "y": 0.0, "z": -24.1 },
    { "unit_type": 3, "x": -28.0, "y": 0.0, "z": -22.5 }
  ]
}
```

## Messages Serveur → Client

### `match_found`
```json
{ "type": "match_found", "match_id": "uuid", "team_id": 1, "opponent_username": "xX_Sniper_Xx", "mode": "deathmatch" }
```

### `turn_timer`
Diffusé chaque seconde pendant la phase de planification, pour afficher le décompte côté
client.
```json
{ "type": "turn_timer", "seconds_remaining": 42 }
```

### `opponent_ghosted`
Notifie qu'un joueur est passé en mode Ghost (déconnecté ou timeout) — voir
[04-unity-headless-server.md](04-unity-headless-server.md).
```json
{ "type": "opponent_ghosted", "team_id": 2, "reason": "timeout" }
```

### `deployment_result`
Diffusé aux DEUX clients une fois la phase de déploiement résolue (soumission validée+recadrée, ou
repli automatique par camp, voir `submit_deployment` ci-dessus) — les DEUX camps y figurent, y
compris le sien propre : le client ne fait jamais confiance à ses propres positions candidates
locales et respawn exactement cette liste (`MultiplayerMatchController.OnDeploymentResult`,
`unit_id` réutilisé tel quel par `UnitSpawnerUI.SpawnUnitAt(..., forcedName: ...)` pour que
`turn_result`/`PlaySnapshotsCoroutine` retrouve ensuite chaque unité par ce même nom).
```json
{
  "type": "deployment_result",
  "deployed_units": [
    { "unit_id": "Fantassin_1_1", "unit_type": 0, "team_id": 1, "x": -23.4, "y": 0.0, "z": -24.1 }
  ]
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
