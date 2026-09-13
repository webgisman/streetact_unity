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
        private Label enemyAlertLabel;
        private Button btnAttackNorth, btnAttackSouth, btnAttackEast, btnAttackWest;

        // Un double-tap sur un bouton d'attaque envoyait deux "join_matchmaking" successifs sur la
        // même connexion — le second restait orphelin (DrainMessages ne traite jamais ce type de
        // message hors matchmaking), sans crash mais sans utilité. Verrou simple, levé dès qu'un
        // résultat (capture/combat/refus) revient — voir HandleZoneResult et Show().
        private bool attackInFlight = false;

        // Statut des Zones voisines, rafraîchi par RefreshNeighborOwnership — conservé en champs
        // (2026-09-13) pour que les clics sur les boutons de direction (liés UNE SEULE FOIS dans
        // BindUiOnce) puissent réagir au statut ACTUEL de la Zone visée (neutre/mienne/ennemie +
        // bouclier) plutôt qu'à un flux d'attaque fixe décidé une fois pour toutes au chargement.
        private readonly Dictionary<(int, int), string> lastKnownOwners = new Dictionary<(int, int), string>();
        private readonly Dictionary<(int, int), string> lastKnownShields = new Dictionary<(int, int), string>();
        // Vrai dès qu'un premier RefreshNeighborOwnership a réussi — une tuile ABSENTE de
        // lastKnownOwners après ça signifie "Zone neutre, jamais capturée" (aucune ligne "zones"),
        // PAS "statut inconnu" (voir OnDirectionClicked/DescribeOwner, même convention).
        private bool neighborStatusLoaded = false;

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
            neighborStatusLoaded = false;
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
            enemyAlertLabel = mapRoot.Q<Label>("enemy-alert-label");

            btnAttackNorth = mapRoot.Q<Button>("btn-attack-north");
            btnAttackSouth = mapRoot.Q<Button>("btn-attack-south");
            btnAttackEast = mapRoot.Q<Button>("btn-attack-east");
            btnAttackWest = mapRoot.Q<Button>("btn-attack-west");
            // 2026-09-13 : ne branche plus directement sur ZoneManager.AttackX (capture instantanée
            // fixe) — une Zone ennemie passe maintenant par un siège (OnDirectionClicked décide au
            // moment du clic, selon le statut ACTUEL de la Zone visée, voir lastKnownOwners/Shields).
            btnAttackNorth.clicked += () => OnDirectionClicked(0, -1);
            btnAttackSouth.clicked += () => OnDirectionClicked(0, 1);
            btnAttackEast.clicked += () => OnDirectionClicked(1, 0);
            btnAttackWest.clicked += () => OnDirectionClicked(-1, 0);
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

        /// <summary>Décide, au moment du clic, quoi faire de la Zone dans cette direction — capture
        /// instantanée (neutre, inchangé), aucune action (déjà à vous), ou déclaration d'un SIÈGE
        /// (ennemie, 2026-09-13) au lieu de l'ancien combat instantané contre une garnison IA. Le
        /// statut vient de lastKnownOwners/lastKnownShields, remplis par le dernier
        /// RefreshNeighborOwnership — un statut pas encore connu (juste après Show()) refuse l'action
        /// plutôt que de risquer une décision sur une donnée périmée/absente.</summary>
        private void OnDirectionClicked(int dx, int dy)
        {
            if (attackInFlight) return;
            if (!neighborStatusLoaded)
            {
                if (statusLabel != null) statusLabel.text = "Statut des Zones pas encore connu — patientez.";
                return;
            }

            ZoneManager zm = ZoneManager.EnsureInstance();
            var tile = (zm.CurrentTileX + dx, zm.CurrentTileY + dy);
            string myUserId = SupabaseAuthClient.CurrentSession?.user?.id;

            // Absente de lastKnownOwners == Zone neutre (jamais capturée, aucune ligne "zones") —
            // même convention que DescribeOwner ci-dessous, pas "statut inconnu" (voir
            // neighborStatusLoaded juste au-dessus, qui couvre déjà ce cas).
            lastKnownOwners.TryGetValue(tile, out string owner);

            if (string.IsNullOrEmpty(owner))
            {
                // Neutre : capture instantanée inchangée (voir MatchSessionManager.RunConquestRequest).
                Attack(() => MultiplayerMatchController.EnsureInstance().AttackZone(tile.Item1, tile.Item2));
                return;
            }

            if (owner == myUserId)
            {
                if (statusLabel != null) statusLabel.text = "Cette Zone vous appartient déjà.";
                return;
            }

            if (lastKnownShields.TryGetValue(tile, out string shieldUntilIso) && !string.IsNullOrEmpty(shieldUntilIso)
                && System.DateTime.TryParse(shieldUntilIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out System.DateTime shieldUntil)
                && shieldUntil > System.DateTime.UtcNow)
            {
                var remaining = shieldUntil - System.DateTime.UtcNow;
                if (statusLabel != null) statusLabel.text = $"Zone protégée encore {remaining.Hours}h{remaining.Minutes:D2} — impossible d'assiéger pour l'instant.";
                return;
            }

            StartCoroutine(StartSiegeThenDeploy(tile.Item1, tile.Item2));
        }

        /// <summary>Déclare le siège (RPC start_siege, voir schema.sql §11) puis, seulement en cas de
        /// succès, connecte l'attaquant pour son déploiement (SiegeZone) — jamais l'inverse : un
        /// déploiement ne doit jamais démarrer pour un siège qui n'a en fait pas pu être créé
        /// (bouclier posé entre-temps par une autre attaque, ou déjà un siège en cours sur cette
        /// Zone).</summary>
        private IEnumerator StartSiegeThenDeploy(int tileX, int tileY)
        {
            attackInFlight = true;
            if (statusLabel != null) statusLabel.text = "Déclaration du siège...";

            var task = SupabaseDatabaseClient.StartSiege(tileX, tileY, CityGenerator.ZONE_ZOOM);
            while (!task.IsCompleted) yield return null;
            var (ok, siegeId, error) = task.Result;

            if (!ok)
            {
                attackInFlight = false;
                if (statusLabel != null) statusLabel.text = $"Siège impossible : {error}";
                yield break;
            }

            MultiplayerMatchController.EnsureInstance().SiegeZone(tileX, tileY, siegeId);
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

        [System.Serializable] private class NeighborZoneEntry { public int tile_x; public int tile_y; public string owner_user_id; public string shield_until; }
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
            string filter = $"tile_x=gte.{cx - 1}&tile_x=lte.{cx + 1}&tile_y=gte.{cy - 1}&tile_y=lte.{cy + 1}";
            string url = $"{SupabaseAuthClient.RestBaseUrl}/zones?zoom=eq.{CityGenerator.ZONE_ZOOM}&{filter}&select=tile_x,tile_y,owner_user_id,shield_until";

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
                req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession?.access_token);
                yield return req.SendWebRequest();

                var owners = new System.Collections.Generic.Dictionary<(int, int), string>();
                lastKnownOwners.Clear();
                lastKnownShields.Clear();
                // Distingue "requête réussie" de "échec réseau/parsing" : auparavant un échec
                // laissait le dictionnaire vide sans le signaler, et DescribeOwner affichait alors
                // silencieusement "Neutre" pour les 4 Zones (aucune entrée trouvée = statut par
                // défaut) — une fausse information plutôt qu'une erreur visible (voir rapport
                // d'audit interface, défaut bloquant #3).
                bool ok = req.result == UnityWebRequest.Result.Success;
                if (ok)
                {
                    try
                    {
                        string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                        var parsed = JsonUtility.FromJson<NeighborZoneQueryResult>(wrapped);
                        if (parsed?.items != null)
                        {
                            foreach (var e in parsed.items)
                            {
                                owners[(e.tile_x, e.tile_y)] = e.owner_user_id;
                                lastKnownOwners[(e.tile_x, e.tile_y)] = e.owner_user_id;
                                lastKnownShields[(e.tile_x, e.tile_y)] = e.shield_until;
                            }
                        }
                        neighborStatusLoaded = true;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[ZoneMapController] Parsing du statut des Zones voisines échoué : {ex.Message}");
                        ok = false;
                    }
                }

                if (!ok)
                {
                    Debug.LogWarning($"[ZoneMapController] Statut des Zones voisines indisponible : {req.error}");
                    if (statusLabel != null) statusLabel.text = "Statut des Zones indisponible (erreur réseau). Réessayez.";
                    SetNeighborStatus(btnAttackNorth, "⬆ NORD", "?", "btn-zone-neutral");
                    SetNeighborStatus(btnAttackSouth, "⬇ SUD", "?", "btn-zone-neutral");
                    SetNeighborStatus(btnAttackEast, "➡ EST", "?", "btn-zone-neutral");
                    SetNeighborStatus(btnAttackWest, "⬅ OUEST", "?", "btn-zone-neutral");
                    yield break;
                }

                ApplyNeighborStatus(btnAttackNorth, "⬆ ATTAQUER NORD", owners, north, myUserId);
                ApplyNeighborStatus(btnAttackSouth, "⬇ ATTAQUER SUD", owners, south, myUserId);
                ApplyNeighborStatus(btnAttackEast, "➡ ATTAQUER EST", owners, east, myUserId);
                ApplyNeighborStatus(btnAttackWest, "⬅ ATTAQUER OUEST", owners, west, myUserId);

                // Minimap cells (huit voisines, "cell-c" au centre reste juste "VOUS" — voir UXML)
                bool anyEnemyNearby = false;
                anyEnemyNearby |= UpdateMiniMapCell("cell-nw", (cx - 1, cy - 1), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-n", (cx, cy - 1), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-ne", (cx + 1, cy - 1), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-w", (cx - 1, cy), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-e", (cx + 1, cy), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-sw", (cx - 1, cy + 1), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-s", (cx, cy + 1), owners, myUserId);
                anyEnemyNearby |= UpdateMiniMapCell("cell-se", (cx + 1, cy + 1), owners, myUserId);

                // Bannière "ENNEMI DÉTECTÉ" — sans elle, la seule façon de remarquer un ennemi était
                // de lire le mot "(Ennemi)" sur le bon bouton de boussole parmi les 4, facile à
                // manquer (voir plainte "rien n'est clair").
                if (enemyAlertLabel != null) enemyAlertLabel.style.display = anyEnemyNearby ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private static readonly string[] MiniMapCellClasses = { "zonecell-neutral", "zonecell-mine", "zonecell-enemy" };

        /// <summary>Colore ET écrit un symbole sur la case (voir Theme.tss "zonecell-*" — les cases de
        /// la mini-carte sont des VisualElement, pas des Button, donc les classes btn-team1/btn-team2/
        /// btn-zone-neutral utilisées par les boutons de boussole ne s'y appliquaient jamais : ces
        /// cases restaient invisibles quelle que soit la vraie propriété de la Zone (voir plainte
        /// "rien n'est clair"). Renvoie vrai si cette Zone est ennemie, pour la bannière d'alerte.</summary>
        private bool UpdateMiniMapCell(string cellName, (int x, int y) tile, System.Collections.Generic.Dictionary<(int, int), string> owners, string myUserId)
        {
            VisualElement mapRoot = UIScreenManager.Instance.GetScreen("ZoneMap");
            var cell = mapRoot?.Q<VisualElement>(cellName);
            var symbol = mapRoot?.Q<Label>(cellName + "-symbol");
            if (cell == null) return false;

            string status = DescribeOwner(owners, tile, myUserId);
            string cssClass = status switch
            {
                "À vous" => "zonecell-mine",
                "Ennemi" => "zonecell-enemy",
                _ => "zonecell-neutral",
            };
            foreach (string c in MiniMapCellClasses)
                if (c != cssClass) cell.RemoveFromClassList(c);
            if (!cell.ClassListContains(cssClass)) cell.AddToClassList(cssClass);

            if (symbol != null)
            {
                symbol.text = status switch { "À vous" => "★", "Ennemi" => "⚔", _ => "" };
            }

            return status == "Ennemi";
        }

        private static string DescribeOwner(System.Collections.Generic.Dictionary<(int, int), string> owners, (int x, int y) tile, string myUserId)
        {
            if (!owners.TryGetValue(tile, out string owner) || string.IsNullOrEmpty(owner)) return "Neutre";
            return owner == myUserId ? "À vous" : "Ennemi";
        }

        private static readonly string[] ZoneStatusClasses = { "btn-zone-neutral", "btn-team1", "btn-team2" };

        /// <summary>Bascule aussi la classe CSS du bouton selon le statut réel (neutre/allié/ennemi)
        /// — auparavant Nord/Sud portaient toujours la classe "ennemi" (rouge) et Est/Ouest toujours
        /// "allié" (bleu), fixé par direction dans l'UXML et jamais mis à jour : une Zone neutre au
        /// Nord s'affichait en rouge "ennemi" (voir rapport d'audit interface, défaut important #4).</summary>
        private static void ApplyNeighborStatus(Button btn, string label, System.Collections.Generic.Dictionary<(int, int), string> owners, (int x, int y) tile, string myUserId)
        {
            string status = DescribeOwner(owners, tile, myUserId);
            string cssClass = status switch
            {
                "À vous" => "btn-team1",
                "Ennemi" => "btn-team2",
                _ => "btn-zone-neutral",
            };
            SetNeighborStatus(btn, label, status, cssClass);
        }

        private static void SetNeighborStatus(Button btn, string label, string status, string cssClass = null)
        {
            if (btn == null) return;
            btn.text = $"{label}\n({status})";
            if (cssClass != null)
            {
                foreach (string c in ZoneStatusClasses)
                    if (c != cssClass) btn.RemoveFromClassList(c);
                if (!btn.ClassListContains(cssClass)) btn.AddToClassList(cssClass);
            }
        }
    }
#endif
}
