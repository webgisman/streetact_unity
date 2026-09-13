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
    /// SIMPLIFICATION ASSUMÉE : seuls position/rotation/PV/type/équipe survivent à une pause. L'état
    /// de garnison (fenêtre occupée)/embuscade/camouflage d'une unité est PERDU si un cycle pause/
    /// reprise a lieu pendant qu'elle l'utilisait — un compromis de portée, pas un oubli. Documenté
    /// dans 11-real-engine-switch-2026-09-13.md.
    /// </summary>
    public partial class MatchSessionManager
    {
        private const float AsyncPlanningSeconds = 6f * 60f * 60f; // 6 heures

        [Serializable] private class PausedUnitDto { public string unit_id; public int unit_type; public int team_id; public float x; public float y; public float z; public float ry; public int health; }
        [Serializable] private class PausedRosterDto { public PausedUnitDto[] units; }

        /// <summary>Photo de toutes les vraies UnitAI vivantes de CE match, avant destruction. Écrit
        /// directement dans matches.paused_roster_json (PATCH REST, même mécanisme que
        /// CloseMatchRecord).</summary>
        private IEnumerator PersistPausedRoster(string matchId)
        {
            var units = UnitAI.AllLivingUnits.Where(u => u != null && !u.isDead).ToList();
            var dto = new PausedRosterDto
            {
                units = units.Select(u => new PausedUnitDto
                {
                    unit_id = u.gameObject.name,
                    unit_type = (int)UnitTypeStats.InferType(u),
                    team_id = u.teamID,
                    x = u.transform.position.x,
                    y = u.transform.position.y,
                    z = u.transform.position.z,
                    ry = u.transform.eulerAngles.y,
                    health = u.health
                }).ToArray()
            };
            string json = JsonUtility.ToJson(dto);
            // Échappement minimal : le JSON de dto est déjà valide, on l'enveloppe dans un objet
            // PATCH classique {"paused_roster_json": <json>} — JsonUtility ne sait pas sérialiser un
            // objet imbriqué déjà-JSON tel quel, donc on construit la requête à la main ici plutôt que
            // de repasser par un DTO englobant.
            string patchBody = "{\"paused_roster_json\":" + json + "}";
            yield return PostgrestPatch($"/matches?id=eq.{matchId}", patchBody);
            Debug.Log($"[AsyncPause] [{matchId}] Effectif ({dto.units.Length} unité(s)) sérialisé — scène libérée pour d'autres matchs.");
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
        /// ignoré (ApplyOrdersToUnits ne retrouverait plus l'unité par son ancien nom).</summary>
        private void RespawnPausedRoster(PausedRosterDto roster)
        {
            if (roster?.units == null) return;
            foreach (var u in roster.units)
            {
                UnitAI ai = UnitSpawnerUI.Instance.SpawnUnitAt((UnitSpawnerUI.UnitType)u.unit_type, new Vector3(u.x, u.y, u.z), u.team_id, forcedName: u.unit_id, skipSafeSpawnAdjustment: true);
                if (ai == null) continue;
                ai.transform.rotation = Quaternion.Euler(0f, u.ry, 0f);
                ai.health = u.health;
                ai.isPlayerControlled = true; // voir RunMatchLive : les deux camps sont toujours de vrais joueurs ici
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
