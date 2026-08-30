using UnityEngine;
#if !UNITY_SERVER
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UIElements;
using UnityEngine.Networking;
using Novgov.Auth;
using Novgov.Generation;
using Novgov.Network;
#endif

namespace Novgov.UI
{
#if !UNITY_SERVER
    /// <summary>
    /// Écran "CARTE DES ZONES" (Assets/Resources/UI/ZoneMapScreen.uxml) : boutons d'attaque
    /// Nord/Sud/Est/Ouest sur la Zone adjacente à la Zone courante du joueur (voir ZoneManager), et
    /// affichage du résultat sur ZoneResultScreen.uxml une fois la réponse du serveur reçue (voir
    /// MultiplayerMatchController.OnZoneResult) pour une capture instantanée ou un refus — le cas
    /// "combat de conquête" (garnison IA) bascule directement sur l'écran de déploiement/match
    /// normal, géré par MultiplayerMatchController lui-même.
    ///
    /// Toute la classe est sous #if !UNITY_SERVER : elle référence MultiplayerMatchController.
    /// OnZoneResult, lui-même déclaré uniquement sous ce garde (voir ce fichier), et le module UI
    /// Toolkit n'a de toute façon aucune raison de tourner sur un build Dedicated Server.
    /// </summary>
    public class ZoneMapController : MonoBehaviour
    {
        public static ZoneMapController Instance { get; private set; }

        public static ZoneMapController EnsureInstance()
        {
            if (Instance == null)
            {
                var go = new GameObject("ZoneMapController");
                DontDestroyOnLoad(go);
                go.AddComponent<ZoneMapController>();
            }
            return Instance;
        }

        private bool uiBound = false;
        private Label currentZoneLabel;
        private Label statusLabel;
        private Label resultLabel;
        private Button btnAttackNorth, btnAttackSouth, btnAttackEast, btnAttackWest;

        // Un double-tap sur un bouton d'attaque envoyait deux "join_matchmaking" successifs sur la
        // même connexion — le second restait orphelin (DrainMessages ne traite jamais ce type de
        // message hors matchmaking), sans crash mais sans utilité. Verrou simple, levé dès qu'un
        // résultat (capture/combat/refus) revient — voir HandleZoneResult et Show().
        private bool attackInFlight = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            MultiplayerMatchController.OnZoneResult += HandleZoneResult;
        }

        private void OnDisable()
        {
            MultiplayerMatchController.OnZoneResult -= HandleZoneResult;
        }

        public void Show()
        {
            BindUiOnce();
            RefreshCurrentZoneLabel();
            if (statusLabel != null) statusLabel.text = "";
            attackInFlight = false;
            UIScreenManager.Instance?.Show("ZoneMap");
            StartCoroutine(RefreshNeighborOwnership());
        }

        private void BindUiOnce()
        {
            if (uiBound) return;
            if (UIScreenManager.Instance == null) return;

            VisualElement mapRoot = UIScreenManager.Instance.GetScreen("ZoneMap");
            if (mapRoot == null) return;

            currentZoneLabel = mapRoot.Q<Label>("zone-current-label");
            statusLabel = mapRoot.Q<Label>("zone-status-label");

            btnAttackNorth = mapRoot.Q<Button>("btn-attack-north");
            btnAttackSouth = mapRoot.Q<Button>("btn-attack-south");
            btnAttackEast = mapRoot.Q<Button>("btn-attack-east");
            btnAttackWest = mapRoot.Q<Button>("btn-attack-west");
            btnAttackNorth.clicked += () => Attack(ZoneManager.EnsureInstance().AttackNorth);
            btnAttackSouth.clicked += () => Attack(ZoneManager.EnsureInstance().AttackSouth);
            btnAttackEast.clicked += () => Attack(ZoneManager.EnsureInstance().AttackEast);
            btnAttackWest.clicked += () => Attack(ZoneManager.EnsureInstance().AttackWest);
            mapRoot.Q<Button>("btn-zone-back").clicked += () => GameManagerUI.Instance?.ReturnToStartupMenu();

            VisualElement resultRoot = UIScreenManager.Instance.GetScreen("ZoneResult");
            if (resultRoot != null)
            {
                resultLabel = resultRoot.Q<Label>("zone-result-text");
                resultRoot.Q<Button>("btn-zone-result-ok").clicked += () => GameManagerUI.Instance?.ReturnToStartupMenu();
            }

            uiBound = true;
        }

        private void Attack(System.Action attackAction)
        {
            if (attackInFlight) return;
            attackInFlight = true;
            if (statusLabel != null) statusLabel.text = "Envoi de la demande...";
            attackAction();
        }

        private void RefreshCurrentZoneLabel()
        {
            if (currentZoneLabel == null) return;
            ZoneManager zm = ZoneManager.EnsureInstance();
            currentZoneLabel.text = $"Zone actuelle : ({zm.CurrentTileX},{zm.CurrentTileY})";
        }

        private void HandleZoneResult(string message)
        {
            BindUiOnce();
            if (resultLabel != null) resultLabel.text = message;
            UIScreenManager.Instance?.Show("ZoneResult");
        }

        [System.Serializable] private class NeighborZoneEntry { public int tile_x; public int tile_y; public string owner_user_id; }
        [System.Serializable] private class NeighborZoneQueryResult { public NeighborZoneEntry[] items; }

        /// <summary>Interroge le statut des 4 Zones voisines (neutre/possédée par moi/possédée par un
        /// autre joueur) et met à jour le texte des boutons en conséquence — jusqu'ici l'écran
        /// "CARTE DES ZONES" n'affichait littéralement rien de la Zone visée avant d'attaquer (juste 4
        /// boutons de boussole et des coordonnées de tuile brutes), le joueur cliquait à l'aveugle
        /// sans savoir s'il allait déclencher une capture instantanée ou un combat, ni contre quelle
        /// force (voir rapport d'audit §2.C). Reste volontairement simple (texte, pas une vraie
        /// tuile cartographique) : suffisant pour lever l'aveuglement total sans reconstruire tout
        /// l'écran en composant de carte.</summary>
        private IEnumerator RefreshNeighborOwnership()
        {
            ZoneManager zm = ZoneManager.EnsureInstance();
            int cx = zm.CurrentTileX, cy = zm.CurrentTileY;
            string myUserId = SupabaseAuthClient.CurrentSession?.user?.id;

            SetNeighborStatus(btnAttackNorth, "⬆ NORD", "…");
            SetNeighborStatus(btnAttackSouth, "⬇ SUD", "…");
            SetNeighborStatus(btnAttackEast, "➡ EST", "…");
            SetNeighborStatus(btnAttackWest, "⬅ OUEST", "…");

            (int x, int y) north = (cx, cy - 1), south = (cx, cy + 1), east = (cx + 1, cy), west = (cx - 1, cy);
            string filter =
                $"or=(and(tile_x.eq.{north.x},tile_y.eq.{north.y})," +
                $"and(tile_x.eq.{south.x},tile_y.eq.{south.y})," +
                $"and(tile_x.eq.{east.x},tile_y.eq.{east.y})," +
                $"and(tile_x.eq.{west.x},tile_y.eq.{west.y}))";
            string url = $"{SupabaseAuthClient.RestBaseUrl}/zones?zoom=eq.{CityGenerator.ZONE_ZOOM}&{filter}&select=tile_x,tile_y,owner_user_id";

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
                req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession?.access_token);
                yield return req.SendWebRequest();

                var owners = new System.Collections.Generic.Dictionary<(int, int), string>();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                        var parsed = JsonUtility.FromJson<NeighborZoneQueryResult>(wrapped);
                        if (parsed?.items != null)
                            foreach (var e in parsed.items) owners[(e.tile_x, e.tile_y)] = e.owner_user_id;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[ZoneMapController] Parsing du statut des Zones voisines échoué : {ex.Message}");
                    }
                }

                SetNeighborStatus(btnAttackNorth, "⬆ NORD", DescribeOwner(owners, north, myUserId));
                SetNeighborStatus(btnAttackSouth, "⬇ SUD", DescribeOwner(owners, south, myUserId));
                SetNeighborStatus(btnAttackEast, "➡ EST", DescribeOwner(owners, east, myUserId));
                SetNeighborStatus(btnAttackWest, "⬅ OUEST", DescribeOwner(owners, west, myUserId));
            }
        }

        private static string DescribeOwner(System.Collections.Generic.Dictionary<(int, int), string> owners, (int x, int y) tile, string myUserId)
        {
            if (!owners.TryGetValue(tile, out string owner) || string.IsNullOrEmpty(owner)) return "Neutre";
            return owner == myUserId ? "À vous" : "Ennemi";
        }

        private static void SetNeighborStatus(Button btn, string label, string status)
        {
            if (btn != null) btn.text = $"{label}\n({status})";
        }
    }
#endif
}
