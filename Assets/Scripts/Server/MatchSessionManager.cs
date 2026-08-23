using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Novgov.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace Novgov.Server
{
    /// <summary>
    /// Orchestre UN match à la fois (voir README.md, "Portée V1"). Réutilise tel quel le moteur de
    /// simulation existant (UnitAI / TacticalAIPlanner / NavMesh) — voir 04-unity-headless-server.md.
    ///
    /// Limitation connue V1 : pas de reprise de partie après coupure TCP complète — un joueur qui se
    /// déconnecte puis se reconnecte rejoint la file d'attente pour un NOUVEAU match, il ne réintègre
    /// pas la partie en cours (qui continue avec son camp en mode Ghost jusqu'à la victoire/défaite).
    /// </summary>
    public class MatchSessionManager : MonoBehaviour
    {
        private const float PlanningSeconds = 60f;
        private const int SnapshotIntervalMs = 100;
        private const int ZoneControlTurnCap = 20;
        // Sans plafond, deux joueurs qui se contentent de se cacher chaque tour pouvaient bloquer
        // indéfiniment l'unique emplacement de match du serveur (une seule partie à la fois, voir
        // "Portée V1" en tête de classe) — le Deathmatch avait ce plafond en Zone de Contrôle mais
        // pas ici.
        private const int DeathmatchTurnCap = 60;
        private const int MaxOrderPathNodes = 200;

        private string currentMatchMode = "deathmatch";

        // Connexions authentifiées mais dont on n'a pas encore reçu "join_matchmaking" (donc dont
        // on ne connaît pas encore le mode voulu).
        private readonly List<PlayerConnection> pendingMode = new List<PlayerConnection>();

        // Une file d'attente séparée par mode : deux joueurs ne sont appariés que s'ils ont
        // demandé le même mode.
        private readonly List<PlayerConnection> waitingDeathmatch = new List<PlayerConnection>();
        private readonly List<PlayerConnection> waitingZoneControl = new List<PlayerConnection>();

        private bool matchInProgress = false;

        private void Start()
        {
            StartCoroutine(SetupWorldOnce());
        }

        private IEnumerator SetupWorldOnce()
        {
            yield return null; // laisser les Awake/Start des autres composants de la scène s'exécuter d'abord

            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
            if (cityGen != null) cityGen.LoadDefaultOfflineCity();

            Debug.Log("[MatchSessionManager] Carte par défaut (hors-ligne) chargée pour le serveur.");
        }

        private void Update()
        {
            while (GameServerBootstrap.AuthenticatedConnections.TryDequeue(out PlayerConnection conn))
            {
                pendingMode.Add(conn);
            }

            // Lire "join_matchmaking" pour connaître le mode voulu, puis basculer la connexion
            // dans la file du mode correspondant. Par rétrocompatibilité (client plus ancien sans
            // champ "mode"), une valeur absente/vide vaut "deathmatch".
            for (int i = pendingMode.Count - 1; i >= 0; i--)
            {
                PlayerConnection conn = pendingMode[i];
                while (conn.TryDequeueMessage(out NetMessage msg))
                {
                    if (msg.type != "join_matchmaking") continue;
                    conn.Mode = string.IsNullOrEmpty(msg.mode) ? "deathmatch" : msg.mode;
                    var targetList = conn.Mode == "zone_control" ? waitingZoneControl : waitingDeathmatch;
                    targetList.Add(conn);
                    pendingMode.RemoveAt(i);
                    Debug.Log($"[MatchSessionManager] Joueur en file d'attente ({conn.Mode}) : {conn.UserId} ({targetList.Count} en attente)");
                    break;
                }
            }

            // Nettoyer les connexions perdues avant même d'avoir rejoint un match.
            pendingMode.RemoveAll(c => c.IsDisconnected);
            waitingDeathmatch.RemoveAll(c => c.IsDisconnected);
            waitingZoneControl.RemoveAll(c => c.IsDisconnected);

            if (!matchInProgress)
            {
                TryStartMatch(waitingDeathmatch);
                if (!matchInProgress) TryStartMatch(waitingZoneControl);
            }
        }

        private void TryStartMatch(List<PlayerConnection> queue)
        {
            if (queue.Count < 2) return;
            PlayerConnection p1 = queue[0];
            PlayerConnection p2 = queue[1];
            queue.RemoveRange(0, 2);
            matchInProgress = true;
            StartCoroutine(RunMatchGuarded(p1, p2));
        }

        /// <summary>
        /// Enveloppe RunMatch() pour qu'une exception non prévue (message client malformé,
        /// coordonnée invalide, etc.) ne laisse jamais matchInProgress bloqué à "true" pour
        /// toujours — vu qu'un seul match tourne à la fois (voir "Portée V1"), une exception
        /// non rattrapée y bloquait le serveur en entier, silencieusement, sans crash ni
        /// redémarrage Docker possible. `yield return` n'est pas autorisé dans un bloc
        /// try/catch en C#, d'où ce pompage manuel de l'énumérateur plutôt qu'un try/catch
        /// direct autour du corps de RunMatch.
        /// </summary>
        private IEnumerator RunMatchGuarded(PlayerConnection p1, PlayerConnection p2)
        {
            IEnumerator inner = RunMatch(p1, p2);
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
                    matchInProgress = false;
                    yield break;
                }

                if (!moved) yield break;
                yield return inner.Current;
            }
        }

        private static void AbortMatchSafely(PlayerConnection p)
        {
            try { if (!p.IsDisconnected) p.Send(new NetMessage { type = "match_over", winner_team = 0 }); } catch { }
            try { p.Close(); } catch { }
        }

        private IEnumerator RunMatch(PlayerConnection p1, PlayerConnection p2)
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
                matchInProgress = false;
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;
            UnitSpawnerUI.Instance.AutoDeployBattlefield();
            yield return null;

            if (mode == "zone_control")
            {
                CaptureZone zone = CaptureZone.Instance != null ? CaptureZone.Instance : CaptureZone.CreateAtMapCenter();
                zone.ResetProgress();
            }

            // Les deux camps sont pilotés par de vrais joueurs : TacticalAIPlanner ne les touchera
            // que si isGhosted passe à true (voir TacticalAIPlanner.cs, guard assoupli).
            foreach (var unit in UnitAI.AllLivingUnits) unit.isPlayerControlled = true;

            p1.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = p2.Username, mode = mode });
            p2.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 2, opponent_username = p1.Username, mode = mode });

            yield return CreateMatchRecord(matchId, p1, p2, mode);

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunPlanningPhase(p1, p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhase(turnNumber, p1, p2);

                int team1Alive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1);
                int team2Alive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2);

                if (team1Alive == 0 || team2Alive == 0)
                {
                    matchOver = true;
                    // Anéantissement mutuel (les deux équipes à 0) = match nul, pas une victoire par défaut.
                    winnerTeam = (team1Alive == 0 && team2Alive == 0) ? 0 : (team1Alive == 0 ? 2 : 1);
                }
                else if (currentMatchMode == "zone_control")
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
                    // Départage par nombre d'unités vivantes, puis par total de points de vie
                    // restants ; égalité parfaite sur les deux critères = match nul.
                    matchOver = true;
                    int team1Health = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).Sum(u => u.health);
                    int team2Health = UnitAI.AllLivingUnits.Where(u => u.teamID == 2).Sum(u => u.health);
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

            p1.Close();
            p2.Close();
            matchInProgress = false;
        }

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

            ApplyForPlayer(p1, p2);
            ApplyForPlayer(p2, p1);
        }

        private void DrainMessages(PlayerConnection conn, int turnNumber)
        {
            while (conn.TryDequeueMessage(out NetMessage msg))
            {
                if (msg.type == "submit_turn" && msg.turn_number == turnNumber)
                {
                    conn.PendingOrders = msg.orders ?? Array.Empty<UnitOrder>();
                    conn.HasSubmittedThisTurn = true;
                }
                else if (msg.type == "heartbeat")
                {
                    conn.LastHeartbeat = DateTime.UtcNow;
                }
            }
        }

        private void ApplyForPlayer(PlayerConnection conn, PlayerConnection opponent)
        {
            var myUnits = UnitAI.AllLivingUnits.Where(u => u.teamID == conn.TeamId).ToList();
            bool shouldGhost = conn.IsDisconnected || !conn.HasSubmittedThisTurn;

            if (shouldGhost)
            {
                foreach (var u in myUnits)
                {
                    u.isGhosted = true;
                    u.ClearTacticalPath();
                    TacticalAIPlanner.PlanTurnForUnit(u);
                }

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
                foreach (var u in myUnits) u.isGhosted = false;
                ApplyOrdersToUnits(myUnits, conn.PendingOrders);
            }
        }

        private void ApplyOrdersToUnits(List<UnitAI> myUnits, UnitOrder[] orders)
        {
            if (orders == null) return;

            foreach (var order in orders)
            {
                UnitAI unit = myUnits.FirstOrDefault(u => u.gameObject.name == order.unit_id);
                if (unit == null) continue;

                unit.ClearTacticalPath();
                if (order.path == null) continue;
                // Un client modifié pourrait soumettre un chemin de milliers de points (toujours
                // sous la limite de 8 Mo du protocole) pour faire tourner la simulation de
                // mouvement en boucle inutilement, ou des coordonnées NaN/Infinity/une valeur
                // d'action hors de l'enum pour déclencher un comportement indéfini plus loin dans
                // TacticalPathManager/NavMesh — on rejette silencieusement ce qui est invalide
                // plutôt que de faire confiance au client.
                if (order.path.Length > MaxOrderPathNodes) continue;

                foreach (var node in order.path)
                {
                    if (float.IsNaN(node.x) || float.IsNaN(node.y) || float.IsNaN(node.z)) continue;
                    if (float.IsInfinity(node.x) || float.IsInfinity(node.y) || float.IsInfinity(node.z)) continue;
                    if (!Enum.IsDefined(typeof(TacticalPathManager.NodeAction), node.action)) continue;

                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = new Vector3(node.x, node.y, node.z),
                        action = (TacticalPathManager.NodeAction)node.action
                    });
                }
            }
        }

        private IEnumerator RunExecutionPhase(int turnNumber, PlayerConnection p1, PlayerConnection p2)
        {
            var allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude).ToList();

            foreach (var unit in allUnits)
            {
                if (!unit.isDead) unit.ExecuterOrdres();
            }

            var snapshots = new List<Snapshot>();
            float elapsedMs = 0f;
            float nextSnapshotAt = 0f;

            float movementTimer = 0f;
            while (allUnits.Any(u => !u.isDead && u.IsMovingOrActing()) && movementTimer < 45f)
            {
                movementTimer += Time.deltaTime;
                elapsedMs += Time.deltaTime * 1000f;
                if (elapsedMs >= nextSnapshotAt)
                {
                    snapshots.Add(CaptureSnapshot((int)elapsedMs, allUnits));
                    nextSnapshotAt += SnapshotIntervalMs;
                }
                yield return null;
            }

            float combatTimer = 0f;
            while (allUnits.Any(u => !u.isDead && u.HasActiveTargetInRange()) && combatTimer < 2f)
            {
                combatTimer += Time.deltaTime;
                elapsedMs += Time.deltaTime * 1000f;
                if (elapsedMs >= nextSnapshotAt)
                {
                    snapshots.Add(CaptureSnapshot((int)elapsedMs, allUnits));
                    nextSnapshotAt += SnapshotIntervalMs;
                }
                yield return null;
            }

            if (movementTimer == 0f && combatTimer == 0f)
            {
                yield return new WaitForSeconds(0.8f);
                elapsedMs += 800f;
            }

            snapshots.Add(CaptureSnapshot((int)elapsedMs, allUnits)); // état final garanti

            foreach (var unit in allUnits)
            {
                unit.ResetOrderState();
                unit.isGhosted = false;
            }

            var result = new NetMessage
            {
                type = "turn_result",
                turn_number = turnNumber,
                snapshot_interval_ms = SnapshotIntervalMs,
                snapshots = snapshots.ToArray()
            };
            if (!p1.IsDisconnected) p1.Send(result);
            if (!p2.IsDisconnected) p2.Send(result);
        }

        private Snapshot CaptureSnapshot(int t, List<UnitAI> units)
        {
            var states = new UnitState[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                UnitAI u = units[i];
                states[i] = new UnitState
                {
                    unit_id = u.gameObject.name,
                    x = u.transform.position.x,
                    y = u.transform.position.y,
                    z = u.transform.position.z,
                    ry = u.transform.eulerAngles.y,
                    health = u.health,
                    dead = u.isDead,
                    shooting = u.IsShootingNow
                };
            }
            float zoneProgress1 = 0f, zoneProgress2 = 0f;
            if (currentMatchMode == "zone_control" && CaptureZone.Instance != null)
            {
                CaptureZone.Instance.Tick();
                zoneProgress1 = CaptureZone.Instance.ProgressTeam1;
                zoneProgress2 = CaptureZone.Instance.ProgressTeam2;
            }

            return new Snapshot { t = t, units = states, zone_progress_team1 = zoneProgress1, zone_progress_team2 = zoneProgress2 };
        }

        // =====================================================================
        // Persistance minimale via PostgREST (rôle service_role, contourne RLS).
        // Le log détaillé par tour (match_turn_orders / match_event_log) n'est pas
        // encore branché ici — non nécessaire pour le test à 2 téléphones, à ajouter
        // plus tard si un historique de partie détaillé est requis.
        // =====================================================================

        private IEnumerator FetchUsername(PlayerConnection conn)
        {
            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=eq.{conn.UserId}&select=username";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                try
                {
                    var parsed = JsonUtility.FromJson<UsernameQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0 && !string.IsNullOrEmpty(parsed.items[0].username))
                        conn.Username = parsed.items[0].username;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MatchSessionManager] Parsing username échoué : {ex.Message}");
                }
            }
        }

        private IEnumerator CreateMatchRecord(string matchId, PlayerConnection p1, PlayerConnection p2, string mode)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            string matchJson = "{\"id\":\"" + matchId + "\",\"status\":\"active\",\"mode\":\"" + mode + "\",\"started_at\":\"" + nowIso + "\"}";
            yield return PostgrestPost("/matches", matchJson);

            string participantsJson = "[" +
                "{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p1.UserId + "\",\"team_id\":1}," +
                "{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p2.UserId + "\",\"team_id\":2}" +
                "]";
            yield return PostgrestPost("/match_participants", participantsJson);
        }

        private IEnumerator CloseMatchRecord(string matchId, int winnerTeam)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            string patchJson = "{\"status\":\"finished\",\"winner_team\":" + winnerTeam + ",\"ended_at\":\"" + nowIso + "\"}";
            yield return PostgrestPatch($"/matches?id=eq.{matchId}", patchJson);
        }

        private IEnumerator PostgrestPost(string path, string jsonBody)
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
                Debug.LogWarning($"[MatchSessionManager] Écriture DB échouée ({path}) : {req.error} — {req.downloadHandler.text}");
        }

        private IEnumerator PostgrestPatch(string path, string jsonBody)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "PATCH");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Mise à jour DB échouée ({path}) : {req.error} — {req.downloadHandler.text}");
        }

        [Serializable] private class UsernameEntry { public string username; }
        [Serializable] private class UsernameQueryResult { public UsernameEntry[] items; }

        // =====================================================================
        // Classement ELO — profiles.rating existe déjà (schema.sql), jamais mis à jour avant.
        // =====================================================================
        private const float EloKFactor = 32f;

        private IEnumerator UpdateRatings(PlayerConnection p1, PlayerConnection p2, int winnerTeam)
        {
            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=in.({p1.UserId},{p2.UserId})&select=id,rating";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MatchSessionManager] Lecture des ratings échouée : {req.error}");
                yield break;
            }

            int rating1 = 1000, rating2 = 1000;
            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<RatingQueryResult>(wrapped);
                if (parsed?.items != null)
                {
                    foreach (var entry in parsed.items)
                    {
                        if (entry.id == p1.UserId) rating1 = entry.rating;
                        else if (entry.id == p2.UserId) rating2 = entry.rating;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchSessionManager] Parsing ratings échoué : {ex.Message}");
                yield break;
            }

            // winnerTeam == 0 : match nul (anéantissement mutuel ou double déconnexion) -> 0.5 partout.
            float score1 = winnerTeam == 0 ? 0.5f : (winnerTeam == p1.TeamId ? 1f : 0f);
            float score2 = winnerTeam == 0 ? 0.5f : (winnerTeam == p2.TeamId ? 1f : 0f);

            float expected1 = 1f / (1f + Mathf.Pow(10f, (rating2 - rating1) / 400f));
            float expected2 = 1f / (1f + Mathf.Pow(10f, (rating1 - rating2) / 400f));

            int newRating1 = Mathf.RoundToInt(rating1 + EloKFactor * (score1 - expected1));
            int newRating2 = Mathf.RoundToInt(rating2 + EloKFactor * (score2 - expected2));

            p1.NewRating = newRating1;
            p1.RatingDelta = newRating1 - rating1;
            p2.NewRating = newRating2;
            p2.RatingDelta = newRating2 - rating2;

            yield return PostgrestPatch($"/profiles?id=eq.{p1.UserId}", "{\"rating\":" + newRating1 + "}");
            yield return PostgrestPatch($"/profiles?id=eq.{p2.UserId}", "{\"rating\":" + newRating2 + "}");

            Debug.Log($"[MatchSessionManager] Ratings mis à jour : {p1.UserId} {rating1}->{newRating1}, {p2.UserId} {rating2}->{newRating2}");
        }

        [Serializable] private class RatingEntry { public string id; public int rating; }
        [Serializable] private class RatingQueryResult { public RatingEntry[] items; }
    }
}
