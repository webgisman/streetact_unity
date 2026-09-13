using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Point d'entrée Deathmatch/Zone de Contrôle "vivant" — bascule demandée par l'utilisateur le
    /// 2026-09-13, voir Assets/_ServerDocs/multiplayer/09-real-unity-combat-investigation-2026-09-13.md
    /// pour le contexte complet (le calcul sous-jacent, TacticalResolver.Resolve(), reste identique à
    /// la famille "Pure" — ce qui change ici, c'est que Deathmatch/Zone de Contrôle utilisent
    /// maintenant de vraies UnitAI/BuildingStructure de scène, exactement comme la Conquête, au lieu
    /// de données pures isolées par match). Remplace RunMatch (MatchSessionManager_Matchmaking.cs,
    /// laissé intact mais plus appelé) comme cible de TryStartMatch.
    ///
    /// CONSÉQUENCE ASSUMÉE (demandée explicitement) : un seul match (Deathmatch, Zone de Contrôle OU
    /// Conquête) peut tourner à la fois sur ce processus — la scène/le NavMesh sont de nouveau
    /// partagés. C'est exactement la contrainte que l'Option B (2026-08-30) avait supprimée pour ces
    /// deux modes ; elle revient ici en échange de vraies UnitAI/BuildingStructure de scène. Voir
    /// TryStartMatchLive ci-dessous pour comment matchInProgress sérialise désormais TOUS les modes
    /// entre eux (Conquête comprise), pas seulement Deathmatch/Zone de Contrôle entre eux.
    /// </summary>
    public partial class MatchSessionManager
    {
        /// <summary>Remplace l'ancien TryStartMatch (toujours utilisé tel quel pour appeler cette
        /// méthode, voir MatchSessionManager_Matchmaking.cs) : ne démarre un nouveau match Deathmatch/
        /// Zone de Contrôle QUE si aucun autre match (Deathmatch/Zone de Contrôle/Conquête) ne tourne
        /// déjà sur ce processus — matchInProgress est maintenant tenu pour TOUTE la durée d'un match
        /// Live, pas juste l'instant bref de la capture de géométrie comme du temps de la famille
        /// Pure. Les joueurs déjà en file d'attente patientent simplement un tick de plus, sans
        /// message d'erreur (contrairement à HandleConquestMessage qui rejette du "server_busy" pour
        /// une demande ponctuelle) : une file d'attente FIFO n'a pas besoin d'être prévenue, elle sera
        /// simplement servie au prochain passage libre.</summary>
        private void TryStartMatchLive(List<PlayerConnection> queue)
        {
            if (matchInProgress) return;
            if (queue.Count < 2) return;

            int i1 = 0, i2 = -1;
            for (int j = 1; j < queue.Count; j++)
            {
                if (queue[j].UserId != queue[i1].UserId) { i2 = j; break; }
            }
            if (i2 < 0) return;

            PlayerConnection p1 = queue[i1];
            PlayerConnection p2 = queue[i2];
            queue.RemoveAt(i2);
            queue.RemoveAt(i1);
            var (cacheKey, _) = DetermineMatchCacheKey(p1, p2);

            matchInProgress = true;
            activeMatchCount++;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunMatchLiveGuarded(p1, p2, cacheKey));
        }

        /// <summary>Même principe que RunMatchGuarded (famille Pure) : une exception non prévue ne
        /// doit ni planter le processus ni laisser matchInProgress bloqué à "true" pour toujours
        /// (ce qui empêcherait tout futur match, Deathmatch/Zone de Contrôle ET Conquête, de jamais
        /// démarrer sur ce processus).</summary>
        private IEnumerator RunMatchLiveGuarded(PlayerConnection p1, PlayerConnection p2, string cacheKey)
        {
            IEnumerator inner = RunMatchLive(p1, p2, cacheKey);
            while (true)
            {
                bool moved = false;
                bool crashed = false;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant un match Live — abandon en match nul : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    AbortMatchSafely(p1);
                    AbortMatchSafely(p2);
                    if (CaptureZone.Instance != null) UnityEngine.Object.Destroy(CaptureZone.Instance.gameObject);
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

        /// <summary>Équivalent "vivant" de RunMatch (MatchSessionManager_Matchmaking.cs) — même
        /// protocole réseau côté client (match_found/deployment_result/turn_timer/turn_result/
        /// match_over, voir 03-network-protocol.md, RIEN ne change pour le client), mais orchestre de
        /// vraies UnitAI/BuildingStructure au lieu de MatchState.World.</summary>
        private IEnumerator RunMatchLive(PlayerConnection p1, PlayerConnection p2, string cacheKey)
        {
            string matchId = Guid.NewGuid().ToString();
            p1.TeamId = 1;
            p2.TeamId = 2;
            string mode = string.IsNullOrEmpty(p1.Mode) ? "deathmatch" : p1.Mode;
            currentMatchMode = mode;

            yield return FetchUsername(p1);
            yield return FetchUsername(p2);

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MatchSessionManager] UnitSpawnerUI.Instance introuvable — la scène serveur est-elle correctement chargée ?");
                AbortMatchSafely(p1);
                AbortMatchSafely(p2);
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            // Charge RÉELLEMENT la carte dans la scène serveur (contrairement à la famille Pure, qui
            // ne touche qu'un cache de géométrie hors-scène) — chemin GPS réel ou carte par défaut,
            // même logique que GenerateAndCacheTile (voir MatchSessionManager_Matchmaking.cs).
            bool hasRealTile = TryParseTileCacheKey(cacheKey, out int tileX, out int tileY);
            if (hasRealTile)
            {
                yield return LoadZoneOnServer(tileX, tileY);
            }
            else if (!defaultMapLoaded)
            {
                yield return RestoreDefaultMapOnServer();
            }

            // Zone de Contrôle : CaptureZone.Instance n'est JAMAIS instancié ailleurs dans ce projet
            // (CreateAtMapCenter() n'avait jusqu'ici aucun appelant — vérifié par recherche exhaustive
            // avant d'écrire ce fichier) — sans cette création explicite, RunExecutionPhase aurait
            // silencieusement ignoré toute logique de capture de zone (son garde
            // "CaptureZone.Instance != null" restant toujours faux), rendant le mode "zone_control"
            // injouable via ce chemin Live (seule la victoire par élimination/plafond de tours aurait
            // jamais pu se déclencher).
            if (mode == "zone_control")
            {
                if (CaptureZone.Instance != null) UnityEngine.Object.Destroy(CaptureZone.Instance.gameObject);
                CaptureZone.CreateAtMapCenter();
            }

            TacticalWorldState liveWorldSnapshot = TacticalGridBuilder.BuildFromScene();
            int authoritativeCityHash = TacticalGridBuilder.ComputeBuildingListHash(liveWorldSnapshot.buildings);
            Debug.Log($"[CityVerify] [{matchId}] (Live) Hash de référence figé : {authoritativeCityHash} ({liveWorldSnapshot.buildings.Count} bâtiments, tuile {cacheKey}).");

            string cityDataJson = null;
            if (hasRealTile && !CityGenerator.TryReadZoneCacheFromDisk(tileX, tileY, out cityDataJson))
            {
                Debug.LogWarning($"[MatchSessionManager] (Live) JSON de la tuile ({tileX},{tileY}) introuvable sur disque malgré une géométrie résolue — les clients vont se rabattre sur leur propre génération (risque d'iniquité résiduel).");
            }

            p1.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = p2.Username, mode = mode, has_home_tile = hasRealTile, zone_tile_x = tileX, zone_tile_y = tileY, city_data_json = cityDataJson });
            p2.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 2, opponent_username = p1.Username, mode = mode, has_home_tile = hasRealTile, zone_tile_x = tileX, zone_tile_y = tileY, city_data_json = cityDataJson });

            yield return CreateMatchRecord(matchId, p1, p2, mode);

            yield return RunDeploymentPhaseLive(matchId, p1, p2);

            // CRITIQUE (2026-09-13, moteur réel) : ResolveDeployment/UnitSpawnerUI.SpawnUnitAt ne
            // marque isPlayerControlled=true que pour l'équipe 1 côté serveur (repli hérité de la
            // Conquête, où l'équipe 2 est TOUJOURS une garnison IA — voir UnitSpawnerUI.SpawnUnitAt,
            // branche UNITY_SERVER). Ici les DEUX équipes sont de vrais joueurs : sans cette ligne,
            // TacticalAIPlanner n'aurait jamais pris le relais pour l'équipe 2 (isPlayerControlled y
            // reste à sa valeur de spawn), mais ExecuterOrdres() sur une unité isPlayerControlled=
            // false peut emprunter des branches pensées pour un ennemi IA (voir UnitAI_Movement) —
            // jamais exercées ni voulues ici. Même correctif que RunConquestSkirmish fait pour son
            // équipe 1 (ligne 236 de MatchSessionManager_Conquest.cs), étendu aux deux équipes.
            foreach (var unit in UnitAI.AllLivingUnits) unit.isPlayerControlled = true;

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunPlanningPhaseLiveNoAI(p1, p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                // 2026-09-13 : RunExecutionPhaseRealEngine (vrai moteur, MatchSessionManager_
                // CombatRealEngine.cs) remplace RunExecutionPhase (TacticalResolver.Resolve(), laissée
                // intacte mais plus appelée par ce chemin) — demande explicite de l'utilisateur.
                yield return RunExecutionPhaseRealEngine(turnNumber, p1, p2);

                int team1Alive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1 && !u.isDead);
                int team2Alive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2 && !u.isDead);

                if (team1Alive == 0 || team2Alive == 0)
                {
                    matchOver = true;
                    winnerTeam = (team1Alive == 0 && team2Alive == 0) ? 0 : (team1Alive == 0 ? 2 : 1);
                }
                else if (mode == "zone_control")
                {
                    int zoneWinner = CaptureZone.Instance != null ? CaptureZone.Instance.GetWinningTeamIfComplete() : 0;
                    if (zoneWinner != 0)
                    {
                        matchOver = true;
                        winnerTeam = zoneWinner;
                    }
                    else if (turnNumber >= ZoneControlTurnCap)
                    {
                        matchOver = true;
                        float p1Progress = CaptureZone.Instance != null ? CaptureZone.Instance.ProgressTeam1 : 0f;
                        float p2Progress = CaptureZone.Instance != null ? CaptureZone.Instance.ProgressTeam2 : 0f;
                        winnerTeam = Mathf.Approximately(p1Progress, p2Progress) ? 0 : (p1Progress > p2Progress ? 1 : 2);
                    }
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    matchOver = true;
                    int team1Health = UnitAI.AllLivingUnits.Where(u => u.teamID == 1 && !u.isDead).Sum(u => u.health);
                    int team2Health = UnitAI.AllLivingUnits.Where(u => u.teamID == 2 && !u.isDead).Sum(u => u.health);
                    if (team1Alive != team2Alive) winnerTeam = team1Alive > team2Alive ? 1 : 2;
                    else if (team1Health != team2Health) winnerTeam = team1Health > team2Health ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            yield return UpdateRatings(p1, p2, winnerTeam);

            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p1.NewRating, rating_delta = p1.RatingDelta });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p2.NewRating, rating_delta = p2.RatingDelta });

            yield return CloseMatchRecord(matchId, winnerTeam);

            // Libère la scène pour le prochain match (Live ou Conquête) — indispensable maintenant
            // qu'un seul match à la fois occupe la scène partagée.
            if (CaptureZone.Instance != null) UnityEngine.Object.Destroy(CaptureZone.Instance.gameObject);
            UnitSpawnerUI.Instance.ClearAllUnits();

            p1.Close();
            p2.Close();
            matchInProgress = false;
            StartCoroutine(ReportInstanceStatus());
        }

        /// <summary>Équivalent de RunPlanningPhase (CombatLive.cs), mais appelle ApplyForPlayerLiveNoAI
        /// au lieu de ApplyForPlayer à la fin — voir ce dernier pour le pourquoi (pas de reprise IA
        /// pour un joueur absent en Deathmatch/Zone de Contrôle, demande explicite déjà satisfaite par
        /// la famille Pure via ApplyForPlayerPure). Corps de boucle IDENTIQUE à RunPlanningPhase par
        /// ailleurs — dupliqué plutôt que paramétré pour ne jamais risquer de changer le comportement
        /// de la Conquête, même pattern que Pure/Live pour RunExecutionPhase(Pure).</summary>
        private IEnumerator RunPlanningPhaseLiveNoAI(PlayerConnection p1, PlayerConnection p2, int turnNumber)
        {
            p1.HasSubmittedThisTurn = false;
            p2.HasSubmittedThisTurn = false;

            float remaining = PlanningSeconds;
            int lastTick = -1;

            while (remaining > 0f && !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn))
            {
                DrainMessages(p1, turnNumber);
                DrainMessages(p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    var tick = new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft };
                    if (!p1.IsDisconnected) p1.Send(tick);
                    if (!p2.IsDisconnected) p2.Send(tick);
                }

                remaining -= Time.deltaTime;
                yield return null;
            }

            ApplyForPlayerLiveNoAI(p1, p2);
            ApplyForPlayerLiveNoAI(p2, p1);
        }

        /// <summary>Équivalent de ApplyForPlayer (CombatLive.cs), mais SANS reprise IA
        /// (TacticalAIPlanner.PlanifierTourIA) pour un joueur absent/en retard. Demande explicite de
        /// l'utilisateur ("je ne veux pas d'IA dans le jeu multijoueur", déjà satisfaite pour la
        /// famille Pure via ApplyForPlayerPure — voir project_novgov_scaling_2026-08-30 en mémoire de
        /// session). Réutiliser ApplyForPlayer tel quel ici aurait réintroduit CETTE régression déjà
        /// corrigée : un joueur ghosté voit simplement ses unités ne recevoir AUCUN ordre ce tour-ci
        /// (chemin tactique vidé, jamais régénéré par TacticalAIPlanner) — TacticalResolver.Resolve()
        /// (appelé par RunExecutionPhase) évalue quand même les tirs pour TOUTE unité vivante qu'elle
        /// ait un ordre actif ou non, donc ces unités se défendent normalement, elles n'avancent/ne
        /// flanquent juste pas activement comme le ferait une vraie IA.</summary>
        private void ApplyForPlayerLiveNoAI(PlayerConnection conn, PlayerConnection opponent)
        {
            var myUnits = UnitAI.AllLivingUnits.Where(u => u.teamID == conn.TeamId).ToList();
            bool shouldGhost = conn.IsDisconnected || !conn.HasSubmittedThisTurn;

            if (shouldGhost)
            {
                foreach (var u in myUnits) u.ClearTacticalPath();

                if (!opponent.IsDisconnected)
                {
                    opponent.Send(new NetMessage
                    {
                        type = "opponent_ghosted",
                        team_id = conn.TeamId,
                        reason = conn.IsDisconnected ? "disconnected" : "timeout"
                    });
                }
            }
            else
            {
                ApplyOrdersToUnits(myUnits, conn.PendingOrders);
            }
        }
    }
}
