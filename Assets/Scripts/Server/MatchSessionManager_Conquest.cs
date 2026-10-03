using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;
using UnityEngine.Networking;

namespace Novgov.Server
{
    /// <summary>
    /// Conquête territoriale — Zones de Conquête (= quartiers, grille Slippy Map fixe, zoom
    /// CityGenerator.ZONE_ZOOM) :
    ///   - quartier LIBRE -> capture instantanée, sans combat (mode "conquest", ci-dessous) ;
    ///   - quartier déjà tenu par un autre joueur -> SIÈGE, une bataille au tour par tour contre son
    ///     propriétaire (MatchSessionManager_SiegeBattle.cs / _Siege.cs). Jamais de combat contre une
    ///     garnison IA : la Conquête n'utilise pas d'IA (2026-10-03).
    /// Contient aussi les outils partagés de persistance des Zones (lecture du propriétaire, capture
    /// atomique, pillage) et le chargement de la géométrie réelle d'un quartier sur le serveur.
    /// </summary>
    public partial class MatchSessionManager
    {
        private const int ConquestCaptureRatingGain = 8;

        private void HandleConquestMessage(PlayerConnection conn, NetMessage msg)
        {
            if (matchInProgress)
            {
                // La scène serveur est occupée (bataille de siège en cours) — le client peut retenter
                // une nouvelle demande plus tard.
                conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_busy", zone_tile_x = msg.zone_tile_x, zone_tile_y = msg.zone_tile_y });
                conn.Close();
                return;
            }

            matchInProgress = true;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunConquestRequestGuarded(conn, msg.zone_tile_x, msg.zone_tile_y));
        }

        /// <summary>Une exception non prévue ne doit jamais laisser matchInProgress bloqué à "true"
        /// pour toujours.</summary>
        private IEnumerator RunConquestRequestGuarded(PlayerConnection conn, int tileX, int tileY)
        {
            return SafeCoroutineRunner.Run(
                RunConquestRequest(conn, tileX, tileY),
                onComplete: () =>
                {
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                },
                onException: (Exception e) =>
                {
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant une conquête — abandon : {e}");
                    try { if (!conn.IsDisconnected) conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_error", zone_tile_x = tileX, zone_tile_y = tileY }); } catch { }
                    try { conn.Close(); } catch { }
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                }
            );
        }

        /// <summary>Vrai si (bx,by) est adjacente à (ax,ay) au sens des 4 directions cardinales
        /// (Nord/Sud/Est/Ouest, jamais diagonale).</summary>
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
                    yield return ApplyRatingDeltaWithRetry(attacker, ConquestCaptureRatingGain);
                    attacker.Send(new NetMessage { type = "zone_captured", success = true, zone_tile_x = tileX, zone_tile_y = tileY, your_new_rating = attacker.NewRating, rating_delta = attacker.RatingDelta });
                }
                else
                    attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "zone_taken", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            // Quartier déjà tenu par un autre joueur : jamais de combat ici (plus de "garnison IA" —
            // pas d'IA en multijoueur, 2026-10-03). Il se prend par un SIÈGE, une bataille au tour par
            // tour contre son propriétaire (voir MatchSessionManager_SiegeBattle.cs) ; le client à
            // jour ne l'envoie de toute façon jamais ici, sauf course (quartier pris entre-temps).
            attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "zone_owned", zone_tile_x = tileX, zone_tile_y = tileY });
            attacker.Close();
        }

        [Serializable] private class PillageResult { public int stolen_ap; public int attacker_new_ap; }
        [Serializable] private class PillageQueryResult { public PillageResult[] items; }

        /// <summary>Pille les Points d'Action du défenseur (ramené à 0) vers l'attaquant, quand un
        /// siège lui prend un quartier (voir MatchSessionManager_Siege.ApplySiegeOutcome).
        /// CORRECTIF 2026-09-19 : Utilisation d'un appel RPC transactionnel (pillage_action_points)
        /// plutôt qu'un schéma Fetch -> Patch, pour éviter une condition de course permettant
        /// de dupliquer des AP si un joueur dépensait ses points pile au moment du pillage.
        /// </summary>
        private IEnumerator LootActionPoints(string attackerUserId, string defenderOwnerId)
        {
            string url = $"{GameServerBootstrap.RestUrl}/rpc/pillage_action_points";
            string jsonBody = $"{{\"attacker_id\":\"{attackerUserId}\", \"defender_id\":\"{defenderOwnerId}\"}}";

            using var req = new UnityWebRequest(url, "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Conquête] Pillage échoué : {req.error} / {req.downloadHandler?.text}");
                yield break;
            }
            
            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<PillageQueryResult>(wrapped);
                if (parsed?.items != null && parsed.items.Length > 0)
                {
                    int stolenAP = parsed.items[0].stolen_ap;
                    Debug.Log($"[Conquête] {attackerUserId} a pillé {stolenAP} AP au joueur {defenderOwnerId} !");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Conquête] Parsing pillage échoué : {ex.Message}");
            }
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

            yield return EnsureHqBuildingIndex(tileX, tileY);
        }

        /// <summary>Désigne UNE FOIS, en base (public.zones.hq_building_index), le bâtiment "HQ" de
        /// cette Zone — le plus grand par emprise au sol, un choix déterministe qui donne le MÊME
        /// résultat à chaque appel tant que la géométrie de la tuile ne change pas. Remplace
        /// l'ancien MultiplayerMatchController.ClaimBuildingAsync (index tiré au hasard côté client,
        /// écrit uniquement dans le PlayerPrefs local de CET appareil — jamais partagé, d'où le
        /// bâtiment jaune incohérent entre les deux joueurs d'un même match). L'index utilisé est
        /// la position du bâtiment dans BuildingStructure.AllBuildings au moment de la génération —
        /// la MÊME numérotation que CityGenerator.lotIndex côté rendu (les deux parcourent les
        /// mêmes données OSM dans le même ordre, déjà l'hypothèse retenue ailleurs pour la
        /// vérification de hash de géométrie, voir city_verify) et que TacticalBuilding.id côté
        /// TacticalGridBuilder.
        ///
        /// Toujours réécrit (pas seulement "si absent") : le calcul est déterministe à partir de la
        /// géométrie de la tuile, donc idempotent — appeler ceci plusieurs fois sur la même tuile
        /// redonne toujours le même index, un upsert répété est donc sans risque et plus simple qu'un
        /// aller-retour de lecture préalable.</summary>
        private IEnumerator EnsureHqBuildingIndex(int tileX, int tileY)
        {
            var buildings = BuildingStructure.AllBuildings;
            if (buildings == null || buildings.Count == 0) yield break;

            int bestIndex = -1;
            float bestArea = -1f;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b == null || b.polygonFootprint == null || b.polygonFootprint.Count < 3) continue;
                float area = PolygonArea(b.polygonFootprint);
                if (area > bestArea) { bestArea = area; bestIndex = i; }
            }
            if (bestIndex < 0) yield break;

            string json = "{\"tile_x\":" + tileX + ",\"tile_y\":" + tileY + ",\"zoom\":" + CityGenerator.ZONE_ZOOM +
                          ",\"hq_building_index\":" + bestIndex + "}";
            yield return PostgrestUpsert("/zones", json);
            Debug.Log($"[HQ] Zone ({tileX},{tileY}) : bâtiment HQ désigné = index {bestIndex} (emprise {bestArea:F1} m²).");
        }

        /// <summary>Aire d'un polygone 2D par la formule du lacet (shoelace) — valeur absolue, peu
        /// importe le sens de parcours du contour.</summary>
        private static float PolygonArea(List<Vector2> polygon)
        {
            float sum = 0f;
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % n];
                sum += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(sum) * 0.5f;
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

        /// <summary>Toutes les Zones actuellement possédées par <paramref name="userId"/> — sert à
        /// valider l'adjacence d'une nouvelle capture (RunConquestRequest). En cas d'échec réseau,
        /// retourne une liste VIDE (pas null) : le joueur est alors traité comme n'ayant aucune Zone
        /// (comportement "fail-open", comme FetchZoneOwner qui traite un échec comme "Zone neutre").</summary>
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
