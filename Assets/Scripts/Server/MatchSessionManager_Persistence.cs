using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
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

        /// <summary>p2 peut être null (garnison IA de conquête, voir RunConquestSkirmish) : dans ce
        /// cas aucune ligne n'est insérée pour l'équipe 2 (une IA n'a pas de user_id).</summary>
        private IEnumerator CreateMatchRecord(string matchId, PlayerConnection p1, PlayerConnection p2, string mode)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            string matchJson = "{\"id\":\"" + matchId + "\",\"status\":\"active\",\"mode\":\"" + mode + "\",\"started_at\":\"" + nowIso + "\"}";
            yield return PostgrestPost("/matches", matchJson);

            string participantsJson = p2 != null
                ? "[" +
                    "{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p1.UserId + "\",\"team_id\":1}," +
                    "{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p2.UserId + "\",\"team_id\":2}" +
                  "]"
                : "[{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p1.UserId + "\",\"team_id\":1}]";
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

        /// <summary>Comme PostgrestPost, mais avec "Prefer: resolution=merge-duplicates" pour écraser
        /// une ligne existante au lieu d'échouer en conflit — utilisé pour le heartbeat
        /// "server_instances" (ReportInstanceStatus), où chaque instance écrase sans risque sa PROPRE
        /// ligne à chaque battement. Ne PAS réutiliser pour la capture de Zones : voir CaptureZoneInDb,
        /// qui a besoin d'une écriture conditionnelle, pas d'un simple écrasement (rapport d'audit §1.1).</summary>
        private IEnumerator PostgrestUpsert(string path, string jsonBody)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "resolution=merge-duplicates,return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Upsert échoué ({path}) : {req.error} — {req.downloadHandler.text}");
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

            // Mathf.Max(0, ...) : sans plafond bas, un joueur en série de défaites pouvait voir son
            // rating calculé descendre sous 0 (voir rapport d'audit §1.9) — un score ELO négatif n'a
            // pas de sens affiché sur un classement.
            int newRating1 = Mathf.Max(0, Mathf.RoundToInt(rating1 + EloKFactor * (score1 - expected1)));
            int newRating2 = Mathf.Max(0, Mathf.RoundToInt(rating2 + EloKFactor * (score2 - expected2)));

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
