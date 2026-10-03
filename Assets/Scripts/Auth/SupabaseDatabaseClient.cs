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
        // 2026-09-13 : action_points est maintenant une VRAIE colonne (schema.sql §10), plus jamais
        // écrasée par une valeur locale (PlayerPrefs) — voir "Que reste-t-il de faux ?" en bas de ce
        // fichier pour l'historique de ce qui a changé. profiles.action_points est verrouillée en
        // écriture directe (le grant large de bootstrap-db.sh a été retiré par le même revoke que
        // "username"/"rating") : seules les fonctions buy_unit()/claim_daily_bonus() (SECURITY
        // DEFINER, voir schema.sql) peuvent la modifier, jamais un PATCH direct du client.
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
                if (arr != null && arr.Length > 0) return (true, arr[0]);
            }

            // Repli MINIMAL si le réseau est instable (jamais un vrai substitut à la ligne réelle) :
            // ne PAS inventer un solde d'AP ici (200/150 arbitraires comme avant) — 0 est le seul
            // repli honnête tant que la vraie valeur serveur n'a pas pu être lue.
            var fallback = new PlayerProfile
            {
                id = userId,
                username = SupabaseAuthClient.CurrentSession.user.email?.Split('@')[0] ?? "Commandant",
                rating = 1000,
                action_points = 0
            };
            return (true, fallback);
        }

        public static async Task<bool> UpdateProfile(string username)
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null) return false;
            if (string.IsNullOrEmpty(username)) return true;

            string userId = SupabaseAuthClient.CurrentSession.user.id;
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

        [Serializable] private class DailyBonusEntry { public int new_action_points; public bool already_claimed; }

        /// <summary>Appelle la fonction Postgres claim_daily_bonus() (schema.sql §10) — +50 AP une
        /// fois par jour UTC, suivi par un vrai horodatage serveur (profiles.last_daily_bonus_at),
        /// pas un PlayerPrefs local qu'une réinstallation remettrait à zéro.</summary>
        public static async Task<(bool ok, int newActionPoints, bool alreadyClaimed)> ClaimDailyBonus()
        {
            if (SupabaseAuthClient.CurrentSession == null) return (false, 0, false);
            string url = $"{SupabaseAuthClient.RestBaseUrl}/rpc/claim_daily_bonus";
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
            req.downloadHandler = new DownloadHandlerBuffer();
            SetupHeaders(req);
            req.SetRequestHeader("Content-Type", "application/json");
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) return (false, 0, false);

            var arr = JsonHelper.FromJson<DailyBonusEntry>(req.downloadHandler.text);
            if (arr == null || arr.Length == 0) return (false, 0, false);
            return (true, arr[0].new_action_points, arr[0].already_claimed);
        }

        // --- ROSTER (Caserne d'unités) — VRAIE table partagée (schema.sql §10, public.player_roster),
        // remplace l'ancienne version PlayerPrefs locale (2026-09-13). Le client ne peut plus écrire
        // directement dedans (RLS default-deny) — un achat passe par BuyUnit()/la fonction buy_unit(),
        // jamais un PATCH/POST direct.
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

        public static async Task<(bool ok, PlayerRosterItem[] roster)> GetRoster()
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return (false, new PlayerRosterItem[0]);

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            string url = $"{SupabaseAuthClient.RestBaseUrl}/player_roster?user_id=eq.{userId}&select=user_id,unit_type,quantity";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                return (false, CurrentRoster ?? new PlayerRosterItem[0]);

            CurrentRoster = JsonHelper.FromJson<PlayerRosterItem>(req.downloadHandler.text);
            return (true, CurrentRoster);
        }

        [Serializable] private class BuyUnitEntry { public int new_action_points; public int new_quantity; }

        /// <summary>Achète UNE unité de <paramref name="unitType"/> via la fonction Postgres
        /// buy_unit() (schema.sql §10) — remplace UpsertRosterItem (PlayerPrefs local, 2026-09-13).
        /// Le coût est déterminé SERVEUR (jamais envoyé par ce client) ; échoue proprement (ok=false)
        /// si les Points d'Action sont insuffisants ou le type inconnu.</summary>
        public static async Task<(bool ok, int newActionPoints, int newQuantity, string error)> BuyUnit(string unitType)
        {
            if (SupabaseAuthClient.CurrentSession == null) return (false, 0, 0, "Non connecté");

            string url = $"{SupabaseAuthClient.RestBaseUrl}/rpc/buy_unit";
            string json = "{\"p_unit_type\":\"" + unitType.Replace("\"", "\\\"") + "\"}";
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            SetupHeaders(req);
            req.SetRequestHeader("Content-Type", "application/json");
            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                // PostgREST renvoie le message de la fonction (raise exception) dans le corps —
                // c'est ce message ("Points d'action insuffisants...") qu'on veut montrer au joueur,
                // pas un code HTTP brut.
                string body = req.downloadHandler.text;
                return (false, 0, 0, string.IsNullOrEmpty(body) ? req.error : body);
            }

            var arr = JsonHelper.FromJson<BuyUnitEntry>(req.downloadHandler.text);
            if (arr == null || arr.Length == 0) return (false, 0, 0, "Réponse inattendue");
            return (true, arr[0].new_action_points, arr[0].new_quantity, null);
        }

        // --- ZONES (territoires de Conquête) — VRAIE table partagée (schema.sql §6/§10), déjà
        // utilisée par le serveur (MatchSessionManager_Conquest.CaptureZoneInDb/EnsureHqBuildingIndex) ;
        // ce client la lit directement (policy SELECT "visible par tous les joueurs authentifiés").
        // Remplace l'ancien PlayerBuilding/GetBuildings/ClaimBuilding (PlayerPrefs local, 2026-09-13) —
        // il n'y a plus besoin de "réclamer" un bâtiment séparément : la Zone EST déjà la source de
        // vérité de propriété, et son bâtiment HQ est désigné une fois par le serveur.
        [Serializable]
        public class ZoneInfo
        {
            public int tile_x;
            public int tile_y;
            public string owner_user_id;
            public int hq_building_index = -1;
            // 2026-09-13 (schema.sql §11) : niveau d'investissement du propriétaire (1-3, voir
            // UpgradeBuilding) et fin de bouclier de grâce après un siège résolu (ISO 8601, vide/null
            // = pas de bouclier actif).
            public int building_level = 1;
            public string shield_until;
        }

        private const string ZoneInfoSelect = "tile_x,tile_y,owner_user_id,hq_building_index,building_level,shield_until";

        public static async Task<(bool ok, ZoneInfo zone)> GetZoneInfo(int tileX, int tileY, int zoom)
        {
            string url = $"{SupabaseAuthClient.RestBaseUrl}/zones?tile_x=eq.{tileX}&tile_y=eq.{tileY}&zoom=eq.{zoom}&select={ZoneInfoSelect}";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) return (false, null);

            var arr = JsonHelper.FromJson<ZoneInfo>(req.downloadHandler.text);
            return (arr != null && arr.Length > 0) ? (true, arr[0]) : (false, null);
        }

        [Serializable] private class UsernameEntry { public string id; public string username; }

        /// <summary>Pseudos de plusieurs joueurs (profiles est lisible par tout joueur authentifié) —
        /// pour dire "en attente de Bob" / "tenu par Bob" plutôt que "l'autre joueur" (2026-10-03).
        /// Les identifiants introuvables sont simplement absents du dictionnaire.</summary>
        public static async Task<Dictionary<string, string>> GetUsernames(IEnumerable<string> userIds)
        {
            var result = new Dictionary<string, string>();
            var ids = new HashSet<string>(userIds ?? Array.Empty<string>());
            ids.RemoveWhere(string.IsNullOrEmpty);
            if (ids.Count == 0) return result;

            string url = $"{SupabaseAuthClient.RestBaseUrl}/profiles?id=in.({string.Join(",", ids)})&select=id,username";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) return result;

            var arr = JsonHelper.FromJson<UsernameEntry>(req.downloadHandler.text);
            if (arr != null) foreach (var e in arr) if (!string.IsNullOrEmpty(e.id)) result[e.id] = e.username;
            return result;
        }

        /// <summary>Toutes les Zones possédées par le joueur connecté — pour l'écran "Territoires".</summary>
        public static async Task<(bool ok, ZoneInfo[] zones)> GetOwnedZones()
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return (false, new ZoneInfo[0]);

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            string url = $"{SupabaseAuthClient.RestBaseUrl}/zones?owner_user_id=eq.{userId}&select={ZoneInfoSelect}";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) return (false, new ZoneInfo[0]);

            return (true, JsonHelper.FromJson<ZoneInfo>(req.downloadHandler.text));
        }

        [Serializable] private class SiegeStartEntry { public long new_siege_id; public string siege_deadline; }

        /// <summary>Déclare un siège sur une Zone déjà possédée par un autre joueur (schema.sql §11,
        /// fonction start_siege) — vérifie bouclier/siège déjà en cours SERVEUR-SIDE (SECURITY
        /// DEFINER), écrit la notification au défenseur. N'ouvre PAS encore la connexion de jeu :
        /// voir Novgov.Network.MultiplayerMatchController.SiegeZone, appelé seulement si ok=true.</summary>
        public static async Task<(bool ok, long siegeId, string error)> StartSiege(int tileX, int tileY, int zoom)
        {
            if (SupabaseAuthClient.CurrentSession == null) return (false, 0, "Non connecté");

            string url = $"{SupabaseAuthClient.RestBaseUrl}/rpc/start_siege";
            string json = "{\"p_tile_x\":" + tileX + ",\"p_tile_y\":" + tileY + ",\"p_zoom\":" + zoom + "}";
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            SetupHeaders(req);
            req.SetRequestHeader("Content-Type", "application/json");
            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string body = req.downloadHandler.text;
                return (false, 0, string.IsNullOrEmpty(body) ? req.error : body);
            }

            var arr = JsonHelper.FromJson<SiegeStartEntry>(req.downloadHandler.text);
            if (arr == null || arr.Length == 0) return (false, 0, "Réponse inattendue");
            return (true, arr[0].new_siege_id, null);
        }

        [Serializable] private class UpgradeBuildingEntry { public int new_action_points; public int new_level; }

        /// <summary>Améliore le bâtiment de la Zone (niveau 1->2->3, schema.sql §11, fonction
        /// upgrade_building) — coût croissant en AP, vérifié/déduit atomiquement côté serveur.</summary>
        public static async Task<(bool ok, int newActionPoints, int newLevel, string error)> UpgradeBuilding(int tileX, int tileY, int zoom)
        {
            if (SupabaseAuthClient.CurrentSession == null) return (false, 0, 0, "Non connecté");

            string url = $"{SupabaseAuthClient.RestBaseUrl}/rpc/upgrade_building";
            string json = "{\"p_tile_x\":" + tileX + ",\"p_tile_y\":" + tileY + ",\"p_zoom\":" + zoom + "}";
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            SetupHeaders(req);
            req.SetRequestHeader("Content-Type", "application/json");
            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string body = req.downloadHandler.text;
                return (false, 0, 0, string.IsNullOrEmpty(body) ? req.error : body);
            }

            var arr = JsonHelper.FromJson<UpgradeBuildingEntry>(req.downloadHandler.text);
            if (arr == null || arr.Length == 0) return (false, 0, 0, "Réponse inattendue");
            return (true, arr[0].new_action_points, arr[0].new_level, null);
        }

        [Serializable]
        public class SiegeInfo
        {
            public long id;
            public int tile_x;
            public int tile_y;
            public string attacker_user_id;
            public string defender_user_id;
            public string status;
            public string deadline;
        }

        /// <summary>Sièges "pending" où le joueur connecté est attaquant OU défenseur — la policy RLS
        /// (schema.sql §11) filtre déjà à ses propres lignes, pas besoin d'un filtre explicite ici.</summary>
        public static async Task<(bool ok, SiegeInfo[] sieges)> GetMySieges()
        {
            if (SupabaseAuthClient.CurrentSession == null) return (false, new SiegeInfo[0]);

            string url = $"{SupabaseAuthClient.RestBaseUrl}/zone_sieges?status=eq.pending&select=id,tile_x,tile_y,attacker_user_id,defender_user_id,status,deadline&order=deadline.asc";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) return (false, new SiegeInfo[0]);

            return (true, JsonHelper.FromJson<SiegeInfo>(req.downloadHandler.text));
        }

        // --- NOTIFICATIONS (schema.sql §9) — écrites UNIQUEMENT par le serveur de jeu (connexion
        // Postgres directe) ; ce client ne fait que lire les siennes et marquer read_at (seule
        // colonne grantée en écriture, voir revoke/grant ci-dessus côté schema.sql). Pas de push
        // mobile : lu à l'ouverture de l'application, comme demandé explicitement ("le joueur qui
        // lance de temps en temps son appli pour voir la notif").
        [Serializable]
        public class NotificationInfo
        {
            public long id;
            public string match_id;
            public string type;
            public string message;
            public string read_at;
            public string created_at;
        }

        /// <summary>Notifications non lues du joueur connecté, les plus récentes d'abord.</summary>
        public static async Task<(bool ok, NotificationInfo[] notifications)> GetUnreadNotifications()
        {
            if (SupabaseAuthClient.CurrentSession == null || SupabaseAuthClient.CurrentSession.user == null)
                return (false, new NotificationInfo[0]);

            string userId = SupabaseAuthClient.CurrentSession.user.id;
            string url = $"{SupabaseAuthClient.RestBaseUrl}/notifications?user_id=eq.{userId}&read_at=is.null&order=created_at.desc&select=id,match_id,type,message,read_at,created_at";
            using var req = UnityWebRequest.Get(url);
            SetupHeaders(req);
            await req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) return (false, new NotificationInfo[0]);

            return (true, JsonHelper.FromJson<NotificationInfo>(req.downloadHandler.text));
        }

        /// <summary>Marque une notification comme lue (seule écriture permise sur cette table côté
        /// client — voir "grant update (read_at)" dans schema.sql).</summary>
        public static async Task<bool> MarkNotificationRead(long notificationId)
        {
            if (SupabaseAuthClient.CurrentSession == null) return false;

            string url = $"{SupabaseAuthClient.RestBaseUrl}/notifications?id=eq.{notificationId}";
            string json = "{\"read_at\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
            using var req = new UnityWebRequest(url, "PATCH");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            SetupHeaders(req);
            req.SetRequestHeader("Content-Type", "application/json");
            await req.SendWebRequest();
            return req.result == UnityWebRequest.Result.Success;
        }
    }
}
