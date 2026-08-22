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
```json
{ "type": "join_matchmaking" }
```

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

### `heartbeat`
Envoyé toutes les ~5s pendant la phase de planification, pour détecter une déconnexion avant
l'expiration du timer de tour.
```json
{ "type": "heartbeat" }
```

## Messages Serveur → Client

### `match_found`
```json
{ "type": "match_found", "match_id": "uuid", "team_id": 1, "opponent_username": "xX_Sniper_Xx" }
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
      "units": [
        { "unit_id": "Fantassin_1_1", "x": 12.4, "y": 0, "z": -5.2, "ry": 90.0, "health": 100, "dead": false, "shooting": false }
      ]
    },
    {
      "t": 1300,
      "units": [
        { "unit_id": "Fantassin_1_1", "x": 12.4, "y": 0, "z": -5.2, "ry": 90.0, "health": 0, "dead": true, "shooting": false }
      ]
    }
  ]
}
```
La mort et les dégâts sont donc *déduits* des variations de `health`/`dead` entre deux
snapshots côté client (déclenchement de l'animation de mort via `UnitAI.ApplyNetworkDeath()`),
plutôt que transmis comme événements discrets `shot`/`death`.

### `match_over`
```json
{ "type": "match_over", "winner_team": 2 }
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
