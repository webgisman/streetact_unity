using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;
using UnityEngine.Networking;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
        public static bool IsHQDestroyedThisMatch = false;

        private const int ConquestCaptureRatingGain = 8;
        private const int ConquestFailedAttackRatingLoss = 4;

        /// <summary>Petit ajustement de classement pour un combat de conquête (NOUVEAU, 2026-08-30) —
        /// le calcul ELO complet (UpdateRatings) suppose un adversaire humain noté ; ici
        /// l'"adversaire" est une garnison IA sans rating propre, donc un delta fixe plutôt qu'un
        /// calcul ELO entre deux joueurs. Objectif : donner une raison visible (le classement déjà
        /// affiché sur le leaderboard) de jouer ce mode, qui n'avait jusqu'ici AUCUNE récompense
        /// mesurable (voir rapport d'audit §2.B) — capturer une Zone ne faisait qu'écrire une ligne
        /// invisible en base, sans aucun impact sur rien de visible par le joueur.</summary>
        private IEnumerator ApplyConquestRatingDelta(PlayerConnection attacker, bool captured, bool won)
        {
            // "won mais pas captured" = combat gagné, mais un autre attaquant a pris la Zone entre
            // temps (voir le champ reason="zone_lost_race" dans RunConquestSkirmish) : ni récompense
            // ni pénalité, ce n'est pas la faute du joueur.
            int delta = captured ? ConquestCaptureRatingGain : (won ? 0 : -ConquestFailedAttackRatingLoss);
            if (delta == 0)
            {
                attacker.NewRating = 0;
                attacker.RatingDelta = 0;
                yield break;
            }

            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=eq.{attacker.UserId}&select=rating";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            int currentRating = 1000;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<RatingQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0) currentRating = parsed.items[0].rating;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MatchSessionManager] Parsing rating (conquête) échoué : {ex.Message}");
                }
            }

            int newRating = Mathf.Max(0, currentRating + delta);
            attacker.NewRating = newRating;
            attacker.RatingDelta = newRating - currentRating;
            yield return PostgrestPatch($"/profiles?id=eq.{attacker.UserId}", "{\"rating\":" + newRating + "}");
        }

        // =====================================================================
        // Conquête territoriale — Zones de Conquête (grille Slippy Map fixe, Zoom
        // CityGenerator.ZONE_ZOOM). Portée V1 (voir tête de classe) :
        //   - Zone neutre (jamais capturée) -> capture INSTANTANÉE, pas de combat.
        //   - Zone déjà possédée par un autre joueur -> combat contre une garnison IA
        //     (TacticalAIPlanner, même mécanisme que les ennemis du mode Solo), PAS contre le
        //     propriétaire réel en direct : celui-ci n'est pas forcément connecté au moment de
        //     l'attaque, et notifier/faire patienter un joueur en ligne pour qu'il défende en
        //     direct nécessiterait un système de siège asynchrone qui n'existe pas encore
        //     (à ajouter plus tard, voir 08-known-issues-and-todo.md).
        //   - Contrairement à deathmatch/zone_control (qui gardent la carte par défaut fixe,
        //     chargée une fois par SetupWorldOnce), un combat de conquête charge la géométrie
        //     RÉELLE de la Zone attaquée via CityGenerator/MapTileLoader — exactement le même
        //     (tileX, tileY) que celui utilisé côté client, ce qui garantit un NavMesh et des
        //     bâtiments identiques sans jamais transmettre la géométrie sur le réseau.
        // =====================================================================

        private void HandleConquestMessage(PlayerConnection conn, NetMessage msg)
        {
            if (matchInProgress)
            {
                // Un seul combat/emplacement de simulation à la fois sur ce serveur (voir "Portée
                // V1" en tête de classe) — le client peut retenter une nouvelle demande plus tard.
                conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_busy", zone_tile_x = msg.zone_tile_x, zone_tile_y = msg.zone_tile_y });
                conn.Close();
                return;
            }

            matchInProgress = true;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunConquestRequestGuarded(conn, msg.zone_tile_x, msg.zone_tile_y));
        }

        /// <summary>Même principe que RunMatchGuarded : une exception non prévue ne doit jamais
        /// laisser matchInProgress bloqué à "true" pour toujours.</summary>
        private IEnumerator RunConquestRequestGuarded(PlayerConnection conn, int tileX, int tileY)
        {
            IEnumerator inner = RunConquestRequest(conn, tileX, tileY);
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
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant une conquête — abandon : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    try { if (!conn.IsDisconnected) conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_error", zone_tile_x = tileX, zone_tile_y = tileY }); } catch { }
                    try { conn.Close(); } catch { }
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }

                if (!moved)
                {
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }
                yield return inner.Current;
            }
        }

        /// <summary>Vrai si (bx,by) est adjacente à (ax,ay) au sens des 4 directions cardinales
        /// (Nord/Sud/Est/Ouest, jamais diagonale — voir ZoneManager.AttackNorth/South/East/West).</summary>
        private static bool IsAdjacentTile(int ax, int ay, int bx, int by)
        {
            return (ax == bx && Mathf.Abs(ay - by) == 1) || (ay == by && Mathf.Abs(ax - bx) == 1);
        }

        private IEnumerator RunConquestRequest(PlayerConnection attacker, int tileX, int tileY)
        {
            // Le serveur ne faisait jusqu'ici AUCUNE vérification que (tileX,tileY) est réellement
            // adjacente au territoire de l'attaquant — un client modifié pouvait attaquer/capturer
            // n'importe quelle Zone du monde instantanément (voir rapport d'audit §1.2). On exige
            // désormais l'adjacence à une Zone déjà possédée, SAUF pour la toute première Zone d'un
            // joueur qui n'en possède encore aucune (bootstrap : il n'a par définition aucun
            // territoire dont être adjacent).
            List<(int x, int y)> ownedByAttacker = null;
            yield return FetchOwnedZones(attacker.UserId, list => ownedByAttacker = list);

            if (ownedByAttacker.Count > 0 && !ownedByAttacker.Exists(z => IsAdjacentTile(z.x, z.y, tileX, tileY)))
            {
                attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "not_adjacent", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            string ownerId = null;
            yield return FetchZoneOwner(tileX, tileY, id => ownerId = id);

            if (!string.IsNullOrEmpty(ownerId) && ownerId == attacker.UserId)
            {
                attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "already_owned", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            if (string.IsNullOrEmpty(ownerId))
            {
                bool captured = false;
                // expectedPriorOwner=null : INSERT strict (pas d'upsert) — si un autre joueur/une
                // autre instance du pool a capturé cette même Zone neutre entre notre lecture
                // ci-dessus et cette écriture, la contrainte de clé primaire fait échouer l'insertion
                // au lieu d'écraser silencieusement son résultat (voir rapport d'audit §1.1).
                yield return CaptureZoneInDb(tileX, tileY, attacker.UserId, null, ok => captured = ok);
                if (captured)
                {
                    // Capture d'une Zone neutre = victoire de conquête au même titre qu'un combat
                    // gagné contre une garnison : doit rapporter le même delta de classement, sinon
                    // le geste le plus fréquent du mode Conquête (capturer du neutre) reste sans
                    // aucune récompense visible (voir rapport d'audit jouabilité, défaut bloquant #3).
                    yield return ApplyConquestRatingDelta(attacker, captured: true, won: true);
                    attacker.Send(new NetMessage { type = "zone_captured", success = true, zone_tile_x = tileX, zone_tile_y = tileY, your_new_rating = attacker.NewRating, rating_delta = attacker.RatingDelta });
                }
                else
                    attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "zone_taken", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            yield return RunConquestSkirmish(attacker, tileX, tileY, ownerId);
        }

        private IEnumerator RunConquestSkirmish(PlayerConnection attacker, int tileX, int tileY, string defenderOwnerId)
        {
            attacker.TeamId = 1;
            string matchId = Guid.NewGuid().ToString();

            yield return FetchUsername(attacker);

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MatchSessionManager] UnitSpawnerUI.Instance introuvable — la scène serveur est-elle correctement chargée ?");
                attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_error", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            yield return LoadZoneOnServer(tileX, tileY);

            // Voir le même correctif dans RunMatch (deathmatch/zone_control) : la Conquête vise
            // TOUJOURS une vraie tuile (jamais "Default"), le JSON est donc systématiquement attendu.
            string cityDataJson = null;
            if (!CityGenerator.TryReadZoneCacheFromDisk(tileX, tileY, out cityDataJson))
            {
                Debug.LogWarning($"[MatchSessionManager] JSON de la Zone ({tileX},{tileY}) introuvable sur disque après LoadZoneOnServer — le client va se rabattre sur sa propre génération (risque d'iniquité résiduel).");
            }
            attacker.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = "Garnison ennemie", mode = "conquest", zone_tile_x = tileX, zone_tile_y = tileY, city_data_json = cityDataJson });

            yield return CreateMatchRecord(matchId, attacker, null, "conquest");

            yield return RunConquestDeploymentPhase(attacker, defenderOwnerId);

            // Le camp attaquant est piloté par un vrai joueur. La garnison IA (équipe 2) GARDE
            // isPlayerControlled à sa valeur par défaut (false) : TacticalAIPlanner.PlanTurnForUnit()
            // planifie donc pour elle exactement comme pour un ennemi en mode Solo (voir
            // RunConquestPlanningPhase et UnitAI_Movement.PlanifierTourIA).
            foreach (var unit in UnitAI.AllLivingUnits.Where(u => u.teamID == 1)) unit.isPlayerControlled = true;

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;
            ConsecutiveMissedTurns = 0;
            IsHQDestroyedThisMatch = false;

            while (!matchOver)
            {
                yield return RunConquestPlanningPhase(attacker, turnNumber);

                if (IsHQDestroyedThisMatch)
                {
                    Debug.Log("[Conquête] QG DÉTRUIT ! L'attaquant remporte une victoire totale.");
                    matchOver = true;
                    winnerTeam = 1;
                    break;
                }

                if (attacker.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                // Abandon sur inactivité prolongée : sans ça, un joueur connecté mais qui ne joue plus
                // (application en arrière-plan) immobilisait l'unique créneau de combat vivant de
                // l'instance jusqu'au plafond de 60 tours, soit près d'une heure.
                if (ConsecutiveMissedTurns >= MaxConsecutiveMissedTurns)
                {
                    Debug.Log($"[Conquête] Abandon : {ConsecutiveMissedTurns} tours consécutifs sans ordre — la garnison l'emporte et l'instance est libérée.");
                    matchOver = true;
                    winnerTeam = 2; // la garnison tient la Zone
                    break;
                }

                yield return RunExecutionPhase(turnNumber, attacker, null);

                int attackerAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1);
                int garrisonAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2);

                if (IsHQDestroyedThisMatch)
                {
                    Debug.Log("[Conquête] QG DÉTRUIT ! L'attaquant remporte une victoire totale.");
                    matchOver = true;
                    winnerTeam = 1;
                }
                else if (attackerAlive == 0 || garrisonAlive == 0)
                {
                    matchOver = true;
                    winnerTeam = (attackerAlive == 0 && garrisonAlive == 0) ? 0 : (attackerAlive == 0 ? 2 : 1);
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    matchOver = true;
                    int attackerHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).Sum(u => u.health);
                    int garrisonHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 2).Sum(u => u.health);
                    if (attackerAlive != garrisonAlive) winnerTeam = attackerAlive > garrisonAlive ? 1 : 2;
                    else if (attackerHealth != garrisonHealth) winnerTeam = attackerHealth > garrisonHealth ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            bool won = winnerTeam == 1;
            bool captureConfirmed = false;
            if (won)
            {
                // expectedPriorOwner=defenderOwnerId : si la Zone a changé de propriétaire entre le
                // début de ce combat et maintenant (un autre combat contre la même garnison, lancé en
                // parallèle sur une autre instance du pool, a capturé la Zone en premier — voir
                // rapport d'audit §1.1), cette écriture conditionnelle échoue proprement au lieu
                // d'écraser silencieusement le résultat de l'autre combat.
                yield return CaptureZoneInDb(tileX, tileY, attacker.UserId, defenderOwnerId, ok => captureConfirmed = ok);
            }
            bool captured = won && captureConfirmed;

            if (captured && !string.IsNullOrEmpty(defenderOwnerId))
            {
                yield return LootActionPoints(attacker, defenderOwnerId);
            }

            yield return ApplyConquestRatingDelta(attacker, captured, won);

            if (!attacker.IsDisconnected)
            {
                attacker.Send(new NetMessage
                {
                    type = "match_over",
                    winner_team = winnerTeam,
                    zone_tile_x = tileX,
                    zone_tile_y = tileY,
                    success = captured,
                    // "zone_lost_race" : le combat a été GAGNÉ (winnerTeam==1) mais un autre
                    // attaquant a capturé cette Zone entre-temps — distinct d'une simple défaite
                    // contre la garnison, le client doit afficher un message différent.
                    reason = (won && !captureConfirmed) ? "zone_lost_race" : "",
                    your_new_rating = attacker.NewRating,
                    rating_delta = attacker.RatingDelta
                });
            }

            yield return CloseMatchRecord(matchId, winnerTeam);
            attacker.Close();
        }

        private IEnumerator LootActionPoints(PlayerConnection attacker, string defenderOwnerId)
        {
            string urlDefender = $"{GameServerBootstrap.RestUrl}/profiles?username=eq.{defenderOwnerId}&select=action_points";
            int stolenAP = 0;
            using (UnityWebRequest req = UnityWebRequest.Get(urlDefender))
            {
                req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
                req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    string json = req.downloadHandler.text;
                    if (json.Contains("\"action_points\":"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(json, "\"action_points\":\\s*(\\d+)");
                        if (match.Success) int.TryParse(match.Groups[1].Value, out stolenAP);
                    }
                }
            }

            if (stolenAP > 0)
            {
                // Update defender to 0
                string updateDef = "{\"action_points\": 0}";
                using (UnityWebRequest req = UnityWebRequest.Put($"{GameServerBootstrap.RestUrl}/profiles?username=eq.{defenderOwnerId}", updateDef))
                {
                    req.method = "PATCH";
                    req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
                    req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
                    req.SetRequestHeader("Content-Type", "application/json");
                    yield return req.SendWebRequest();
                }

                // Get attacker current AP
                int attackerAP = 0;
                string urlAttacker = $"{GameServerBootstrap.RestUrl}/profiles?username=eq.{attacker.UserId}&select=action_points";
                using (UnityWebRequest reqA = UnityWebRequest.Get(urlAttacker))
                {
                    reqA.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
                    reqA.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
                    yield return reqA.SendWebRequest();
                    if (reqA.result == UnityWebRequest.Result.Success)
                    {
                        string jsonA = reqA.downloadHandler.text;
                        var matchA = System.Text.RegularExpressions.Regex.Match(jsonA, "\"action_points\":\\s*(\\d+)");
                        if (matchA.Success) int.TryParse(matchA.Groups[1].Value, out attackerAP);
                    }
                }

                int newAp = attackerAP + stolenAP;
                string updateAttacker = $"{{\"action_points\": {newAp}}}";
                using (UnityWebRequest req = UnityWebRequest.Put($"{GameServerBootstrap.RestUrl}/profiles?username=eq.{attacker.UserId}", updateAttacker))
                {
                    req.method = "PATCH";
                    req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
                    req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
                    req.SetRequestHeader("Content-Type", "application/json");
                    yield return req.SendWebRequest();
                }

                Debug.Log($"[Conquête] {attacker.UserId} a pillé {stolenAP} AP au joueur {defenderOwnerId} !");
            }
        }

        // Renfort de garnison IA en fonction de la taille du territoire du défenseur (NOUVEAU,
        // 2026-08-30 — voir rapport d'audit §2.B "rich get richer sans mécanique de retour") :
        // jusqu'ici la garnison défendant une Zone était TOUJOURS la même composition fixe, quelle
        // que soit la valeur stratégique de la Zone ou la taille de l'empire du propriétaire — un
        // joueur assidu pouvait donc étendre son territoire indéfiniment sans jamais rencontrer de
        // résistance croissante. Un empire plus grand devient maintenant mécaniquement plus difficile
        // à continuer d'agrandir, sans avoir besoin d'un système d'économie/ressources complet.
        private static int GarrisonExtraInfantryForZoneCount(int zoneCount)
        {
            if (zoneCount >= 10) return 3;
            if (zoneCount >= 6) return 2;
            if (zoneCount >= 3) return 1;
            return 0;
        }

        /// <summary>Comme RunDeploymentPhase, mais un seul vrai joueur (l'attaquant, équipe 1) : la
        /// garnison IA (équipe 2) est auto-déployée immédiatement, sans attendre — elle n'a pas de
        /// timer de joueur à respecter. Sa force est renforcée selon le nombre de Zones déjà
        /// possédées par le défenseur (voir GarrisonExtraInfantryForZoneCount).</summary>
        private IEnumerator RunConquestDeploymentPhase(PlayerConnection attacker, string defenderOwnerId)
        {
            attacker.HasSubmittedDeployment = false;
            attacker.PendingDeployment = null;
            attacker.MapReady = false;

            DateTime conquestDeployStartUtc = DateTime.UtcNow;

            // Même filet que RunDeploymentPhase : LoadZoneOnServer a déjà attendu la génération
            // CÔTÉ SERVEUR, mais le client attaquant doit aussi charger cette même Zone de son côté
            // (LoadConquestZoneThenOpenDeployment) avant de voir son dock — sans attendre son
            // "deployment_ready", le timer de 45s pouvait s'écouler entièrement pendant ce chargement.
            float mapWait = MapReadyMaxWaitSeconds;
            while (mapWait > 0f && !attacker.MapReady)
            {
                DrainMessages(attacker, 0);
                if (attacker.IsDisconnected) yield break;
                mapWait -= Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[Timing] Conquête — MapReady terminé après {(DateTime.UtcNow - conquestDeployStartUtc).TotalSeconds:F1}s réelles.");

            DateTime conquestCountdownStartUtc = DateTime.UtcNow;
            float remaining = DeploymentSeconds;
            // Même compte à rebours de déploiement que dans RunDeploymentPhasePure (voir là-bas).
            int lastDeployTick = -1;
            while (remaining > 0f && !attacker.HasSubmittedDeployment)
            {
                DrainMessages(attacker, 0);
                if (attacker.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastDeployTick)
                {
                    lastDeployTick = secondsLeft;
                    attacker.Send(new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft });
                }

                remaining -= Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[Timing] Conquête — compte à rebours de déploiement terminé après {(DateTime.UtcNow - conquestCountdownStartUtc).TotalSeconds:F1}s réelles (attendu {DeploymentSeconds}s), HasSubmittedDeployment={attacker.HasSubmittedDeployment}.");

            var attackerUnits = ResolveDeployment(attacker, 1);

            int extraGarrisonInfantry = 0;
            if (!string.IsNullOrEmpty(defenderOwnerId))
            {
                // Fetch Defender Roster and Deploy
                string url = $"{GameServerBootstrap.RestUrl}/player_roster?user_id=eq.{defenderOwnerId}";
                using var req = UnityWebRequest.Get(url);
                req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
                req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
                yield return req.SendWebRequest();
                
                Novgov.Auth.PlayerRosterItem[] defenderRoster = null;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try {
                        defenderRoster = Novgov.Auth.JsonHelper.FromJson<Novgov.Auth.PlayerRosterItem>(req.downloadHandler.text);
                    } catch {}
                }

                if (defenderRoster != null && defenderRoster.Length > 0)
                {
                    Debug.Log($"[Conquête] Déploiement automatique du roster pour le défenseur {defenderOwnerId}");
                    UnitSpawnerUI.Instance.AutoDeployRoster(2, defenderRoster);
                }
                else
                {
                    // Fallback
                    List<(int x, int y)> defenderZones = null;
                    yield return FetchOwnedZones(defenderOwnerId, list => defenderZones = list);
                    extraGarrisonInfantry = GarrisonExtraInfantryForZoneCount(defenderZones?.Count ?? 0);
                    UnitSpawnerUI.Instance.AutoDeployTeamFallback(2, extraGarrisonInfantry);
                }
            }
            else
            {
                UnitSpawnerUI.Instance.AutoDeployTeamFallback(2, extraGarrisonInfantry);
            }

            // Brouillard de guerre au déploiement (voir RunDeploymentPhase) : l'attaquant ne voit que
            // SA PROPRE escouade avant le premier tour — la garnison IA n'existera côté client qu'une
            // fois repérée en jeu (voir PlaySnapshotsCoroutine).
            if (!attacker.IsDisconnected)
                attacker.Send(new NetMessage { type = "deployment_result", deployed_units = attackerUnits.ToArray() });
        }

        /// <summary>Comme RunPlanningPhase, mais la garnison IA planifie immédiatement (pas
        /// d'attente : elle n'a pas de timer de joueur), exactement comme un ennemi en mode Solo.</summary>
        private IEnumerator RunConquestPlanningPhase(PlayerConnection attacker, int turnNumber)
        {
            attacker.HasSubmittedThisTurn = false;

            foreach (var u in UnitAI.AllLivingUnits.Where(u => u.teamID == 2 && !u.isDead))
            {
                u.PlanifierTourIA();
            }

            DateTime conquestPlanningStartUtc = DateTime.UtcNow;
            float remaining = PlanningSeconds;
            int lastTick = -1;

            while (remaining > 0f && !attacker.HasSubmittedThisTurn)
            {
                DrainMessages(attacker, turnNumber);
                if (attacker.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    attacker.Send(new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft });
                }

                remaining -= Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[Timing] Conquête tour {turnNumber} — RunConquestPlanningPhase terminé après {(DateTime.UtcNow - conquestPlanningStartUtc).TotalSeconds:F1}s réelles (attendu max {PlanningSeconds}s), HasSubmittedThisTurn={attacker.HasSubmittedThisTurn}.");

            var attackerUnitsThisTurn = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).ToList();

            if (attacker.HasSubmittedThisTurn)
            {
                ConsecutiveMissedTurns = 0;
                foreach (var u in attackerUnitsThisTurn) u.isGhosted = false;
                ApplyOrdersToUnits(attackerUnitsThisTurn, attacker.PendingOrders);
            }
            else
            {
                // Substitut IA comme dans ApplyForPlayer (partie à deux joueurs) : la Conquête et
                // l'Entraînement s'en passaient totalement et se contentaient d'effacer les ordres, si
                // bien qu'un joueur inactif regardait ses unités ne rien faire pendant des dizaines de
                // tours. Vu en production : un combat a enchaîné 21 tours de 60s (21 minutes) avec
                // "HasSubmittedThisTurn=False" à chaque tour et zéro ordre côté attaquant — tout en
                // monopolisant l'unique créneau de combat "vivant" de l'instance (matchInProgress), donc
                // en refusant l'accès à tous les autres joueurs pendant ce temps.
                ConsecutiveMissedTurns++;
                foreach (var u in attackerUnitsThisTurn)
                {
                    u.isGhosted = true;
                    u.PlanifierTourIA();
                }
                Debug.Log($"[Conquête] Tour sans ordre ({ConsecutiveMissedTurns}/{MaxConsecutiveMissedTurns}) — unités de l'attaquant pilotées par l'IA de remplacement.");
            }
        }

        // Au-delà de ce nombre de tours consécutifs sans le moindre ordre, le combat est abandonné.
        // Borne la durée qu'un joueur inactif peut imposer : sans elle, le plafond de 60 tours à 60s
        // laissait une instance bloquée jusqu'à une heure sur une partie que plus personne ne jouait.
        private const int MaxConsecutiveMissedTurns = 3;
        private int ConsecutiveMissedTurns = 0;

        // =====================================================================
        // Entraînement contre l'IA (2026-09-02) — permet à un joueur SEUL en file d'attente
        // Deathmatch/Zone de Contrôle de jouer une partie immédiate contre une garnison IA sur la
        // carte par défaut, en attendant qu'un vrai adversaire se présente, plutôt que de patienter
        // les bras croisés. Réutilise TEL QUEL le moteur "vivant" 1-joueur-contre-garnison déjà
        // éprouvé par la Conquête (RunConquestDeploymentPhase/RunConquestPlanningPhase/
        // RunExecutionPhase, TacticalAIPlanner) — jamais le moteur "Option B" en donnée pure
        // (RunMatch), qui ne sait pas piloter d'IA. Contrainte héritée de la Conquête, inchangée :
        // un seul combat "vivant" (Conquête OU Entraînement) à la fois par instance de serveur (voir
        // matchInProgress) — un joueur qui déclenche l'entraînement pendant qu'un combat vivant
        // tourne déjà reçoit un refus immédiat plutôt que d'attendre indéfiniment ; le client peut
        // simplement retenter. Partie hors-score, sans effet sur le classement ni sur la table
        // "zones" : uniquement pour s'entraîner/patienter.
        // =====================================================================

        private void HandlePracticeAiMessage(PlayerConnection conn)
        {
            if (matchInProgress)
            {
                conn.Send(new NetMessage { type = "match_over", winner_team = 0, reason = "server_busy" });
                conn.Close();
                return;
            }

            matchInProgress = true;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunPracticeVsAIGuarded(conn));
        }

        /// <summary>Même principe que RunMatchGuarded/RunConquestRequestGuarded : une exception non
        /// prévue ne doit jamais laisser matchInProgress bloqué à "true" pour toujours.</summary>
        private IEnumerator RunPracticeVsAIGuarded(PlayerConnection player)
        {
            IEnumerator inner = RunPracticeVsAI(player);
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
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant un entraînement IA — abandon : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    try { if (!player.IsDisconnected) player.Send(new NetMessage { type = "match_over", winner_team = 0, reason = "server_error" }); } catch { }
                    try { player.Close(); } catch { }
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }

                if (!moved)
                {
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }
                yield return inner.Current;
            }
        }

        private IEnumerator RunPracticeVsAI(PlayerConnection player)
        {
            player.TeamId = 1;
            string matchId = Guid.NewGuid().ToString();

            yield return FetchUsername(player);

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MatchSessionManager] UnitSpawnerUI.Instance introuvable — la scène serveur est-elle correctement chargée ?");
                player.Send(new NetMessage { type = "match_over", winner_team = 0, reason = "server_error" });
                player.Close();
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            // Toujours la carte par défaut (jamais une vraie Zone/tuile GPS) — un entraînement n'a
            // pas de territoire réel à charger, contrairement à la Conquête (LoadZoneOnServer).
            yield return RestoreDefaultMapOnServer();

            player.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = "IA (Entraînement)", mode = "practice_ai" });

            yield return CreateMatchRecord(matchId, player, null, "practice_ai");

            // defenderOwnerId=null : pas de renfort de garnison lié à un territoire (voir
            // GarrisonExtraInfantryForZoneCount) — l'entraînement n'a pas de notion de "propriétaire
            // de Zone", la garnison reste toujours à sa force de base.
            yield return RunConquestDeploymentPhase(player, null);

            foreach (var unit in UnitAI.AllLivingUnits.Where(u => u.teamID == 1)) unit.isPlayerControlled = true;

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            ConsecutiveMissedTurns = 0;

            while (!matchOver)
            {
                yield return RunConquestPlanningPhase(player, turnNumber);

                if (player.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                // Même filet que la Conquête : l'entraînement occupe le même créneau unique de combat
                // vivant par instance (matchInProgress), donc un joueur parti sans se déconnecter le
                // rendrait indisponible pour tout le monde pendant près d'une heure.
                if (ConsecutiveMissedTurns >= MaxConsecutiveMissedTurns)
                {
                    Debug.Log($"[Entraînement] Abandon : {ConsecutiveMissedTurns} tours consécutifs sans ordre — instance libérée.");
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhase(turnNumber, player, null);

                int playerAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1);
                int garrisonAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2);

                if (playerAlive == 0 || garrisonAlive == 0)
                {
                    matchOver = true;
                    winnerTeam = (playerAlive == 0 && garrisonAlive == 0) ? 0 : (playerAlive == 0 ? 2 : 1);
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    matchOver = true;
                    int playerHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).Sum(u => u.health);
                    int garrisonHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 2).Sum(u => u.health);
                    if (playerAlive != garrisonAlive) winnerTeam = playerAlive > garrisonAlive ? 1 : 2;
                    else if (playerHealth != garrisonHealth) winnerTeam = playerHealth > garrisonHealth ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            if (!player.IsDisconnected)
            {
                player.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, reason = "practice" });
            }

            yield return CloseMatchRecord(matchId, winnerTeam);
            player.Close();
        }

        /// <summary>Charge la géométrie réelle (bâtiments Overpass + sol OSM + NavMesh) d'une Zone
        /// de Conquête sur le serveur, en attendant qu'elle soit ENTIÈREMENT prête (voir
        /// CityGenerator.IsCityReady) avant de continuer — sans ça, le déploiement pourrait démarrer
        /// sur un NavMesh pas encore baké.</summary>
        private IEnumerator LoadZoneOnServer(int tileX, int tileY)
        {
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            if (cityGen == null)
            {
                Debug.LogError("[MatchSessionManager] CityGenerator introuvable — impossible de charger la Zone de Conquête.");
                yield break;
            }

            defaultMapLoaded = false;
            cityGen.zoneTileX = tileX;
            cityGen.zoneTileY = tileY;
            cityGen.GenerateCity();
            if (mapLoader != null) mapLoader.LoadMap();
            TacticalGridBuilder.InvalidateCache(); // nouvelle Zone = bâtiments totalement différents

            float maxWait = 60f; // Filet de sécurité : Overpass peut être lent, mais jamais infini.
            while (!cityGen.IsCityReady && maxWait > 0f)
            {
                maxWait -= Time.deltaTime;
                yield return null;
            }

            if (!cityGen.IsCityReady)
            {
                Debug.LogWarning($"[MatchSessionManager] Chargement de la Zone ({tileX},{tileY}) trop long (>60s) — poursuite avec l'état actuel.");
            }
        }

        /// <summary>Recharge la carte par défaut hors-ligne (deathmatch/zone_control) après qu'un
        /// combat de conquête a temporairement remplacé la géométrie de la scène — voir
        /// defaultMapLoaded. Mêmes appels que SetupWorldOnce(), avec une attente de complétion en plus.</summary>
        private IEnumerator RestoreDefaultMapOnServer()
        {
            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
            if (cityGen != null) cityGen.LoadDefaultOfflineCity();
            TacticalGridBuilder.InvalidateCache(); // retour à la carte par défaut = bâtiments différents de la Zone quittée

            float maxWait = 30f;
            while (cityGen != null && !cityGen.IsCityReady && maxWait > 0f)
            {
                maxWait -= Time.deltaTime;
                yield return null;
            }

            defaultMapLoaded = true;
        }

        // =====================================================================
        // Persistance des Zones (table "zones", schema.sql) via PostgREST.
        // =====================================================================

        private IEnumerator FetchZoneOwner(int tileX, int tileY, Action<string> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/zones?tile_x=eq.{tileX}&tile_y=eq.{tileY}&zoom=eq.{CityGenerator.ZONE_ZOOM}&select=owner_user_id";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MatchSessionManager] Lecture de la Zone ({tileX},{tileY}) échouée : {req.error}");
                onResult(null);
                yield break;
            }

            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<ZoneQueryResult>(wrapped);
                string owner = (parsed?.items != null && parsed.items.Length > 0) ? parsed.items[0].owner_user_id : null;
                onResult(string.IsNullOrEmpty(owner) ? null : owner);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchSessionManager] Parsing Zone échoué : {ex.Message}");
                onResult(null);
            }
        }

        /// <summary>Capture ATOMIQUE d'une Zone — corrige la course entre deux instances du pool qui
        /// combattraient pour la même Zone en même temps (voir rapport d'audit §1.1) : l'ancien
        /// upsert "merge-duplicates" écrasait silencieusement le résultat de l'autre combat, quel que
        /// soit l'état réel de la Zone au moment de l'écriture. <paramref name="expectedPriorOwner"/>
        /// doit être EXACTEMENT ce qui a été lu par FetchZoneOwner avant de lancer le combat/la
        /// capture : null pour une Zone neutre (-> INSERT strict, échoue si quelqu'un a capturé entre
        /// temps grâce à la contrainte de clé primaire), l'ID du propriétaire vu à ce moment-là pour
        /// une Zone contestée (-> PATCH conditionnel sur ce même owner_user_id, n'affecte aucune ligne
        /// si la Zone a changé de mains depuis). <paramref name="onResult"/> reçoit true seulement si
        /// CETTE écriture a réellement pris effet.</summary>
        private IEnumerator CaptureZoneInDb(int tileX, int tileY, string newOwnerUserId, string expectedPriorOwner, Action<bool> onResult)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            if (expectedPriorOwner == null)
            {
                string json = "{\"tile_x\":" + tileX + ",\"tile_y\":" + tileY + ",\"zoom\":" + CityGenerator.ZONE_ZOOM +
                              ",\"owner_user_id\":\"" + newOwnerUserId + "\",\"captured_at\":\"" + nowIso + "\"}";
                yield return PostgrestPostChecked("/zones", json, onResult);
            }
            else
            {
                string path = $"/zones?tile_x=eq.{tileX}&tile_y=eq.{tileY}&zoom=eq.{CityGenerator.ZONE_ZOOM}&owner_user_id=eq.{expectedPriorOwner}";
                string json = "{\"owner_user_id\":\"" + newOwnerUserId + "\",\"captured_at\":\"" + nowIso + "\"}";
                yield return PostgrestPatchChecked(path, json, onResult);
            }
        }

        /// <summary>INSERT strict (jamais d'upsert) — un conflit de clé primaire (409, quelqu'un a
        /// inséré la même ligne entre temps) est une issue ATTENDUE ici, pas une vraie erreur : voir
        /// CaptureZoneInDb. onResult ne reçoit true que sur un succès HTTP franc.</summary>
        private IEnumerator PostgrestPostChecked(string path, string jsonBody, Action<bool> onResult)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Capture de Zone refusée (déjà prise entre-temps ?) ({path}) : {req.error} — {req.downloadHandler.text}");
            onResult(req.result == UnityWebRequest.Result.Success);
        }

        /// <summary>PATCH conditionné par le filtre de la requête (owner_user_id=eq.&lt;attendu&gt;
        /// dans <paramref name="path"/>) — "Prefer: return=representation" permet de distinguer "0
        /// ligne affectée" (corps "[]", la Zone a changé de propriétaire depuis notre lecture) d'une
        /// vraie mise à jour, ce qu'un simple code HTTP 200/204 ne permettrait pas ici : PostgREST
        /// répond 200 avec un tableau vide dans les deux cas (échec conditionnel silencieux ou succès)
        /// sans ce header. Voir CaptureZoneInDb.</summary>
        private IEnumerator PostgrestPatchChecked(string path, string jsonBody, Action<bool> onResult)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "PATCH");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=representation");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MatchSessionManager] Mise à jour conditionnelle de Zone échouée ({path}) : {req.error} — {req.downloadHandler.text}");
                onResult(false);
                yield break;
            }
            string body = req.downloadHandler.text?.Trim();
            onResult(!string.IsNullOrEmpty(body) && body != "[]");
        }

        /// <summary>Toutes les Zones actuellement possédées par <paramref name="userId"/> — sert à la
        /// fois à valider l'adjacence d'une nouvelle attaque (RunConquestRequest) et à renforcer la
        /// garnison d'un défenseur selon la taille de son territoire (RunConquestDeploymentPhase). En
        /// cas d'échec réseau, retourne une liste VIDE (pas null) : voir les appelants, qui traitent
        /// alors le joueur comme n'ayant aucune Zone (comportement "fail-open" déjà utilisé ailleurs
        /// dans cette classe, ex. FetchZoneOwner traite un échec comme "Zone neutre").</summary>
        private IEnumerator FetchOwnedZones(string userId, Action<List<(int x, int y)>> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/zones?owner_user_id=eq.{userId}&select=tile_x,tile_y";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            var result = new List<(int x, int y)>();
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<ZoneCoordQueryResult>(wrapped);
                    if (parsed?.items != null)
                        foreach (var e in parsed.items) result.Add((e.tile_x, e.tile_y));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MatchSessionManager] Parsing des Zones possédées échoué : {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"[MatchSessionManager] Lecture des Zones possédées échouée : {req.error}");
            }
            onResult(result);
        }

        [Serializable] private class ZoneEntry { public string owner_user_id; }
        [Serializable] private class ZoneQueryResult { public ZoneEntry[] items; }
        [Serializable] private class ZoneCoordEntry { public int tile_x; public int tile_y; }
        [Serializable] private class ZoneCoordQueryResult { public ZoneCoordEntry[] items; }
    }
}
