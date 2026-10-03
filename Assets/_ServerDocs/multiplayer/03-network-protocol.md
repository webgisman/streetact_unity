# 03 — Protocole réseau client ↔ serveur de jeu (état au 2026-10-03)

TCP, un message = 4 octets (longueur, little-endian) + JSON UTF-8 d'un `NetMessage`
(`Assets/Scripts/Network/NetMessage.cs`, partagé client/serveur). Taille max 8 Mo.
Client : `GameServerClient` + `MultiplayerMatchController`. Serveur : `Assets/Scripts/Server/`.

## 1. Connexion

1. Le client choisit une instance dans `server_instances` (PostgREST) :
   - capture d'un quartier : n'importe quelle instance libre (`status=neq.busy`, la plus récente) ;
   - bataille de siège : **toujours la même pour les deux joueurs** — instances vivantes
     (`updated_at` < 180 s) triées par `id`, indice = `siege_id % nombre`.
2. `{"type":"auth","access_token":<JWT Supabase>}` — vérifié par `JwtValidator` (signature,
   expiration). Refus = fermeture.
3. `{"type":"join_matchmaking","mode":...,"zone_tile_x":x,"zone_tile_y":y,"siege_id":id}`.
   Modes acceptés : `conquest`, `siege_battle`. Tout autre mode (anciens clients) : refus
   `zone_attack_result` / `reason="outdated_client"` puis fermeture.
4. Signal de vie : le serveur envoie `heartbeat` à toute connexion restée silencieuse (le client a
   un délai de lecture de 25 s) ; le client envoie `heartbeat` toutes les 5 s.

## 2. Capture d'un quartier libre (`mode="conquest"`)

Le serveur vérifie la portée (quartier touchant un quartier possédé, sauf pour le tout premier),
puis capture atomiquement (INSERT strict). Réponse, puis fermeture :

| Message | Champs |
|---|---|
| `zone_captured` | `zone_tile_x/y`, `your_new_rating`, `rating_delta` (+8) |
| `zone_attack_result` | `success=false`, `reason` : `not_adjacent`, `already_owned`, `zone_taken` (pris entre-temps), `zone_owned` (appartient à un joueur → siège), `server_busy`, `server_error`, `outdated_client` |

## 3. Bataille de siège (`mode="siege_battle"`)

Le siège est créé avant par le client (RPC `start_siege`, échéance 6 h, notification
`under_attack` au défenseur). Chaque joueur rejoint ensuite la salle d'attente du siège :

- Refus (puis fermeture) : `siege_deploy_ack` avec `success=false` et `reason` = `siege_invalid`
  (siège clos, déjà en bataille, ou joueur étranger au siège) ou `siege_expired`.
- Le premier arrivé attend (heartbeats seulement). Quand l'attaquant ET le défenseur sont là et que
  l'instance est libre : `zone_sieges.status = 'battle'`, puis la partie :

| Sens | Message | Rôle |
|---|---|---|
| S→C | `match_found` | `match_id`, `team_id` (1 = attaquant, 2 = défenseur), `opponent_username`, `mode="siege"`, `zone_tile_x/y`, `city_data_json` (JSON Overpass exact du quartier, pour une géométrie identique) |
| C→S | `city_verify` | `city_building_hash`, `city_building_count` de la ville générée par le client |
| S→C | `city_verify_result` | `success` ; si faux, `city_buildings` = structure de référence complète (resynchronisation) |
| C→S | `deployment_ready` | la carte est chargée, le dock de déploiement est affiché |
| S→C | `turn_timer` | `seconds_remaining` (déploiement, puis planification) |
| C→S | `submit_deployment` | `placements[]` (`unit_type`, `x,y,z`) — plafonds : 6 unités de combat, 2 mortiers, 8 barricades |
| S→C | `deployment_result` | `deployed_units[]` des deux camps (positions finales) ; `reason="roster_trimmed"` si des placements ont été écartés |
| C→S | `submit_turn` | `turn_number`, `orders[]` (`unit_id`, `path[]` de points `x,y,z,action`, max 40 points) |
| S→C | `opponent_ghosted` | `team_id`, `reason` (`disconnected`/`timeout`) : ce camp n'a pas joué ce tour, ses unités tiennent leur position |
| S→C | `turn_result` | `turn_number`, `snapshot_interval_ms`, `snapshots[]` filtrés par le brouillard de guerre de chaque équipe |
| C→S | `turn_result_ack` | `turn_number` — le serveur attend les deux accusés |
| S→C | `turn_playback_start` | `turn_number` — les deux clients rejouent le tour en même temps |
| S→C | `match_over` | `winner_team` (0 = égalité ou interruption), `your_new_rating`, `rating_delta`, `success` (le quartier a changé de mains), `reason` (`zone_lost_race`, `siege_reopened` si les deux joueurs sont partis : le siège repasse en `pending`), `zone_tile_x/y` |

Fin de partie : élimination d'un camp, ou 60 tours (plus d'unités vivantes, puis plus de PV). Les
conséquences (prise du quartier, pillage, bouclier 6 h, rapports `siege_won`/`siege_lost`) sont
appliquées avant `match_over` (`ApplySiegeOutcome`). Plafonds : 5 min de déploiement, 5 min de
planification par tour, 5 min d'attente du chargement de carte.

Sans bataille avant l'échéance, `SiegeResolutionLoop` résout le siège automatiquement (troupes des
casernes des deux joueurs) — aucun message, résultat dans les rapports (`notifications`).

## 4. Hors protocole TCP (PostgREST, côté client)

Profil, caserne (`buy_unit`), quartiers (`upgrade_building`), sièges (`start_siege`, lecture
`zone_sieges`), rapports (`notifications`), bonus quotidien (`claim_daily_bonus`), classement,
registre des instances — voir `SupabaseDatabaseClient.cs` et `schema.sql`.
