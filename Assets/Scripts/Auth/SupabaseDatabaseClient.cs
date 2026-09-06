using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Novgov.Auth
{
    [Serializable]
    public class PlayerProfile
    {
        public string id;
        public string username;
        public int rating = 1000;
        public int action_points = 100;
        public string last_daily_reward_at;
        public string created_at;
    }

    [Serializable]
    public class PlayerRosterItem
    {
        public string user_id;
        public string unit_type;
        public int quantity;
    }

    [Serializable]
    public class PlayerBuilding
    {
        public string zone_id;
        public string user_id;
        public int building_index;
        public string captured_at;
        public string last_collection_at;
    }

    // Wrap list arrays for JsonUtility (JsonUtility cannot deserialize raw arrays directly if not wrapped)
    public static class JsonHelper
    {
        public static T[] FromJson<T>(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "[]") return new T[0];
            string newJson = "{ \"array\": " + json + "}";
            Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(newJson);
            return wrapper != null ? wrapper.array : new T[0];
        }

        [Serializable]
        private class Wrapper<T>
        {
            public T[] array;
        }
    }

    /// <summary>
    /// Client minimal pour l'API Postgrest de Supabase, pour le profil et le méta-jeu.
    /// </summary>
    public static class SupabaseDatabaseClient
    {
        private static void SetupHeaders(UnityWebRequest req)
        {
            req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
            if (SupabaseAuthClient.CurrentSession != null && !string.IsNullOrEmpty(SupabaseAuthClient.CurrentSession.access_token))
                req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.access_token);
        }

        // --- PROFILES ---
        public static async Task<(bool ok, PlayerProfile profile)> GetProfile()
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return (false, null);

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            string url = $"{SupabaseAuthClient.RestBaseUrl}/profiles?id=eq.{userId}";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var arr = JsonHelper.FromJson<PlayerProfile>(req.downloadHandler.text);
                if (arr != null && arr.Length > 0)
                {
                    var p = arr[0];
                    p.action_points = GetActionPoints(userId);
                    return (true, p);
                }
            }

            // Fallback profil local si réseau instable
            var fallback = new PlayerProfile
            {
                id = userId,
                username = SupabaseAuthClient.CurrentSession.user.email?.Split('@')[0] ?? "Commandant",
                rating = 1000,
                action_points = GetActionPoints(userId)
            };
            return (true, fallback);
        }

        public static async Task<bool> UpdateProfile(string username, int ap)
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null) return false;
            string userId = SupabaseAuthClient.CurrentSession.user.id;
            SetActionPoints(userId, ap);

            if (!string.IsNullOrEmpty(username))
            {
                string url = $"{SupabaseAuthClient.RestBaseUrl}/profiles?id=eq.{userId}";
                string escapedUsername = username.Replace("\"", "\\\"");
                string json = $"{{\"username\": \"{escapedUsername}\"}}";
                using var req = new UnityWebRequest(url, "PATCH");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                SetupHeaders(req);
                req.SetRequestHeader("Content-Type", "application/json");
                await req.SendWebRequest();
                return req.result == UnityWebRequest.Result.Success;
            }
            return true;
        }

        // --- ACTION POINTS (Gestion robuste locale persistée) ---
        public static int GetActionPoints(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return 100;
            return PlayerPrefs.GetInt($"Novgov_AP_{userId}", 150);
        }

        public static void SetActionPoints(string userId, int ap)
        {
            if (string.IsNullOrEmpty(userId)) return;
            PlayerPrefs.SetInt($"Novgov_AP_{userId}", Mathf.Max(0, ap));
            PlayerPrefs.Save();
        }

        public static void AddActionPoints(string userId, int delta)
        {
            int current = GetActionPoints(userId);
            SetActionPoints(userId, current + delta);
        }

        // --- ROSTER (Caserne d'unités) ---
        public static PlayerRosterItem[] CurrentRoster { get; private set; }

        // Source UNIQUE des types d'unité connus de la caserne (correctif 2026-09-06) : ce tableau
        // était dupliqué ICI et dans MultiplayerMatchController.RefreshRosterScreen, avec des noms
        // ("Canon", "Char") qui ne correspondent à AUCUNE valeur réelle de UnitSpawnerUI.UnitType
        // (les vraies valeurs sont "VehiculeCanon"/"CharLeopard", voir l'énum). La comparaison
        // `item.unit_type.Equals(type.ToString())` faite au déploiement (UnitSpawnerUI.StartPlacingUnit)
        // ne matchait donc JAMAIS pour un Canon ou un Char : `owned` restait à 0 pour ces deux types
        // pour toujours, même une fois la caserne correctement chargée et même après un achat "Recruter"
        // — ces deux unités devenaient indéfiniment indéployables en multijoueur ("Vous ne possédez pas
        // d'unité ... supplémentaire dans votre caserne !"), ce qui pouvait se lire comme "la sélection
        // des unités est cassée". "Drone" n'a PAS d'équivalent dans UnitType : conservé tel quel, c'est
        // un type prévu mais pas encore implémenté comme unité déployable — inoffensif (n'entre jamais
        // en conflit avec `sourceUnitType`, juste un slot de la boutique actuellement sans usage).
        public static readonly string[] KnownUnitTypes = { "Fantassin", "VehiculeCanon", "CharLeopard", "Mortier", "Drone" };
        public static readonly int[] KnownUnitCosts = { 50, 150, 300, 400, 200 }; // même ordre que KnownUnitTypes

        public static Task<(bool ok, PlayerRosterItem[] roster)> GetRoster()
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return Task.FromResult((false, new PlayerRosterItem[0]));

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            var list = new List<PlayerRosterItem>();

            foreach (var u in KnownUnitTypes)
            {
                int defaultQty = (u == "Fantassin") ? 4 : (u == "VehiculeCanon" || u == "CharLeopard") ? 1 : 0;
                int qty = PlayerPrefs.GetInt($"Novgov_Roster_{userId}_{u}", defaultQty);
                list.Add(new PlayerRosterItem { user_id = userId, unit_type = u, quantity = qty });
            }

            CurrentRoster = list.ToArray();
            return Task.FromResult((true, CurrentRoster));
        }

        public static Task<bool> UpsertRosterItem(string unitType, int quantity)
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return Task.FromResult(false);

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            PlayerPrefs.SetInt($"Novgov_Roster_{userId}_{unitType}", quantity);
            PlayerPrefs.Save();
            return Task.FromResult(true);
        }

        // --- BUILDINGS (Territoires & Conquête) ---
        public static Task<(bool ok, PlayerBuilding[] buildings)> GetBuildings()
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return Task.FromResult((false, new PlayerBuilding[0]));

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            string json = PlayerPrefs.GetString($"Novgov_Buildings_{userId}", "[]");
            var arr = JsonHelper.FromJson<PlayerBuilding>(json);
            return Task.FromResult((true, arr));
        }

        public static Task<bool> ClaimBuilding(string zoneId, int buildingIndex)
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return Task.FromResult(false);

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            string json = PlayerPrefs.GetString($"Novgov_Buildings_{userId}", "[]");
            var current = new List<PlayerBuilding>(JsonHelper.FromJson<PlayerBuilding>(json));

            if (!current.Exists(b => b.zone_id == zoneId && b.building_index == buildingIndex))
            {
                current.Add(new PlayerBuilding
                {
                    zone_id = zoneId,
                    user_id = userId,
                    building_index = buildingIndex,
                    captured_at = DateTime.UtcNow.ToString("o")
                });
                string updatedJson = JsonUtility.ToJson(new BuildingListWrapper { items = current.ToArray() });
                // Extraire le tableau interne
                int arrayStart = updatedJson.IndexOf('[');
                int arrayEnd = updatedJson.LastIndexOf(']');
                if (arrayStart >= 0 && arrayEnd >= arrayStart)
                {
                    PlayerPrefs.SetString($"Novgov_Buildings_{userId}", updatedJson.Substring(arrayStart, arrayEnd - arrayStart + 1));
                    PlayerPrefs.Save();
                }
            }
            return Task.FromResult(true);
        }

        public static async Task<(bool ok, PlayerBuilding building)> GetBuilding(string zoneId)
        {
            var (ok, list) = await GetBuildings();
            if (ok && list != null)
            {
                var found = Array.Find(list, b => b.zone_id == zoneId);
                if (found != null) return (true, found);
            }
            return (false, null);
        }

        [Serializable]
        private class BuildingListWrapper { public PlayerBuilding[] items; }
    }

    public static class UnityWebRequestExtensions
    {
        public static Task<UnityWebRequest> SendWebRequestAsync(this UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            var operation = request.SendWebRequest();
            operation.completed += (asyncOperation) => tcs.TrySetResult(request);
            return tcs.Task;
        }
        
        public static System.Runtime.CompilerServices.TaskAwaiter<UnityWebRequest> GetAwaiter(this UnityWebRequestAsyncOperation op)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            op.completed += _ => tcs.TrySetResult(op.webRequest);
            return tcs.Task.GetAwaiter();
        }
    }
}
