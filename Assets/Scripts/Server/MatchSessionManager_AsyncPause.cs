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
    /// Rythme "async" (jusqu'à 6h par tour, voir NetMessage.turn_pace) — 2026-09-13. Un match "fast"
    /// (5 min/tour, comportement d'origine) garde ses vraies UnitAI vivantes dans la scène entre deux
    /// tours ; un match async ne peut PAS se permettre ça (voir MatchSessionManager_MatchLive.cs,
    /// commentaire sur les files d'attente séparées) — il sérialise l'état réel des UnitAI dans
    /// `matches.paused_roster_json`, DÉTRUIT les GameObjects, et libère matchInProgress pendant
    /// l'attente (potentiellement des heures). Les vraies UnitAI ne sont respawnées qu'au moment de
    /// résoudre le tour suivant, une fois les deux joueurs (ou le délai de 6h) prêts.
    ///
    /// ÉTAT PERSISTÉ À TRAVERS UNE PAUSE (2026-09-13, étendu suite à demande explicite — "critique") :
    /// position/rotation/PV/type/équipe, ET garnison de fenêtre (isGarrisoned + la fenêtre exacte),
    /// intérieur de bâtiment (currentBuilding), guet (isGuarding), camouflage (isCamouflaged), et
    /// perché sur toit (isRooftopSniper). Identifie bâtiment/fenêtre par INDEX dans
    /// BuildingStructure.AllBuildings au moment de la sérialisation, jamais par référence C# (détruite
    /// avec la scène) — valide seulement parce que la reprise recharge TOUJOURS la même carte
    /// (LoadZoneOnServer/RestoreDefaultMapOnServer avec le même cacheKey) avant de relire ces index,
    /// et que la génération de ville est déterministe à partir des mêmes données (même hypothèse déjà
    /// faite ailleurs dans ce projet pour la vérification de hash de géométrie, voir city_verify).
    ///
    /// RESTE non persisté (simplification assumée, documentée dans
    /// 11-real-engine-async-pace-notifications-2026-09-13.md) : tacticalPath en cours (vide de toute
    /// façon à cet instant précis — la pause n'a lieu qu'après ResetOrderState en fin d'exécution),
    /// cooldowns d'arme exacts (repartent à 0, avantage mineur et temporaire pour l'unité concernée).
    /// La progression de Zone de Contrôle, elle, SURVIT maintenant à une pause (zone_progress_team1/2
    /// ci-dessous) — ce n'était pas le cas dans la version précédente de ce fichier.
    /// </summary>
    public partial class MatchSessionManager
    {
        private const float AsyncPlanningSeconds = 6f * 60f * 60f; // 6 heures

        [Serializable]
        private class PausedUnitDto
        {
            public string unit_id;
            public int unit_type;
            public int team_id;
            public float x;
            public float y;
            public float z;
            public float ry;
            public int health;
            public bool is_garrisoned;
            public int garrison_building_id = -1; // index dans BuildingStructure.AllBuildings, -1 = aucun
            public int garrison_window_id = -1;   // BuildingWindow.id à l'intérieur de ce bâtiment
            public int current_building_id = -1;  // intérieur (hors garnison de fenêtre), -1 = aucun
            public bool is_guarding;
            public bool is_camouflaged;
            public bool is_rooftop_sniper;
        }
        [Serializable] private class PausedRosterDto { public PausedUnitDto[] units; public float zone_progress_team1; public float zone_progress_team2; }

        /// <summary>Photo de toutes les vraies UnitAI vivantes de CE match, avant destruction — PV,
        /// position, ET état tactique persistant (garnison/intérieur/guet/camouflage/toit). Écrit
        /// directement dans matches.paused_roster_json (PATCH REST, même mécanisme que
        /// CloseMatchRecord).</summary>
        private IEnumerator PersistPausedRoster(string matchId)
        {
            var buildingsSnapshot = new List<BuildingStructure>(BuildingStructure.AllBuildings);
            var buildingIndex = new Dictionary<BuildingStructure, int>();
            for (int i = 0; i < buildingsSnapshot.Count; i++) buildingIndex[buildingsSnapshot[i]] = i;

            var units = UnitAI.AllLivingUnits.Where(u => u != null && !u.isDead).ToList();
            var dto = new PausedRosterDto
            {
                units = units.Select(u =>
                {
                    int garrisonBuildingId = -1, garrisonWindowId = -1;
                    if (u.isGarrisoned && u.currentWindow != null)
                    {
                        // La fenêtre ne porte pas de référence directe vers "son" bâtiment — on
                        // cherche celui dont la liste windows contient cette instance précise.
                        foreach (var b in buildingsSnapshot)
                        {
                            if (b != null && b.windows != null && b.windows.Contains(u.currentWindow))
                            {
                                garrisonBuildingId = buildingIndex[b];
                                garrisonWindowId = u.currentWindow.id;
                                break;
                            }
                        }
                    }
                    int currentBuildingId = (u.currentBuilding != null && buildingIndex.TryGetValue(u.currentBuilding, out int bi)) ? bi : -1;

                    return new PausedUnitDto
                    {
                        unit_id = u.gameObject.name,
                        unit_type = (int)UnitTypeStats.InferType(u),
                        team_id = u.teamID,
                        x = u.transform.position.x,
                        y = u.transform.position.y,
                        z = u.transform.position.z,
                        ry = u.transform.eulerAngles.y,
                        health = u.health,
                        is_garrisoned = u.isGarrisoned,
                        garrison_building_id = garrisonBuildingId,
                        garrison_window_id = garrisonWindowId,
                        current_building_id = currentBuildingId,
                        is_guarding = u.isGuarding,
                        is_camouflaged = u.isCamouflaged,
                        is_rooftop_sniper = u.isRooftopSniper
                    };
                }).ToArray(),
                zone_progress_team1 = currentMatchMode == "zone_control" && CaptureZone.Instance != null ? CaptureZone.Instance.ProgressTeam1 : 0f,
                zone_progress_team2 = currentMatchMode == "zone_control" && CaptureZone.Instance != null ? CaptureZone.Instance.ProgressTeam2 : 0f
            };
            string json = JsonUtility.ToJson(dto);
            // Échappement minimal : le JSON de dto est déjà valide, on l'enveloppe dans un objet
            // PATCH classique {"paused_roster_json": <json>} — JsonUtility ne sait pas sérialiser un
            // objet imbriqué déjà-JSON tel quel, donc on construit la requête à la main ici plutôt que
            // de repasser par un DTO englobant.
            string patchBody = "{\"paused_roster_json\":" + json + "}";
            yield return PostgrestPatch($"/matches?id=eq.{matchId}", patchBody);
            Debug.Log($"[AsyncPause] [{matchId}] Effectif ({dto.units.Length} unité(s)) sérialisé (garnison/intérieur/guet/camouflage/toit inclus) — scène libérée pour d'autres matchs.");
        }

        /// <summary>Relit le dernier effectif sérialisé pour ce match — null si jamais mis en pause
        /// (ne devrait pas arriver pour un match async après son premier tour) ou en cas d'échec réseau
        /// (le match continue quand même, voir l'appelant : un effectif introuvable revient à un repli
        /// "aucune unité respawnée", jamais une exception qui planterait le match).</summary>
        private IEnumerator FetchPausedRoster(string matchId, Action<PausedRosterDto> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/matches?id=eq.{matchId}&select=paused_roster_json";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[AsyncPause] [{matchId}] Lecture de l'effectif en pause échouée : {req.error}");
                onResult(null);
                yield break;
            }

            try
            {
                // Réponse PostgREST : [{"paused_roster_json": {...}}] — JsonUtility ne parse pas un
                // tableau top-level ni un champ jsonb imbriqué directement, extraction manuelle du
                // sous-objet avant de le redonner à JsonUtility.
                string text = req.downloadHandler.text.Trim();
                int fieldStart = text.IndexOf("\"paused_roster_json\":", StringComparison.Ordinal);
                if (fieldStart < 0) { onResult(null); yield break; }
                int braceStart = text.IndexOf('{', fieldStart);
                if (braceStart < 0) { onResult(null); yield break; }
                int depth = 0, i = braceStart;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}') { depth--; if (depth == 0) break; }
                }
                string rosterJson = text.Substring(braceStart, i - braceStart + 1);
                onResult(JsonUtility.FromJson<PausedRosterDto>(rosterJson));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AsyncPause] [{matchId}] Parsing de l'effectif en pause échoué : {ex.Message}");
                onResult(null);
            }
        }

        /// <summary>Respawn RÉEL (UnitSpawnerUI.SpawnUnitAt, pas une donnée pure) de chaque unité de
        /// l'effectif sauvegardé, avec le MÊME nom de GameObject (forcedName) qu'avant la pause — les
        /// ordres soumis par le client pendant l'attente référencent des unit_id par ce nom
        /// (NetMessage.UnitOrder.unit_id), jamais réappris depuis un nouveau spawn ; sans ce nom
        /// préservé, tout ordre soumis pendant que la partie était en pause serait silencieusement
        /// ignoré (ApplyOrdersToUnits ne retrouverait plus l'unité par son ancien nom).
        ///
        /// Restaure aussi garnison/intérieur/guet/camouflage/toit — appelé APRÈS que la carte a été
        /// rechargée (LoadZoneOnServer/RestoreDefaultMapOnServer), donc BuildingStructure.AllBuildings
        /// est déjà repeuplé dans le MÊME ordre qu'au moment de la sérialisation (génération
        /// déterministe à partir des mêmes données — même hypothèse que city_verify).</summary>
        private void RespawnPausedRoster(PausedRosterDto roster)
        {
            if (roster?.units == null) return;
            var buildingsSnapshot = new List<BuildingStructure>(BuildingStructure.AllBuildings);

            foreach (var u in roster.units)
            {
                UnitAI ai = UnitSpawnerUI.Instance.SpawnUnitAt((UnitSpawnerUI.UnitType)u.unit_type, new Vector3(u.x, u.y, u.z), u.team_id, forcedName: u.unit_id, skipSafeSpawnAdjustment: true);
                if (ai == null) continue;
                ai.transform.rotation = Quaternion.Euler(0f, u.ry, 0f);
                ai.health = u.health;
                ai.isPlayerControlled = true; // voir RunMatchLive : les deux camps sont toujours de vrais joueurs ici
                ai.isGuarding = u.is_guarding;
                ai.isCamouflaged = u.is_camouflaged;
                ai.isRooftopSniper = u.is_rooftop_sniper;

                if (u.current_building_id >= 0 && u.current_building_id < buildingsSnapshot.Count)
                {
                    BuildingStructure building = buildingsSnapshot[u.current_building_id];
                    if (building != null)
                    {
                        building.RegisterUnitInside(ai);
                        ai.currentBuilding = building;
                    }
                }

                if (u.is_garrisoned && u.garrison_building_id >= 0 && u.garrison_building_id < buildingsSnapshot.Count)
                {
                    BuildingStructure building = buildingsSnapshot[u.garrison_building_id];
                    BuildingStructure.BuildingWindow window = building?.windows?.Find(w => w.id == u.garrison_window_id);
                    if (building != null && window != null)
                    {
                        building.OccupyWindow(window, ai);
                        ai.currentWindow = window;
                        ai.isGarrisoned = true;
                    }
                    else
                    {
                        // Fenêtre introuvable (bâtiment détruit/géométrie différente depuis la pause,
                        // en principe impossible mais pas structurellement garanti) — repli sûr : PAS
                        // de garnison plutôt qu'un état incohérent (currentWindow pointant sur rien).
                        Debug.LogWarning($"[AsyncPause] Fenêtre de garnison introuvable au respawn pour '{u.unit_id}' (bâtiment {u.garrison_building_id}, fenêtre {u.garrison_window_id}) — reprise sans garnison pour cette unité.");
                    }
                }
            }
        }

        private IEnumerator WriteNotification(string userId, string matchId, string type, string message)
        {
            string json = "{\"user_id\":\"" + userId + "\",\"match_id\":\"" + matchId + "\",\"type\":\"" + type + "\",\"message\":\"" + EscapeJsonString(message) + "\"}";
            yield return PostgrestPost("/notifications", json);
        }

        private static string EscapeJsonString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Attente async pure — AUCUNE UnitAI vivante pendant cette boucle (voir
        /// PersistPausedRoster, appelé par l'appelant juste avant). Ne fait que collecter
        /// submit_turn/heartbeat via DrainMessages, jusqu'à AsyncPlanningSeconds (6h) ou double
        /// déconnexion. Contrairement à RunPlanningPhaseLiveNoAI, n'appelle PAS ApplyForPlayerLiveNoAI
        /// à la fin (aucune UnitAI à qui donner un ordre tant que RespawnPausedRoster n'a pas tourné) —
        /// c'est l'appelant (RunMatchLive) qui applique les ordres une fois le respawn fait.</summary>
        private IEnumerator WaitForBothOrdersAsync(PlayerConnection p1, PlayerConnection p2, int turnNumber, string matchId)
        {
            p1.HasSubmittedThisTurn = false;
            p2.HasSubmittedThisTurn = false;

            bool notifiedP1 = false, notifiedP2 = false;
            float remaining = AsyncPlanningSeconds;

            while (remaining > 0f && !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn))
            {
                DrainMessages(p1, turnNumber);
                DrainMessages(p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                // Notifie chaque joueur UNE SEULE FOIS que c'est son tour, dès que l'autre a déjà
                // soumis (le cas le plus fréquent : un joueur soumet vite, l'autre revient des heures
                // plus tard) — voir WriteNotification. Un joueur qui soumet aussitôt tout seul n'a
                // besoin d'aucune notification, l'autre n'a alors encore rien soumis.
                if (!notifiedP1 && p2.HasSubmittedThisTurn && !p1.HasSubmittedThisTurn)
                {
                    notifiedP1 = true;
                    yield return WriteNotification(p1.UserId, matchId, "your_turn", $"C'est ton tour (tour {turnNumber}) — ton adversaire a déjà joué.");
                }
                if (!notifiedP2 && p1.HasSubmittedThisTurn && !p2.HasSubmittedThisTurn)
                {
                    notifiedP2 = true;
                    yield return WriteNotification(p2.UserId, matchId, "your_turn", $"C'est ton tour (tour {turnNumber}) — ton adversaire a déjà joué.");
                }

                remaining -= Time.deltaTime;
                yield return null;
            }
        }
    }
}
