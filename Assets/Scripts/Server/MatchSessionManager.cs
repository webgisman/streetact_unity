using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StreetAct.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace StreetAct.Server
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

        private readonly List<PlayerConnection> waiting = new List<PlayerConnection>();
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
                waiting.Add(conn);
                Debug.Log($"[MatchSessionManager] Joueur en file d'attente : {conn.UserId} ({waiting.Count} en attente)");
            }

            // Nettoyer les connexions perdues avant même d'avoir rejoint un match.
            waiting.RemoveAll(c => c.IsDisconnected);

            if (!matchInProgress && waiting.Count >= 2)
            {
                PlayerConnection p1 = waiting[0];
                PlayerConnection p2 = waiting[1];
                waiting.RemoveRange(0, 2);
                matchInProgress = true;
                StartCoroutine(RunMatch(p1, p2));
            }
        }

        private IEnumerator RunMatch(PlayerConnection p1, PlayerConnection p2)
        {
            string matchId = Guid.NewGuid().ToString();
            p1.TeamId = 1;
            p2.TeamId = 2;

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

            // Les deux camps sont pilotés par de vrais joueurs : TacticalAIPlanner ne les touchera
            // que si isGhosted passe à true (voir TacticalAIPlanner.cs, guard assoupli).
            foreach (var unit in UnitAI.AllLivingUnits) unit.isPlayerControlled = true;

            p1.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = p2.Username });
            p2.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 2, opponent_username = p1.Username });

            yield return CreateMatchRecord(matchId, p1, p2);

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
                    winnerTeam = team1Alive == 0 ? 2 : 1;
                }

                turnNumber++;
            }

            var overMsg = new NetMessage { type = "match_over", winner_team = winnerTeam };
            if (!p1.IsDisconnected) p1.Send(overMsg);
            if (!p2.IsDisconnected) p2.Send(overMsg);

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

                foreach (var node in order.path)
                {
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
            return new Snapshot { t = t, units = states };
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

        private IEnumerator CreateMatchRecord(string matchId, PlayerConnection p1, PlayerConnection p2)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            string matchJson = "{\"id\":\"" + matchId + "\",\"status\":\"active\",\"started_at\":\"" + nowIso + "\"}";
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
    }
}
