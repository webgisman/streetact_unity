using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
        // Nombre de parties Deathmatch/Zone de Contrôle RÉELLEMENT en cours sur ce processus — sert
        // uniquement à la télémétrie (logs), matchInProgress ne reflète plus cette notion depuis
        // l'Option B.
        private int activeMatchCount = 0;

        // "Des milliers de cartes" (2026-08-30) — voir EnsureTileLoadedAndSnapshot :
        // anti-doublon (une tuile neuve visée par deux parties en même temps n'est générée qu'une
        // fois) et anti-abus (limite le rythme de génération de tuile neuve par utilisateur, seul
        // moyen aujourd'hui pour un client de déclencher un travail coûteux — fetch OSM + bake NavMesh
        // synchrone qui gèle tout le processus — côté serveur sans validation, contrairement à la
        // Conquête qui vérifie la propriété en base).
        private readonly Dictionary<string, Coroutine> tileGenerationInFlight = new Dictionary<string, Coroutine>();
        private readonly Dictionary<string, DateTime> lastTileGenerationByUser = new Dictionary<string, DateTime>();
        private const float TileGenerationCooldownSeconds = 30f;

        private void TryStartMatch(List<PlayerConnection> queue)
        {
            if (queue.Count < 2) return;

            // queue[0]/queue[1] peuvent partager le même UserId si un joueur se connecte deux fois
            // avec le même JWT (deux appareils, ou un script) — sans cette vérification, il pouvait
            // s'apparier contre lui-même, contrôler les deux camps et forcer un résultat pour farmer
            // de l'ELO (voir rapport d'audit §1.4). On cherche la première paire d'UserId distincts
            // dans la file plutôt que de toujours prendre les deux premiers en position.
            int i1 = 0, i2 = -1;
            for (int j = 1; j < queue.Count; j++)
            {
                if (queue[j].UserId != queue[i1].UserId) { i2 = j; break; }
            }
            if (i2 < 0) return; // toute la file en attente est le même joueur en double — on patiente

            PlayerConnection p1 = queue[i1];
            PlayerConnection p2 = queue[i2];
            queue.RemoveAt(i2);
            queue.RemoveAt(i1);
            var (cacheKey, owningUserId) = DetermineMatchCacheKey(p1, p2);
            activeMatchCount++;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunMatchGuarded(p1, p2, cacheKey, owningUserId));
        }

        /// <summary>Choisit la tuile Slippy Map de la partie (2026-08-30, "des milliers de cartes") —
        /// celle de p1 (premier arrivé en file) par défaut ; bascule sur celle de p2 UNIQUEMENT si
        /// elle est déjà en cache et pas celle de p1, pour garder les démarrages de partie rapides le
        /// plus souvent possible (voir TacticalGridBuilder.IsTileCached). "Default" si aucun des deux
        /// n'a de tuile domicile connue (jamais eu de position GPS, voir NetMessage.has_home_tile).
        /// owningUserId sert uniquement à l'anti-abus (EnsureTileLoadedAndSnapshot) : le joueur dont
        /// la tuile a été retenue, jamais nul sauf repli sur Default.</summary>
        private static (string cacheKey, string owningUserId) DetermineMatchCacheKey(PlayerConnection p1, PlayerConnection p2)
        {
            string p1Key = p1.HasHomeTile ? $"Z{CityGenerator.ZONE_ZOOM}_{p1.HomeTileX}_{p1.HomeTileY}" : null;
            string p2Key = p2.HasHomeTile ? $"Z{CityGenerator.ZONE_ZOOM}_{p2.HomeTileX}_{p2.HomeTileY}" : null;

            if (p1Key == null && p2Key == null) return ("Default", null);
            if (p1Key == null) return (p2Key, p2.UserId);
            if (p2Key == null) return (p1Key, p1.UserId);

            bool p1Cached = TacticalGridBuilder.IsTileCached(p1Key);
            bool p2Cached = TacticalGridBuilder.IsTileCached(p2Key);
            if (p2Cached && !p1Cached) return (p2Key, p2.UserId);
            return (p1Key, p1.UserId);
        }

        /// <summary>
        /// Enveloppe RunMatch() pour qu'une exception non prévue (message client malformé,
        /// coordonnée invalide, etc.) n'abandonne jamais CETTE partie dans un état incohérent, et
        /// ne laisse jamais matchInProgress bloqué à "true" pour toujours si le crash survient
        /// pendant la section critique brève de RunMatch (voir EnsureTileLoadedAndSnapshot) — ce qui
        /// bloquerait alors le démarrage de TOUTE nouvelle partie sur ce processus, pas seulement
        /// celle-ci. `yield return` n'est pas autorisé dans un bloc try/catch en C#, d'où ce
        /// pompage manuel de l'énumérateur plutôt qu'un try/catch direct autour du corps de RunMatch.
        /// </summary>
        private IEnumerator RunMatchGuarded(PlayerConnection p1, PlayerConnection p2, string cacheKey, string owningUserId)
        {
            IEnumerator inner = RunMatch(p1, p2, cacheKey, owningUserId);
            while (true)
            {
                // "yield break"/"yield return" ne sont pas autorisés à l'intérieur d'un bloc
                // catch (ni d'un try qui a un catch) en C# — on capture juste l'échec ici et on
                // gère l'arrêt du match APRÈS le try/catch, en dehors de ces blocs.
                bool moved = false;
                bool crashed = false;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant un match — abandon en match nul pour ne pas bloquer le serveur : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    AbortMatchSafely(p1);
                    AbortMatchSafely(p2);
                    // Filet de sécurité : si le crash a eu lieu PENDANT la section critique brève de
                    // RunMatch (voir plus bas), matchInProgress resterait sinon bloqué à "true" pour
                    // toujours — sans conséquence s'il n'était pas tenu (juste une réaffectation à
                    // "false", déjà sa valeur).
                    matchInProgress = false;
                    activeMatchCount--;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }

                if (!moved)
                {
                    activeMatchCount--;
                    yield break;
                }
                yield return inner.Current;
            }
        }

        private static void AbortMatchSafely(PlayerConnection p)
        {
            try { if (!p.IsDisconnected) p.Send(new NetMessage { type = "match_over", winner_team = 0 }); } catch { }
            try { p.Close(); } catch { }
        }

        /// <summary>
        /// Point d'entrée Deathmatch/Zone de Contrôle — "Option B" (2026-08-30, voir MatchState.cs) :
        /// à partir d'ici, TOUT l'état de la partie vit dans un MatchState local à cette coroutine,
        /// jamais dans un champ d'instance ni un GameObject partagé. C'est ce qui permet à plusieurs
        /// parties de tourner en parallèle sur ce même processus sans jamais se corrompre entre elles
        /// (voir le bug de corruption de santé de bâtiment trouvé pendant la conception de ce
        /// chantier — impossible ici puisque chaque MatchState a son propre TacticalWorldState).
        /// </summary>
        private IEnumerator RunMatch(PlayerConnection p1, PlayerConnection p2, string cacheKey, string owningUserId)
        {
            var ms = new MatchState
            {
                MatchId = Guid.NewGuid().ToString(),
                P1 = p1,
                P2 = p2,
            };
            p1.TeamId = 1;
            p2.TeamId = 2;
            ms.Mode = string.IsNullOrEmpty(p1.Mode) ? "deathmatch" : p1.Mode;

            yield return FetchUsername(p1);
            yield return FetchUsername(p2);

            // Fige la géométrie de départ de CETTE partie — chemin rapide (tuile déjà en cache,
            // aucun verrou/aucun contact avec la scène) ou lent (tuile jamais vue, génération protégée
            // par matchInProgress contre un combat de Conquête concurrent — voir
            // EnsureTileLoadedAndSnapshot, 2026-08-30 "des milliers de cartes"). Une fois cette étape
            // terminée, `ms` est totalement autonome — le reste du match ne touche plus jamais
            // matchInProgress, ni la scène Unity, ni aucun état partagé avec une autre partie.
            yield return EnsureTileLoadedAndSnapshot(ms, cacheKey, owningUserId);
            TryParseTileCacheKey(ms.CacheKey, out int matchTileX, out int matchTileY);

            if (ms.Mode == "zone_control")
            {
                Vector2 zoneCenter2D = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, Vector2.zero, 40f);
                ms.Zone = new MatchZoneState { Center = new Vector3(zoneCenter2D.x, 0f, zoneCenter2D.y) };
            }

            bool hasRealTile = ms.CacheKey != "Default";
            // ÉQUITÉ GÉOMÉTRIQUE (correctif 2026-09-05) : le JSON Overpass EXACT que CE serveur vient
            // d'utiliser pour figer ms.World est joint à match_found — voir NetMessage.city_data_json
            // et CityGenerator.LoadZoneFromServerData. Jamais pour "Default" (bundle identique des
            // deux côtés, RAS). `matchTileX/matchTileY` viennent d'être résolus juste au-dessus par
            // TryParseTileCacheKey : toujours les bons, que ce match ait pris le chemin rapide
            // (cache déjà chaud) ou lent (tuile jamais vue).
            string cityDataJson = null;
            if (hasRealTile && !CityGenerator.TryReadZoneCacheFromDisk(matchTileX, matchTileY, out cityDataJson))
            {
                Debug.LogWarning($"[MatchSessionManager] JSON de la tuile ({matchTileX},{matchTileY}) introuvable sur disque malgré une géométrie résolue — les clients vont se rabattre sur leur propre génération (risque d'iniquité résiduel, voir CityGenerator.LoadZoneFromServerData).");
            }
            p1.Send(new NetMessage { type = "match_found", match_id = ms.MatchId, team_id = 1, opponent_username = p2.Username, mode = ms.Mode, has_home_tile = hasRealTile, zone_tile_x = matchTileX, zone_tile_y = matchTileY, city_data_json = cityDataJson });
            p2.Send(new NetMessage { type = "match_found", match_id = ms.MatchId, team_id = 2, opponent_username = p1.Username, mode = ms.Mode, has_home_tile = hasRealTile, zone_tile_x = matchTileX, zone_tile_y = matchTileY, city_data_json = cityDataJson });

            yield return CreateMatchRecord(ms.MatchId, p1, p2, ms.Mode);

            // Placement manuel (voir 03-network-protocol.md, "submit_deployment"/"deployment_result") :
            // chaque joueur choisit où poser sa PROPRE escouade dans sa zone de déploiement pendant
            // que l'autre fait de même, en parallèle — pas de tour par tour ici. Un joueur qui ne
            // soumet rien (ou une soumission invalide) de valide avant l'expiration du timer reçoit
            // le repli automatique (AutoDeployTeamFallbackPure) pour SON seul camp.
            yield return RunDeploymentPhasePure(ms);

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunPlanningPhasePure(ms, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhasePure(ms, turnNumber);

                int team1Alive = ms.World.units.Count(u => u.team == 1 && !u.isDead);
                int team2Alive = ms.World.units.Count(u => u.team == 2 && !u.isDead);

                if (team1Alive == 0 || team2Alive == 0)
                {
                    matchOver = true;
                    // Anéantissement mutuel (les deux équipes à 0) = match nul, pas une victoire par défaut.
                    winnerTeam = (team1Alive == 0 && team2Alive == 0) ? 0 : (team1Alive == 0 ? 2 : 1);
                }
                else if (ms.Mode == "zone_control")
                {
                    int zoneWinner = ms.Zone != null ? ms.Zone.GetWinningTeamIfComplete() : 0;
                    if (zoneWinner != 0)
                    {
                        matchOver = true;
                        winnerTeam = zoneWinner;
                    }
                    else if (turnNumber >= ZoneControlTurnCap)
                    {
                        matchOver = true;
                        float p1Progress = ms.Zone != null ? ms.Zone.ProgressTeam1 : 0f;
                        float p2Progress = ms.Zone != null ? ms.Zone.ProgressTeam2 : 0f;
                        winnerTeam = Mathf.Approximately(p1Progress, p2Progress) ? 0 : (p1Progress > p2Progress ? 1 : 2);
                    }
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    // Départage par nombre d'unités vivantes, puis par total de points de vie
                    // restants ; égalité parfaite sur les deux critères = match nul.
                    matchOver = true;
                    int team1Health = ms.World.units.Where(u => u.team == 1 && !u.isDead).Sum(u => u.health);
                    int team2Health = ms.World.units.Where(u => u.team == 2 && !u.isDead).Sum(u => u.health);
                    if (team1Alive != team2Alive) winnerTeam = team1Alive > team2Alive ? 1 : 2;
                    else if (team1Health != team2Health) winnerTeam = team1Health > team2Health ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            yield return UpdateRatings(p1, p2, winnerTeam);

            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p1.NewRating, rating_delta = p1.RatingDelta });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p2.NewRating, rating_delta = p2.RatingDelta });

            yield return CloseMatchRecord(ms.MatchId, winnerTeam);

            p1.Close();
            p2.Close();
            StartCoroutine(ReportInstanceStatus());
        }

        /// <summary>Fige ms.World pour la tuile <paramref name="cacheKey"/> — chemin rapide (déjà en
        /// cache mémoire/disque, aucun contact avec la scène vivante, aucun verrou) ou lent (jamais
        /// vue : génération protégée par matchInProgress + anti-doublon + anti-abus). Voir
        /// TacticalGridBuilder.BuildFromCacheOnly/GenerateAndCacheTile. 2026-08-30, "des milliers de
        /// cartes".</summary>
        private IEnumerator EnsureTileLoadedAndSnapshot(MatchState ms, string cacheKey, string requestingUserId)
        {
            TacticalWorldState cached = TacticalGridBuilder.BuildFromCacheOnly(cacheKey);
            if (cached != null) { ms.World = cached; ms.CacheKey = cacheKey; yield break; }

            if (cacheKey != "Default" && !string.IsNullOrEmpty(requestingUserId) && !CanTriggerTileGeneration(requestingUserId))
            {
                Debug.LogWarning($"[MatchSessionManager] Limite de génération de tuile atteinte pour {requestingUserId} ({cacheKey}) — repli sur la carte par défaut pour cette partie.");
                cacheKey = "Default";
                cached = TacticalGridBuilder.BuildFromCacheOnly(cacheKey);
                if (cached != null) { ms.World = cached; ms.CacheKey = cacheKey; yield break; }
            }

            if (!tileGenerationInFlight.TryGetValue(cacheKey, out Coroutine inFlight))
            {
                inFlight = StartCoroutine(GenerateAndCacheTile(cacheKey));
                tileGenerationInFlight[cacheKey] = inFlight;
            }
            yield return inFlight;
            if (tileGenerationInFlight.TryGetValue(cacheKey, out Coroutine current) && current == inFlight)
            {
                tileGenerationInFlight.Remove(cacheKey);
            }

            ms.World = TacticalGridBuilder.BuildFromCacheOnly(cacheKey);
            ms.CacheKey = cacheKey;
            if (ms.World == null)
            {
                // Échec de génération (Overpass injoignable, ville trop grosse pour le délai
                // d'attente, etc.) — jamais laisser une partie sans géométrie, repli sur Default.
                Debug.LogWarning($"[MatchSessionManager] Génération de la tuile {cacheKey} indisponible — repli sur la carte par défaut.");
                ms.World = TacticalGridBuilder.BuildFromCacheOnly("Default");
                ms.CacheKey = "Default";
                if (ms.World == null)
                {
                    // Ne devrait jamais arriver (SetupWorldOnce garantit "Default" au démarrage) —
                    // mieux vaut une exception explicite ici (rattrapée par RunMatchGuarded, la
                    // partie s'annule proprement) qu'un NullReferenceException plus loin dans
                    // RunExecutionPhasePure.
                    throw new InvalidOperationException("Aucune géométrie disponible, ni pour la tuile demandée ni pour 'Default'.");
                }
            }
        }

        /// <summary>Charge RÉELLEMENT une tuile jamais vue dans la scène serveur (réutilise
        /// LoadZoneOnServer, le mécanisme existant de la Conquête — fetch Overpass + bake NavMesh
        /// synchrone, jusqu'à ~60s pire cas) puis extrait/écrit son cache disque via BuildFromScene()
        /// (portes/fenêtres incluses, voir TacticalGridBuilder). Tient matchInProgress pendant toute
        /// l'opération — compromis accepté avec la Conquête (voir le plan), pas de solution "propre"
        /// sans un processus dédié à la génération (hors scope).</summary>
        private IEnumerator GenerateAndCacheTile(string cacheKey)
        {
            while (matchInProgress) yield return null;
            matchInProgress = true;

            if (cacheKey == "Default")
            {
                if (!defaultMapLoaded) yield return RestoreDefaultMapOnServer();
            }
            else if (TryParseTileCacheKey(cacheKey, out int tileX, out int tileY))
            {
                yield return LoadZoneOnServer(tileX, tileY);
            }

            TacticalGridBuilder.BuildFromScene(); // reconstruction complète (jamais vue) -> écrit L1+L2

            matchInProgress = false;
        }

        /// <summary>"Z{zoom}_{tileX}_{tileY}" -> (tileX, tileY) — false pour "Default" ou toute autre
        /// valeur malformée (l'appelant doit alors traiter comme "Default").</summary>
        private static bool TryParseTileCacheKey(string cacheKey, out int tileX, out int tileY)
        {
            tileX = 0; tileY = 0;
            if (string.IsNullOrEmpty(cacheKey) || cacheKey == "Default") return false;
            string[] parts = cacheKey.Split('_');
            return parts.Length == 3 && int.TryParse(parts[1], out tileX) && int.TryParse(parts[2], out tileY);
        }

        /// <summary>Anti-abus (voir EnsureTileLoadedAndSnapshot) — vrai au plus une fois toutes les
        /// TileGenerationCooldownSeconds par utilisateur.</summary>
        private bool CanTriggerTileGeneration(string userId)
        {
            if (lastTileGenerationByUser.TryGetValue(userId, out DateTime last)
                && (DateTime.UtcNow - last).TotalSeconds < TileGenerationCooldownSeconds)
            {
                return false;
            }
            lastTileGenerationByUser[userId] = DateTime.UtcNow;
            return true;
        }
    }
}
