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
    /// Moteur de partie au tour par tour entre DEUX vrais joueurs, sur de vraies UnitAI/
    /// BuildingStructure de scène — utilisé par la bataille de siège (MatchSessionManager_
    /// SiegeBattle.cs). Déroulé : chargement de la carte du quartier, "match_found" aux deux joueurs,
    /// déploiement des deux camps, puis à chaque tour planification (les deux joueurs envoient leurs
    /// ordres) -> simulation au vrai moteur Unity (MatchSessionManager_CombatRealEngine.cs) -> rejeu
    /// synchronisé chez les deux clients, jusqu'à l'élimination d'un camp ou le plafond de tours.
    ///
    /// Un seul match à la fois par processus : la scène/le NavMesh sont partagés (matchInProgress).
    /// Pas d'IA : un joueur absent ou en retard voit simplement ses unités ne recevoir aucun ordre ce
    /// tour-là (elles tiennent leur position et se défendent), voir ApplyOrdersOrHold.
    /// </summary>
    public partial class MatchSessionManager
    {
        // Nombre de batailles en cours sur ce processus (diagnostic d'instance).
        private int activeMatchCount = 0;

        // Géométrie de référence de la bataille en cours (figée au chargement du quartier) — sert à
        // répondre à "city_verify" (voir AnswerCityVerify).
        private int authoritativeCityHash;
        private List<TacticalBuilding> authoritativeBuildings = new List<TacticalBuilding>();

        /// <summary>Complément du message "match_over" rempli par <c>onBeforeMatchOver</c> (bataille de
        /// siège : le quartier a-t-il réellement changé de mains ?).</summary>
        private class MatchOverExtras { public bool Success; public string Reason; }

        private static void AbortMatchSafely(PlayerConnection p)
        {
            try { if (!p.IsDisconnected) p.Send(new NetMessage { type = "match_over", winner_team = 0 }); } catch { }
            try { p.Close(); } catch { }
        }

        /// <summary>Joue une bataille complète sur le quartier (tileX, tileY), p1 = équipe 1, p2 =
        /// équipe 2. <paramref name="onBeforeMatchOver"/> est appelé avec (vainqueur, deux départs,
        /// extras) juste avant le classement et "match_over", pour appliquer les conséquences propres
        /// au mode (prise du quartier pour un siège). Libère matchInProgress en fin de partie.</summary>
        private IEnumerator RunMatchLive(PlayerConnection p1, PlayerConnection p2, int tileX, int tileY, string mode,
            Func<int, bool, MatchOverExtras, IEnumerator> onBeforeMatchOver = null)
        {
            string matchId = Guid.NewGuid().ToString();
            p1.TeamId = 1;
            p2.TeamId = 2;

            yield return FetchUsername(p1);
            yield return FetchUsername(p2);

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MatchSessionManager] UnitSpawnerUI.Instance introuvable — la scène serveur est-elle correctement chargée ?");
                AbortMatchSafely(p1);
                AbortMatchSafely(p2);
                matchInProgress = false; // sinon plus aucun match ne pourrait jamais démarrer sur ce processus
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            // Charge RÉELLEMENT la carte du quartier dans la scène serveur — exactement la même tuile
            // que celle chargée par les clients (géométrie autoritaire, voir city_verify).
            yield return LoadZoneOnServer(tileX, tileY);

            TacticalWorldState liveWorldSnapshot = TacticalGridBuilder.BuildFromScene();
            authoritativeBuildings = liveWorldSnapshot.buildings;
            authoritativeCityHash = TacticalGridBuilder.ComputeBuildingListHash(liveWorldSnapshot.buildings);
            Debug.Log($"[CityVerify] [{matchId}] Hash de référence figé : {authoritativeCityHash} ({liveWorldSnapshot.buildings.Count} bâtiments, quartier ({tileX},{tileY})).");

            if (!CityGenerator.TryReadZoneCacheFromDisk(tileX, tileY, out string cityDataJson))
            {
                Debug.LogWarning($"[MatchSessionManager] JSON du quartier ({tileX},{tileY}) introuvable sur disque malgré une géométrie résolue — les clients vont se rabattre sur leur propre génération (risque d'iniquité résiduel).");
            }

            p1.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = p2.Username, mode = mode, zone_tile_x = tileX, zone_tile_y = tileY, city_data_json = cityDataJson });
            p2.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 2, opponent_username = p1.Username, mode = mode, zone_tile_x = tileX, zone_tile_y = tileY, city_data_json = cityDataJson });

            yield return CreateMatchRecord(matchId, p1, p2, mode);

            yield return RunDeploymentPhaseLive(matchId, p1, p2);

            // ResolveDeployment/UnitSpawnerUI.SpawnUnitAt ne marque isPlayerControlled=true que pour
            // l'équipe 1 côté serveur : ici les DEUX équipes sont de vrais joueurs, sans IA.
            foreach (var unit in UnitAI.AllLivingUnits) unit.isPlayerControlled = true;

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunPlanningPhase(p1, p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected)
                {
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhaseRealEngine(turnNumber, p1, p2);

                int team1Alive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1 && !u.isDead);
                int team2Alive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2 && !u.isDead);

                if (team1Alive == 0 || team2Alive == 0)
                {
                    matchOver = true;
                    winnerTeam = (team1Alive == 0 && team2Alive == 0) ? 0 : (team1Alive == 0 ? 2 : 1);
                }
                else if (turnNumber >= BattleTurnCap)
                {
                    // Plafond atteint : plus d'unités vivantes, puis plus de PV totaux, l'emporte.
                    matchOver = true;
                    int team1Health = UnitAI.AllLivingUnits.Where(u => u.teamID == 1 && !u.isDead).Sum(u => u.health);
                    int team2Health = UnitAI.AllLivingUnits.Where(u => u.teamID == 2 && !u.isDead).Sum(u => u.health);
                    if (team1Alive != team2Alive) winnerTeam = team1Alive > team2Alive ? 1 : 2;
                    else if (team1Health != team2Health) winnerTeam = team1Health > team2Health ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            var extras = new MatchOverExtras();
            if (onBeforeMatchOver != null)
                yield return onBeforeMatchOver(winnerTeam, p1.IsDisconnected && p2.IsDisconnected, extras);

            yield return UpdateRatings(p1, p2, winnerTeam);

            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p1.NewRating, rating_delta = p1.RatingDelta, success = extras.Success, reason = extras.Reason, zone_tile_x = tileX, zone_tile_y = tileY });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p2.NewRating, rating_delta = p2.RatingDelta, success = extras.Success, reason = extras.Reason, zone_tile_x = tileX, zone_tile_y = tileY });

            yield return CloseMatchRecord(matchId, winnerTeam);

            // Libère la scène pour la prochaine bataille ou capture.
            UnitSpawnerUI.Instance.ClearAllUnits();

            p1.Close();
            p2.Close();
            matchInProgress = false;
            StartCoroutine(ReportInstanceStatus());
        }

        /// <summary>Réponse à "city_verify" (envoyé par chaque client une fois sa carte générée, avant
        /// d'ouvrir son déploiement) : succès immédiat si sa géométrie concorde avec celle du serveur,
        /// sinon la structure de bâtiments de référence complète pour qu'il se resynchronise
        /// (CityGenerator.ApplyAuthoritativeBuildings). Avant le 2026-10-03 le serveur ne répondait
        /// jamais : le client attendait 15 s pour rien au début de chaque bataille.</summary>
        private void AnswerCityVerify(PlayerConnection conn, int clientHash)
        {
            bool match = clientHash == authoritativeCityHash;
            var reply = new NetMessage { type = "city_verify_result", success = match };
            if (!match)
            {
                Debug.LogWarning($"[CityVerify] Géométrie divergente chez {conn.UserId} (client {clientHash} / serveur {authoritativeCityHash}) — envoi de la structure de référence ({authoritativeBuildings.Count} bâtiments).");
                reply.city_buildings = authoritativeBuildings.Select(b => new BuildingGeometryDto
                {
                    id = b.id,
                    height = b.height,
                    footprint = (b.footprint ?? new List<Vector2>()).Select(p => new Vector2Data { x = p.x, y = p.y }).ToArray(),
                    doors = b.doors.Select(d => new DoorGeometryDto
                    {
                        position = new Vector2Data { x = d.position.x, y = d.position.y },
                        entry_direction = new Vector2Data { x = d.entryDirection.x, y = d.entryDirection.y },
                        width = d.width
                    }).ToArray(),
                    windows = b.windows.Select(w => new WindowGeometryDto
                    {
                        id = w.id,
                        position = new Vector2Data { x = w.position.x, y = w.position.y },
                        outward_normal = new Vector2Data { x = w.outwardNormal.x, y = w.outwardNormal.y },
                        floor_level = w.floorLevel
                    }).ToArray()
                }).ToArray();
            }
            conn.Send(reply);
        }

        /// <summary>Attend les ordres des deux joueurs (ou le délai de PlanningSeconds), en envoyant
        /// le compte à rebours, puis les applique aux unités.</summary>
        private IEnumerator RunPlanningPhase(PlayerConnection p1, PlayerConnection p2, int turnNumber)
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

            ApplyOrdersOrHold(p1, p2);
            ApplyOrdersOrHold(p2, p1);
        }

        /// <summary>Applique les ordres reçus de <paramref name="conn"/> — ou, s'il est absent/en retard,
        /// ne donne AUCUN ordre à ses unités ce tour-ci (pas de reprise par une IA, demande explicite :
        /// "je ne veux pas d'IA dans le jeu multijoueur") : elles tiennent leur position et se
        /// défendent (le vrai moteur évalue les tirs pour toute unité vivante), et l'adversaire est
        /// prévenu ("opponent_ghosted").</summary>
        private void ApplyOrdersOrHold(PlayerConnection conn, PlayerConnection opponent)
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
