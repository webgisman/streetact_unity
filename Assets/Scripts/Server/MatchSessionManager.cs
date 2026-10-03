using System;
using System.Collections;
using System.Collections.Generic;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Serveur de jeu Novgov (une instance du pool game-server-1/2/3). Deux demandes possibles d'un
    /// client, lues dans "join_matchmaking" :
    ///   - "conquest"     : prendre un quartier LIBRE (capture instantanée, MatchSessionManager_Conquest.cs) ;
    ///   - "siege_battle" : rejoindre la bataille au tour par tour d'un siège (MatchSessionManager_
    ///                      SiegeBattle.cs, moteur dans MatchSessionManager_MatchLive.cs).
    /// Tâches de fond : revenu des quartiers, registre d'instances (server_instances), résolution
    /// automatique des sièges échus (MatchSessionManager_Siege.cs).
    ///
    /// Un seul combat à la fois par processus (scène/NavMesh partagés, voir matchInProgress). Pas de
    /// reprise de partie après une coupure : les unités d'un joueur parti tiennent leur position
    /// jusqu'à la fin de la bataille.
    /// </summary>
    public partial class MatchSessionManager : MonoBehaviour
    {
        // Plafonds de chaque phase d'une bataille — volontairement larges (5 min) : ils ne doivent
        // jamais se déclencher en usage normal, seulement contre un joueur réellement absent.
        private const float PlanningSeconds = 300f;
        private const float DeploymentSeconds = 300f;
        // Le vrai compte à rebours de déploiement ne démarre qu'une fois la carte chargée chez le
        // client ("deployment_ready") — ceci borne l'attente de ce signal.
        private const float MapReadyMaxWaitSeconds = 300f;

        // Sans plafond, deux joueurs qui se cachent chaque tour pourraient faire durer une bataille
        // indéfiniment.
        private const int BattleTurnCap = 60;

        // Un client modifié ne doit pas pouvoir déclencher des centaines de recherches A* par unité
        // et par tour : un joueur légitime ne pose jamais plus de quelques points (budget de
        // mouvement de 50 m).
        private const int MaxOrderPathNodes = 40;

        // Connexions authentifiées dont on n'a pas encore reçu "join_matchmaking".
        private readonly List<PlayerConnection> pendingMode = new List<PlayerConnection>();

        // Vrai tant qu'une capture ou une bataille utilise la scène serveur (un seul combat à la fois).
        private bool matchInProgress = false;

        private void Start()
        {
            StartCoroutine(SetupWorldOnce());
            StartCoroutine(InstanceHeartbeatLoop());
            StartCoroutine(ZoneIncomeLoop());
            StartCoroutine(SiegeResolutionLoop());
        }

        // =====================================================================
        // Revenu passif des quartiers : chaque quartier possédé rapporte des Points d'Action à son
        // propriétaire toutes les 5 minutes (10 PA par niveau de bâtiment).
        // =====================================================================
        private const float ZoneIncomeIntervalSeconds = 300f; // 5 minutes
        private const int ZoneIncomePerZone = 10;

        // Une SEULE instance du pool verse le revenu (2026-10-03) : les 3 instances le versaient
        // chacune, soit trois fois trop de PA toutes les 5 minutes.
        private const string ZoneIncomeInstanceId = "game-server-1";

        private IEnumerator ZoneIncomeLoop()
        {
            if (GameServerBootstrap.InstanceId != ZoneIncomeInstanceId) yield break;
            while (true)
            {
                yield return new WaitForSeconds(ZoneIncomeIntervalSeconds);
                yield return GrantZoneIncome();
            }
        }

        [Serializable] private class ZoneOwnerEntry { public string owner_user_id; public int building_level; }
        [Serializable] private class ZoneOwnerQueryResult { public ZoneOwnerEntry[] items; }

        [Serializable] private class ActionPointsEntry { public int action_points; }
        [Serializable] private class ActionPointsQueryResult { public ActionPointsEntry[] items; }

        /// <summary>Solde de Points d'Action ACTUEL d'un joueur — lu juste avant GrantZoneIncome pour
        /// lui ajouter le revenu au lieu de l'écraser.</summary>
        private IEnumerator FetchActionPoints(string userId, Action<int> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=eq.{userId}&select=action_points";
            using var req = UnityEngine.Networking.UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            int ap = 0;
            if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<ActionPointsQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0) ap = parsed.items[0].action_points;
                }
                catch (Exception ex) { Debug.LogWarning($"[ZoneIncome] Parsing action_points ({userId}) échoué : {ex.Message}"); }
            }
            else
            {
                Debug.LogWarning($"[ZoneIncome] Lecture action_points ({userId}) échouée : {req.error}");
            }
            onResult(ap);
        }

        /// <summary>Verse à chaque propriétaire le revenu de tous ses quartiers, pondéré par le niveau
        /// de bâtiment de chacun (schema.sql §11, upgrade_building()).</summary>
        private IEnumerator GrantZoneIncome()
        {
            string url = $"{GameServerBootstrap.RestUrl}/zones?owner_user_id=not.is.null&select=owner_user_id,building_level";
            using var req = UnityEngine.Networking.UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();
            if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[ZoneIncome] Lecture des Zones possédées échouée : {req.error}");
                yield break;
            }

            var incomeByOwner = new Dictionary<string, int>();
            var zoneCountByOwner = new Dictionary<string, int>();
            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<ZoneOwnerQueryResult>(wrapped);
                if (parsed?.items != null)
                {
                    foreach (var e in parsed.items)
                    {
                        if (string.IsNullOrEmpty(e.owner_user_id)) continue;
                        int level = Mathf.Max(1, e.building_level);
                        incomeByOwner.TryGetValue(e.owner_user_id, out int curIncome);
                        incomeByOwner[e.owner_user_id] = curIncome + level * ZoneIncomePerZone;
                        zoneCountByOwner.TryGetValue(e.owner_user_id, out int curCount);
                        zoneCountByOwner[e.owner_user_id] = curCount + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZoneIncome] Parsing des Zones possédées échoué : {ex.Message}");
                yield break;
            }

            foreach (var kv in incomeByOwner)
            {
                int currentAp = 0;
                yield return FetchActionPoints(kv.Key, ap => currentAp = ap);
                int income = kv.Value;
                yield return PostgrestPatch($"/profiles?id=eq.{kv.Key}", "{\"action_points\":" + (currentAp + income) + "}");
                Debug.Log($"[ZoneIncome] {kv.Key} : +{income} PA pour {zoneCountByOwner[kv.Key]} quartier(s) possédé(s) (niveaux de bâtiment inclus).");
            }
        }

        // =====================================================================
        // Registre d'instances (table "server_instances", schema.sql §7) : cette instance annonce
        // périodiquement si elle est libre ou occupée. Les clients s'en servent pour choisir où se
        // connecter (une bataille de siège : instance déterminée par l'id du siège, voir
        // MultiplayerMatchController.ConnectToGameServerCoroutine).
        // =====================================================================
        private const float InstanceHeartbeatSeconds = 2f;

        private IEnumerator InstanceHeartbeatLoop()
        {
            while (true)
            {
                yield return ReportInstanceStatus();
                yield return new WaitForSeconds(InstanceHeartbeatSeconds);
            }
        }

        /// <summary>Upsert immédiat de l'état de cette instance — appelé aussi à chaque changement réel
        /// (début/fin d'une capture ou d'une bataille).</summary>
        private IEnumerator ReportInstanceStatus()
        {
            string status = matchInProgress ? "busy" : "free";
            string nowIso = DateTime.UtcNow.ToString("o");
            string json = "{\"id\":\"" + GameServerBootstrap.InstanceId + "\"" +
                          ",\"public_port\":" + GameServerBootstrap.PublicPort +
                          ",\"status\":\"" + status + "\"" +
                          ",\"updated_at\":\"" + nowIso + "\"}";
            yield return PostgrestUpsert("/server_instances", json);
        }

        private IEnumerator SetupWorldOnce()
        {
            yield return null; // laisser les Awake/Start des autres composants de la scène s'exécuter d'abord

            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
            if (cityGen != null) cityGen.LoadDefaultOfflineCity();
            // Invalide le cache de géométrie (murs/grille, voir TacticalGridBuilder) : une ville
            // fraîchement (re)générée a des bâtiments totalement différents.
            TacticalGridBuilder.InvalidateCache();

            Debug.Log("[MatchSessionManager] Carte par défaut (hors-ligne) chargée pour le serveur.");
        }

        private void Update()
        {
            // Signal de vie serveur -> client pour TOUTE connexion authentifiée, quelle que soit la
            // phase (salle d'attente d'un siège, chargement de carte, déploiement...) : sans ça, le
            // délai de lecture de 25 s du client le déconnectait pendant une longue attente.
            PlayerConnection.PumpKeepalives();

            while (GameServerBootstrap.AuthenticatedConnections.TryDequeue(out PlayerConnection conn))
            {
                pendingMode.Add(conn);
            }

            for (int i = pendingMode.Count - 1; i >= 0; i--)
            {
                PlayerConnection conn = pendingMode[i];
                while (conn.TryDequeueMessage(out NetMessage msg))
                {
                    if (msg.type != "join_matchmaking") continue;
                    conn.Mode = msg.mode;
                    pendingMode.RemoveAt(i);

                    if (conn.Mode == "conquest")
                    {
                        HandleConquestMessage(conn, msg);
                    }
                    else if (conn.Mode == "siege_battle")
                    {
                        HandleSiegeBattleMessage(conn, msg);
                    }
                    else
                    {
                        // Modes retirés le 2026-10-03 (Match à mort, Contrôle de zone, Entraînement,
                        // ancien siège en différé) : un client qui n'est pas à jour est refusé
                        // proprement plutôt que laissé en attente sans fin.
                        Debug.Log($"[MatchSessionManager] Mode « {conn.Mode} » refusé (client à mettre à jour) : {conn.UserId}");
                        conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "outdated_client", zone_tile_x = msg.zone_tile_x, zone_tile_y = msg.zone_tile_y });
                        conn.Close();
                    }
                    break;
                }
            }

            // Nettoyer les connexions perdues avant même d'avoir choisi un mode.
            pendingMode.RemoveAll(c => c.IsDisconnected);

            PumpSiegeLobbies();
        }
    }
}
