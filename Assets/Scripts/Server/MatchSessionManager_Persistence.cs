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

        /// <summary>Enregistre la bataille et ses deux joueurs (équipe 1 = attaquant, 2 = défenseur).</summary>
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

            int delta1 = Mathf.RoundToInt(EloKFactor * (score1 - expected1));
            int delta2 = Mathf.RoundToInt(EloKFactor * (score2 - expected2));

            yield return ApplyRatingDeltaWithRetry(p1, delta1);
            yield return ApplyRatingDeltaWithRetry(p2, delta2);

            Debug.Log($"[MatchSessionManager] Ratings mis à jour : {p1.UserId} ->{p1.NewRating} ({(p1.RatingDelta >= 0 ? "+" : "")}{p1.RatingDelta}), {p2.UserId} ->{p2.NewRating} ({(p2.RatingDelta >= 0 ? "+" : "")}{p2.RatingDelta})");
        }

        private const int RatingUpdateMaxAttempts = 5;

        /// <summary>Applique <paramref name="delta"/> au rating ACTUEL de <paramref name="player"/>
        /// avec verrouillage optimiste (lecture fraîche + écriture conditionnée sur cette même
        /// valeur, voir PostgrestPatchChecked/CaptureZoneInDb pour le même principe déjà utilisé pour
        /// zones.owner_user_id) — sans ça, deux parties de ce même joueur qui se terminent à
        /// quelques centaines de ms d'écart sur deux instances différentes (une capture et une
        /// bataille de siège, rien ne l'empêche) pouvaient voir la seconde écriture écraser la première avec un rating
        /// déjà obsolète, perdant silencieusement un delta de classement (rapport d'audit §4).
        /// Utilisé à la fois ici (ELO complet) et par ApplyConquestRatingDelta (delta fixe).</summary>
        private IEnumerator ApplyRatingDeltaWithRetry(PlayerConnection player, int delta)
        {
            for (int attempt = 0; attempt < RatingUpdateMaxAttempts; attempt++)
            {
                int currentRating = 1000;
                bool readOk = false;
                using (var req = UnityWebRequest.Get($"{GameServerBootstrap.RestUrl}/profiles?id=eq.{player.UserId}&select=rating"))
                {
                    req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
                    req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                            var parsed = JsonUtility.FromJson<RatingQueryResult>(wrapped);
                            if (parsed?.items != null && parsed.items.Length > 0) { currentRating = parsed.items[0].rating; readOk = true; }
                        }
                        catch (Exception ex) { Debug.LogWarning($"[MatchSessionManager] Parsing rating ({player.UserId}) échoué : {ex.Message}"); }
                    }
                    else Debug.LogWarning($"[MatchSessionManager] Lecture rating ({player.UserId}) échouée : {req.error}");
                }
                if (!readOk) yield break; // panne réseau : on abandonne plutôt que de boucler sur une IHM absente

                // Mathf.Max(0, ...) : sans plafond bas, un joueur en série de défaites pouvait voir
                // son rating calculé descendre sous 0 (rapport d'audit §1.9) — un score négatif n'a
                // pas de sens affiché sur un classement.
                int newRating = Mathf.Max(0, currentRating + delta);
                bool applied = false;
                yield return PostgrestPatchChecked($"/profiles?id=eq.{player.UserId}&rating=eq.{currentRating}", "{\"rating\":" + newRating + "}", ok => applied = ok);
                if (applied)
                {
                    player.NewRating = newRating;
                    player.RatingDelta = newRating - currentRating;
                    yield break;
                }
                // Le rating a changé entre notre lecture et notre écriture (une autre partie de ce
                // même joueur vient de se terminer ailleurs) : on relit la valeur fraîche et retente
                // plutôt que d'écraser en aveugle.
            }
            Debug.LogWarning($"[MatchSessionManager] Mise à jour du rating de {player.UserId} abandonnée après {RatingUpdateMaxAttempts} tentatives (contention répétée).");
        }

        [Serializable] private class RatingEntry { public string id; public int rating; }
        [Serializable] private class RatingQueryResult { public RatingEntry[] items; }
    }
}
