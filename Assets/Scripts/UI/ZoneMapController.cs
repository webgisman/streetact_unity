using UnityEngine;
#if !UNITY_SERVER
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
    /// Écran CONQUÊTE (ConquestScreen.uxml) : onglets et onglet CARTE ; l'en-tête et l'onglet GESTION sont
    /// câblés par MultiplayerMatchController.BindUI (état Hub), qui appelle <see cref="OnHubShown"/>.
    /// CARTE : 3×3 cases = les vraies tuiles OpenStreetMap zoom 17 du quartier courant et de ses 8 voisins
    /// (un quartier EST une tuile zoom 17, CityGenerator.ZONE_ZOOM), colorées par statut ; toucher une
    /// case ouvre une fiche qui dit ce qu'elle est et ce que le bouton va faire.
    /// Portée : même règle que le serveur (MatchSessionManager_Conquest.RunConquestRequest) — sans
    /// quartier, n'importe quel quartier libre ; ensuite, seulement ceux qui touchent un des vôtres.
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

        public enum Tab { Map, Manage }

        // Statique : l'onglet ouvert survit au rechargement de scène (retour après un siège, etc.)
        // pour revenir là où le joueur était.
        private static Tab lastTab = Tab.Map;

        private const int IncomePerLevelPer5Min = 10; // voir MatchSessionManager.ZoneIncomePerZone

        private enum CellKind { Loading, Free, FreeFar, Mine, Enemy, EnemyShielded, EnemyFar }

        private bool uiBound = false;
        private VisualElement mapContent, manageContent, mapFrame, mapDetail;
        private Button tabMapButton, tabManageButton;
        private Label detailTitle, detailChip, detailText, detailStatus, resultLabel;
        private Button detailAction, detailSecondary;
        private System.Action detailActionHandler, detailSecondaryHandler;

        private readonly Button[,] cells = new Button[3, 3];
        private readonly VisualElement[,] cellOverlays = new VisualElement[3, 3];
        private readonly Label[,] cellTags = new Label[3, 3];
        private readonly Label[,] cellHereTags = new Label[3, 3];
        private readonly Label[,] cellDirections = new Label[3, 3];

        // Un double-tap sur une action envoyait deux "join_matchmaking" successifs — verrou levé dès
        // qu'un résultat revient (HandleZoneResult) ou que le joueur annule l'attente.
        private bool attackInFlight = false;

        /// <summary>Oublie une action lancée depuis la carte dont le résultat n'arrivera jamais
        /// (attente annulée, voir MultiplayerMatchController.OnCancelWaitClicked).</summary>
        public void ForgetPendingAttack() => attackInFlight = false;

        // Dernier état connu de la carte (RefreshMapRoutine).
        private int centerX, centerY;
        private bool mapLoaded = false;
        private bool mapLoadFailed = false;
        private readonly Dictionary<(int, int), string> owners = new Dictionary<(int, int), string>();
        private readonly Dictionary<(int, int), string> shields = new Dictionary<(int, int), string>();
        private readonly Dictionary<(int, int), int> levels = new Dictionary<(int, int), int>();
        private readonly HashSet<(int, int)> myTiles = new HashSet<(int, int)>();
        private readonly Dictionary<string, string> usernames = new Dictionary<string, string>();
        private (int x, int y)? selectedTile = null;
        private Coroutine refreshRoutine;

        // Tuiles OpenStreetMap déjà chargées (mémoire) — partagé avec le cache disque de
        // MapTileLoader (même dossier, même nommage tile_{zoom}_{x}_{y}.png).
        private static readonly Dictionary<(int, int), Texture2D> tileTextures = new Dictionary<(int, int), Texture2D>();

        private float lastLayoutWidth = -1f, lastLayoutHeight = -1f;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() => MultiplayerMatchController.OnZoneResult += HandleZoneResult;
        private void OnDisable() => MultiplayerMatchController.OnZoneResult -= HandleZoneResult;

        // =====================================================================
        // Entrées publiques
        // =====================================================================

        /// <summary>Ouvre la Conquête sur l'onglet CARTE (raccourcis "ouvrir la carte" des écrans de
        /// gestion) — passe par l'état Hub du contrôleur multijoueur, qui rappelle OnHubShown.</summary>
        public void Show()
        {
            lastTab = Tab.Map;
            MultiplayerMatchController.EnsureInstance().ReturnToHub();
        }

        /// <summary>Appelé par MultiplayerMatchController à CHAQUE affichage de l'écran Conquête.</summary>
        public void OnHubShown()
        {
            if (!BindUiOnce()) return;
            attackInFlight = false;
            ApplyTab(lastTab);
        }

        public void SelectTab(Tab tab)
        {
            if (tab == lastTab) return;
            UiSfx.Play(UiSfx.Sound.Tab);
            lastTab = tab;
            ApplyTab(tab);
        }

        private void ApplyTab(Tab tab)
        {
            bool map = tab == Tab.Map;
            mapContent.style.display = map ? DisplayStyle.Flex : DisplayStyle.None;
            manageContent.style.display = map ? DisplayStyle.None : DisplayStyle.Flex;
            tabMapButton.EnableInClassList("cq-tab--active", map);
            tabManageButton.EnableInClassList("cq-tab--active", !map);
            if (map) RefreshMap();
        }

        // =====================================================================
        // Câblage
        // =====================================================================

        private bool BindUiOnce()
        {
            if (uiBound) return true;
            if (UIScreenManager.Instance == null) return false;
            VisualElement root = UIScreenManager.Instance.GetScreen("Conquest");
            if (root == null)
            {
                Debug.LogError("[ZoneMapController] Écran 'Conquest' introuvable (UXML non chargé).");
                return false;
            }

            mapContent = root.Q<VisualElement>("map-content");
            manageContent = root.Q<VisualElement>("manage-content");
            mapFrame = root.Q<VisualElement>("map-frame");
            mapDetail = root.Q<VisualElement>("map-detail");
            tabMapButton = root.Q<Button>("tab-map");
            tabManageButton = root.Q<Button>("tab-manage");
            detailTitle = root.Q<Label>("detail-title");
            detailChip = root.Q<Label>("detail-chip");
            detailText = root.Q<Label>("detail-text");
            detailStatus = root.Q<Label>("detail-status");
            detailAction = root.Q<Button>("btn-detail-action");
            detailSecondary = root.Q<Button>("btn-detail-secondary");
            VisualElement grid = root.Q<VisualElement>("map-grid");
            if (mapContent == null || manageContent == null || grid == null || detailTitle == null || detailAction == null)
            {
                Debug.LogError("[ZoneMapController] Éléments de ConquestScreen.uxml manquants — carte indisponible.");
                return false;
            }

            tabMapButton.clicked += () => SelectTab(Tab.Map);
            tabManageButton.clicked += () => SelectTab(Tab.Manage);
            detailAction.clicked += () => detailActionHandler?.Invoke();
            detailSecondary.clicked += () => detailSecondaryHandler?.Invoke();

            BuildGrid(grid);
            mapContent.RegisterCallback<GeometryChangedEvent>(_ => ApplyResponsiveLayout());

            VisualElement resultRoot = UIScreenManager.Instance.GetScreen("ZoneResult");
            if (resultRoot != null)
            {
                resultLabel = resultRoot.Q<Label>("zone-result-text");
                // CONTINUER revient sur la Conquête, au même onglet (la carte, puisque le résultat
                // vient d'une action lancée depuis elle) ; ReturnToHub recharge la scène si un
                // déploiement l'a modifiée.
                resultRoot.Q<Button>("btn-zone-result-ok").clicked += () =>
                {
                    UiSfx.Play(UiSfx.Sound.Tap);
                    MultiplayerMatchController.EnsureInstance().ReturnToHub();
                };
            }

            uiBound = true;
            return true;
        }

        private void BuildGrid(VisualElement grid)
        {
            grid.Clear();
            for (int row = 0; row < 3; row++)
            {
                var rowEl = new VisualElement();
                rowEl.AddToClassList("cq-map-row");
                grid.Add(rowEl);
                for (int col = 0; col < 3; col++)
                {
                    int dx = col - 1, dy = row - 1;
                    var cell = new Button();
                    cell.AddToClassList("cq-cell");
                    cell.style.width = 180; cell.style.height = 180; // recalculé par ApplyResponsiveLayout

                    var overlay = new VisualElement();
                    overlay.AddToClassList("cq-cell-overlay");
                    overlay.pickingMode = PickingMode.Ignore;

                    var here = new Label("VOUS ÊTES ICI");
                    here.AddToClassList("cq-cell-tag");
                    here.AddToClassList("cq-cell-tag--here");
                    here.style.marginBottom = 6;
                    here.style.display = dx == 0 && dy == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                    here.pickingMode = PickingMode.Ignore;
                    overlay.Add(here);

                    var tag = new Label("…");
                    tag.AddToClassList("cq-cell-tag");
                    tag.pickingMode = PickingMode.Ignore;
                    overlay.Add(tag);

                    var dir = new Label(QuartierText.Direction(dx, dy));
                    dir.AddToClassList("cq-cell-direction");
                    dir.pickingMode = PickingMode.Ignore;

                    cell.Add(overlay);
                    cell.Add(dir);
                    cell.clicked += () => OnCellClicked(dx, dy);
                    rowEl.Add(cell);

                    cells[row, col] = cell;
                    cellOverlays[row, col] = overlay;
                    cellTags[row, col] = tag;
                    cellHereTags[row, col] = here;
                    cellDirections[row, col] = dir;
                }
            }
        }

        /// <summary>Carte + fiche côte à côte en paysage, l'une sous l'autre en portrait (l'appli
        /// tourne dans les deux sens, voir ProjectSettings) — et cases aussi grandes que la place le
        /// permet. UI Toolkit n'ayant pas de media queries, recalculé ici à chaque changement de
        /// taille de la zone de contenu.</summary>
        private void ApplyResponsiveLayout()
        {
            float w = mapContent.contentRect.width, h = mapContent.contentRect.height;
            if (w <= 0 || h <= 0) return;
            if (Mathf.Abs(w - lastLayoutWidth) < 1f && Mathf.Abs(h - lastLayoutHeight) < 1f) return;
            lastLayoutWidth = w; lastLayoutHeight = h;

            bool landscape = w >= h * 1.15f;
            const float frameChrome = 40f; // padding + bordures du cadre + attribution
            const float cellMargins = 12f; // 2px x 2 côtés x 3 cases
            float mapSide;
            if (landscape)
            {
                float detailWidth = Mathf.Clamp(w * 0.40f, 320f, 620f);
                mapSide = Mathf.Min(w - detailWidth - 20f, h);
                mapContent.style.flexDirection = FlexDirection.Row;
                mapContent.style.justifyContent = Justify.Center;
                mapDetail.style.width = detailWidth;
                mapDetail.style.height = Mathf.Min(h, mapSide); // même hauteur que la carte, côte à côte
                mapDetail.style.maxHeight = StyleKeyword.None;
                mapDetail.style.marginLeft = 16; mapDetail.style.marginTop = 0;
            }
            else
            {
                // Portrait : carte en haut, fiche juste dessous à la hauteur de SON contenu (une hauteur
                // fixe laissait un grand panneau vide sous le bouton, vu sur capture 1080x2400).
                mapSide = Mathf.Min(w, h * 0.58f);
                mapContent.style.flexDirection = FlexDirection.Column;
                mapContent.style.justifyContent = Justify.FlexStart;
                mapDetail.style.width = Mathf.Min(w, 820f);
                mapDetail.style.height = StyleKeyword.Auto;
                mapDetail.style.maxHeight = Mathf.Max(160f, h - mapSide - 14f);
                mapDetail.style.marginLeft = 0; mapDetail.style.marginTop = 12;
            }

            float cellSide = Mathf.Floor(Mathf.Max(90f, (mapSide - frameChrome - cellMargins) / 3f));
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    cells[row, col].style.width = cellSide;
                    cells[row, col].style.height = cellSide;
                }
        }

        // =====================================================================
        // Chargement de la carte
        // =====================================================================

        private void RefreshMap()
        {
            if (refreshRoutine != null) StopCoroutine(refreshRoutine);
            refreshRoutine = StartCoroutine(RefreshMapRoutine(silent: false));
        }

        /// <summary>Remet à jour les propriétaires affichés SANS repasser par l'état "chargement"
        /// (pas de clignotement, sélection conservée) — rafraîchissement périodique de
        /// MultiplayerMatchController pendant que le joueur regarde la carte (2026-10-03).</summary>
        public void RefreshMapSilently()
        {
            if (!uiBound || lastTab != Tab.Map || refreshRoutine != null || attackInFlight || !mapLoaded) return;
            ZoneManager zm = ZoneManager.EnsureInstance();
            if (zm.CurrentTileX != centerX || zm.CurrentTileY != centerY) { RefreshMap(); return; }
            refreshRoutine = StartCoroutine(RefreshMapRoutine(silent: true));
        }

        [System.Serializable] private class ZoneRow { public int tile_x; public int tile_y; public string owner_user_id; public string shield_until; public int building_level = 1; }
        [System.Serializable] private class ZoneRowList { public ZoneRow[] items; }
        [System.Serializable] private class ProfileRow { public string id; public string username; }
        [System.Serializable] private class ProfileRowList { public ProfileRow[] items; }

        private IEnumerator RefreshMapRoutine(bool silent)
        {
            ZoneManager zm = ZoneManager.EnsureInstance();
            int cx = zm.CurrentTileX, cy = zm.CurrentTileY;
            if (!silent)
            {
                if (cx != centerX || cy != centerY) selectedTile = null;
                centerX = cx; centerY = cy;
                mapLoaded = false;
                mapLoadFailed = false;

                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        cells[row, col].userData = (cx + col - 1, cy + row - 1);
                        SetCellVisual(row, col, CellKind.Loading, "…");
                        StartCoroutine(LoadTileImage(cx + col - 1, cy + row - 1, cells[row, col]));
                    }
                RenderDetail();
            }

            // 1. Propriétaires des 9 quartiers affichés.
            string myUserId = SupabaseAuthClient.CurrentSession?.user?.id;
            string filter = $"tile_x=gte.{cx - 1}&tile_x=lte.{cx + 1}&tile_y=gte.{cy - 1}&tile_y=lte.{cy + 1}";
            string url = $"{SupabaseAuthClient.RestBaseUrl}/zones?zoom=eq.{CityGenerator.ZONE_ZOOM}&{filter}&select=tile_x,tile_y,owner_user_id,shield_until,building_level";
            ZoneRow[] rows = null;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
                req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession?.access_token);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try { rows = JsonUtility.FromJson<ZoneRowList>("{\"items\":" + req.downloadHandler.text + "}").items; }
                    catch (System.Exception ex) { Debug.LogWarning($"[ZoneMapController] Lecture des quartiers échouée : {ex.Message}"); }
                }
                else Debug.LogWarning($"[ZoneMapController] Quartiers indisponibles : {req.error}");
            }
            if (rows == null)
            {
                refreshRoutine = null;
                if (silent) yield break; // rafraîchissement de fond : on garde l'état déjà affiché
                // Échec réseau affiché comme tel — jamais "tout est libre" par défaut, ce qui serait
                // une fausse information (voir rapport d'audit interface, défaut bloquant #3).
                mapLoadFailed = true;
                RenderDetail();
                yield break;
            }

            owners.Clear(); shields.Clear(); levels.Clear();
            foreach (var r in rows)
            {
                owners[(r.tile_x, r.tile_y)] = r.owner_user_id;
                shields[(r.tile_x, r.tile_y)] = r.shield_until;
                levels[(r.tile_x, r.tile_y)] = Mathf.Max(1, r.building_level);
            }

            // 2. TOUS mes quartiers (pas seulement ceux affichés) : la portée dépend de mon territoire entier.
            var ownedTask = SupabaseDatabaseClient.GetOwnedZones();
            while (!ownedTask.IsCompleted) yield return null;
            var (okOwned, ownedZones) = ownedTask.Result;
            myTiles.Clear();
            if (okOwned && ownedZones != null)
                foreach (var z in ownedZones) myTiles.Add((z.tile_x, z.tile_y));
            foreach (var kv in owners)
                if (!string.IsNullOrEmpty(kv.Value) && kv.Value == myUserId) myTiles.Add(kv.Key);

            // 3. Pseudos des autres propriétaires ("tenu par Bob" plutôt que "ennemi").
            var unknownIds = new List<string>();
            foreach (var kv in owners)
                if (!string.IsNullOrEmpty(kv.Value) && kv.Value != myUserId && !usernames.ContainsKey(kv.Value) && !unknownIds.Contains(kv.Value))
                    unknownIds.Add(kv.Value);
            if (unknownIds.Count > 0)
            {
                string profilesUrl = $"{SupabaseAuthClient.RestBaseUrl}/profiles?id=in.({string.Join(",", unknownIds)})&select=id,username";
                using (UnityWebRequest req = UnityWebRequest.Get(profilesUrl))
                {
                    req.timeout = 10;
                    req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
                    req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession?.access_token);
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            var list = JsonUtility.FromJson<ProfileRowList>("{\"items\":" + req.downloadHandler.text + "}").items;
                            if (list != null) foreach (var p in list) usernames[p.id] = p.username;
                        }
                        catch (System.Exception ex) { Debug.LogWarning($"[ZoneMapController] Lecture des pseudos échouée : {ex.Message}"); }
                    }
                }
            }

            mapLoaded = true;
            refreshRoutine = null;

            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    var tile = (cx + col - 1, cy + row - 1);
                    CellKind kind = KindOf(tile, myUserId);
                    SetCellVisual(row, col, kind, TagFor(tile, kind));
                }

            // Tout premier quartier : on présélectionne celui du centre s'il est libre — l'action la
            // plus évidente pour un nouveau joueur ("prenez votre quartier").
            if (selectedTile == null && myTiles.Count == 0 && KindOf((cx, cy), myUserId) == CellKind.Free)
                selectedTile = (cx, cy);

            RefreshSelectionHighlight();
            // Rafraîchissement de fond : ne pas réécrire la fiche si une action est en cours (son
            // message "Ordre transmis..." / "Siège impossible : ..." doit rester lisible).
            if (!silent || !attackInFlight) RenderDetail();
        }

        private IEnumerator LoadTileImage(int tileX, int tileY, Button cell)
        {
            var key = (tileX, tileY);
            if (!tileTextures.TryGetValue(key, out Texture2D tex) || tex == null)
            {
                tex = null;
                string cacheFolder = Path.Combine(Application.persistentDataPath, "MapCache");
                string cachePath = Path.Combine(cacheFolder, $"tile_{CityGenerator.ZONE_ZOOM}_{tileX}_{tileY}.png");
                if (File.Exists(cachePath))
                {
                    try
                    {
                        var fromDisk = new Texture2D(2, 2);
                        if (fromDisk.LoadImage(File.ReadAllBytes(cachePath))) tex = fromDisk;
                        else Destroy(fromDisk);
                    }
                    catch (System.Exception) { tex = null; }
                }

                if (tex == null)
                {
                    // Même serveur de tuiles et même User-Agent que MapTileLoader (politique
                    // d'usage des tuiles OpenStreetMap).
                    string url = $"https://tile.openstreetmap.org/{CityGenerator.ZONE_ZOOM}/{tileX}/{tileY}.png";
                    using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(url))
                    {
                        www.SetRequestHeader("User-Agent", "NovgovTacticalGame/1.0 (Windows; Unity)");
                        www.timeout = 15;
                        yield return www.SendWebRequest();
                        if (www.result == UnityWebRequest.Result.Success)
                        {
                            tex = DownloadHandlerTexture.GetContent(www);
                            try
                            {
                                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);
                                File.WriteAllBytes(cachePath, tex.EncodeToPNG());
                            }
                            catch (System.Exception) { }
                        }
                        else Debug.LogWarning($"[ZoneMapController] Tuile {tileX},{tileY} indisponible : {www.error}");
                    }
                }
                if (tex == null) yield break; // la case garde son fond uni : la carte reste utilisable
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tileTextures[key] = tex;
            }

            // La carte a pu être recentrée pendant le téléchargement : ne poser l'image que si cette
            // case montre toujours ce quartier.
            if (cell.userData is System.ValueTuple<int, int> shown && shown.Equals(key))
                cell.style.backgroundImage = new StyleBackground(tex);
        }

        // =====================================================================
        // Statut des quartiers
        // =====================================================================

        private bool IsReachable((int x, int y) tile)
        {
            if (myTiles.Count == 0) return true;
            foreach (var o in myTiles)
                if (Mathf.Abs(o.Item1 - tile.x) + Mathf.Abs(o.Item2 - tile.y) == 1) return true;
            return false;
        }

        private bool IsShielded((int, int) tile, out string remaining)
        {
            remaining = null;
            if (!shields.TryGetValue(tile, out string iso) || string.IsNullOrEmpty(iso)) return false;
            if (!System.DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out System.DateTime until)) return false;
            System.TimeSpan left = until - System.DateTime.UtcNow;
            if (left <= System.TimeSpan.Zero) return false;
            remaining = left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:D2}" : $"{Mathf.Max(1, left.Minutes)} min";
            return true;
        }

        private CellKind KindOf((int x, int y) tile, string myUserId)
        {
            if (!mapLoaded) return CellKind.Loading;
            owners.TryGetValue(tile, out string owner);
            if (string.IsNullOrEmpty(owner)) return IsReachable(tile) ? CellKind.Free : CellKind.FreeFar;
            if (owner == myUserId) return CellKind.Mine;
            if (IsShielded(tile, out _)) return CellKind.EnemyShielded;
            return IsReachable(tile) ? CellKind.Enemy : CellKind.EnemyFar;
        }

        private string OwnerName((int, int) tile)
        {
            if (owners.TryGetValue(tile, out string owner) && !string.IsNullOrEmpty(owner) && usernames.TryGetValue(owner, out string name) && !string.IsNullOrEmpty(name))
                return name;
            return "un autre joueur";
        }

        private string TagFor((int, int) tile, CellKind kind)
        {
            switch (kind)
            {
                case CellKind.Free: return "LIBRE";
                case CellKind.FreeFar: return "LIBRE (trop loin)";
                case CellKind.Mine: return "★ À VOUS";
                case CellKind.EnemyShielded: return "PROTÉGÉ";
                case CellKind.Enemy:
                case CellKind.EnemyFar:
                    string name = OwnerName(tile);
                    return "⚔ " + (name.Length > 14 ? name.Substring(0, 13) + "…" : name);
                default: return "…";
            }
        }

        private static readonly string[] OverlayClasses = { "cq-overlay--free", "cq-overlay--mine", "cq-overlay--enemy", "cq-overlay--shield", "cq-overlay--far", "cq-overlay--loading" };
        private static readonly string[] TagClasses = { "cq-cell-tag--free", "cq-cell-tag--mine", "cq-cell-tag--enemy" };

        private void SetCellVisual(int row, int col, CellKind kind, string tag)
        {
            string overlay = kind switch
            {
                CellKind.Free => "cq-overlay--free",
                CellKind.Mine => "cq-overlay--mine",
                CellKind.Enemy => "cq-overlay--enemy",
                CellKind.EnemyShielded => "cq-overlay--shield",
                CellKind.FreeFar or CellKind.EnemyFar => "cq-overlay--far",
                _ => "cq-overlay--loading",
            };
            string tagClass = kind switch
            {
                CellKind.Free => "cq-cell-tag--free",
                CellKind.Mine => "cq-cell-tag--mine",
                CellKind.Enemy => "cq-cell-tag--enemy",
                _ => null,
            };
            foreach (string c in OverlayClasses) cellOverlays[row, col].EnableInClassList(c, c == overlay);
            foreach (string c in TagClasses) cellTags[row, col].EnableInClassList(c, c == tagClass);
            cellTags[row, col].text = tag;
        }

        private void RefreshSelectionHighlight()
        {
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    var tile = (centerX + col - 1, centerY + row - 1);
                    cells[row, col].EnableInClassList("cq-cell--selected", selectedTile.HasValue && selectedTile.Value == tile);
                }
        }

        // =====================================================================
        // Fiche du quartier touché
        // =====================================================================

        private void OnCellClicked(int dx, int dy)
        {
            var tile = (centerX + dx, centerY + dy);
            selectedTile = tile;
            string myUserId = SupabaseAuthClient.CurrentSession?.user?.id;
            CellKind kind = KindOf(tile, myUserId);
            UiSfx.Play(kind == CellKind.FreeFar || kind == CellKind.EnemyFar || kind == CellKind.EnemyShielded ? UiSfx.Sound.Tap : UiSfx.Sound.Select);
            if (detailStatus != null) detailStatus.text = "";
            RefreshSelectionHighlight();
            RenderDetail();
        }

        private void SetChip(string text, string modifier)
        {
            if (detailChip == null) return;
            detailChip.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            detailChip.text = text ?? "";
            foreach (string c in new[] { "cq-chip--free", "cq-chip--mine", "cq-chip--enemy", "cq-chip--shield", "cq-chip--here" })
                detailChip.EnableInClassList(c, c == modifier);
        }

        private void SetActions(string primary, System.Action onPrimary, string secondary = null, System.Action onSecondary = null)
        {
            detailActionHandler = onPrimary;
            detailSecondaryHandler = onSecondary;
            detailAction.text = primary ?? "";
            detailAction.style.display = string.IsNullOrEmpty(primary) ? DisplayStyle.None : DisplayStyle.Flex;
            detailAction.SetEnabled(!attackInFlight);
            detailSecondary.text = secondary ?? "";
            detailSecondary.style.display = string.IsNullOrEmpty(secondary) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void RenderDetail() => RenderDetailAs(SupabaseAuthClient.CurrentSession?.user?.id);

        private void RenderDetailAs(string myUserId)
        {
            if (!uiBound) return;

            if (mapLoadFailed)
            {
                detailTitle.text = "CARTE INDISPONIBLE";
                SetChip(null, null);
                detailText.text = "Impossible de savoir qui occupe les quartiers autour de vous (problème de connexion). Vérifiez votre réseau, puis réessayez.";
                SetActions("RÉESSAYER", () => { UiSfx.Play(UiSfx.Sound.Tap); RefreshMap(); });
                return;
            }

            if (!mapLoaded)
            {
                detailTitle.text = "VOTRE QUARTIER";
                SetChip(null, null);
                detailText.text = "Chargement de la carte...";
                SetActions(null, null);
                return;
            }

            if (!selectedTile.HasValue)
            {
                detailTitle.text = "VOTRE QUARTIER";
                SetChip(null, null);
                detailText.text = myTiles.Count == 0
                    ? "Voici la vraie carte de votre quartier (au centre) et des 8 quartiers qui l'entourent.\n\nPour bien démarrer, prenez votre premier quartier : touchez une case LIBRE."
                    : "Voici la vraie carte de votre quartier (au centre) et des 8 quartiers qui l'entourent.\n\nTouchez un quartier pour voir ce que vous pouvez y faire.";
                SetActions(null, null);
                return;
            }

            var tile = selectedTile.Value;
            CellKind kind = KindOf(tile, myUserId);
            bool isHere = tile.x == centerX && tile.y == centerY;
            ZoneManager zm = ZoneManager.EnsureInstance();
            bool isHome = zm.HasHomeZone && tile.x == zm.HomeTileX && tile.y == zm.HomeTileY;
            string where = isHome ? "VOTRE QG" : (isHere ? "VOTRE POSITION" : "QUARTIER " + QuartierText.Direction(tile.x - centerX, tile.y - centerY));
            detailTitle.text = where;
            string name = OwnerName(tile);
            levels.TryGetValue(tile, out int level);
            level = Mathf.Max(1, level);

            switch (kind)
            {
                case CellKind.Free:
                    SetChip(isHere ? "LIBRE — VOUS ÊTES ICI" : "LIBRE", "cq-chip--free");
                    detailText.text = $"Personne n'occupe ce quartier.\n\nPrenez-le maintenant : c'est gratuit, immédiat et sans combat. Il vous rapportera ensuite +{IncomePerLevelPer5Min} Points d'Action toutes les 5 minutes, même quand vous ne jouez pas.";
                    SetActions("PRENDRE CE QUARTIER", () => Capture(tile.x, tile.y));
                    break;

                case CellKind.FreeFar:
                    SetChip("LIBRE — HORS DE PORTÉE", null);
                    detailText.text = "Ce quartier est libre, mais il ne touche aucun de vos quartiers.\n\nVotre territoire doit rester d'un seul tenant : prenez d'abord un quartier libre situé entre les deux.";
                    SetActions(null, null);
                    break;

                case CellKind.Mine:
                    SetChip(isHere ? "À VOUS — VOUS ÊTES ICI" : "À VOUS", "cq-chip--mine");
                    detailText.text = isHere
                        ? $"Ce quartier vous appartient (niveau {level}) et vous rapporte +{IncomePerLevelPer5Min * level} Points d'Action toutes les 5 minutes.\n\nTouchez un quartier voisin pour agrandir votre territoire."
                        : $"Ce quartier vous appartient (niveau {level}) et vous rapporte +{IncomePerLevelPer5Min * level} Points d'Action toutes les 5 minutes.\n\nRendez-vous-y pour conquérir les quartiers qui l'entourent.";
                    if (isHere) SetActions(null, null, "AMÉLIORER MES QUARTIERS", OpenBuildings);
                    else SetActions("M'Y RENDRE", () => MoveTo(tile.x, tile.y), "AMÉLIORER MES QUARTIERS", OpenBuildings);
                    break;

                case CellKind.Enemy:
                    SetChip("TENU PAR " + name.ToUpperInvariant(), "cq-chip--enemy");
                    // 2026-10-03 : un siège est désormais une vraie bataille au tour par tour entre
                    // les deux joueurs (voir MultiplayerMatchController.JoinSiegeBattle).
                    detailText.text = $"{name} occupe ce quartier. Pour le lui prendre, lancez un SIÈGE :\n\n" +
                        $"1. {name} est prévenu, et la bataille commence dès que vous êtes tous les deux en ligne ;\n" +
                        "2. chacun place ses troupes, trace ses trajectoires et termine son tour ;\n" +
                        "3. le tour se joue pour vous deux en même temps, et vous le regardez ensemble, jusqu'à la victoire.\n\n" +
                        "Si vous ne vous retrouvez pas sous 6 h, la bataille se joue automatiquement avec les troupes de vos casernes.";
                    SetActions("LANCER UN SIÈGE", () => Siege(tile.x, tile.y), "RECRUTER DES TROUPES", OpenRoster);
                    break;

                case CellKind.EnemyShielded:
                    IsShielded(tile, out string remaining);
                    SetChip("PROTÉGÉ", "cq-chip--shield");
                    detailText.text = $"Ce quartier appartient à {name} et sort tout juste d'un siège : il est protégé encore {remaining}.\n\nAucune attaque n'est possible avant la fin de cette protection.";
                    SetActions(null, null);
                    break;

                case CellKind.EnemyFar:
                    SetChip("TENU PAR " + name.ToUpperInvariant() + " — HORS DE PORTÉE", "cq-chip--enemy");
                    detailText.text = $"{name} occupe ce quartier, mais il ne touche aucun des vôtres.\n\nPrenez d'abord un quartier voisin pour vous en approcher.";
                    SetActions(null, null);
                    break;

                default:
                    SetChip(null, null);
                    detailText.text = "Chargement...";
                    SetActions(null, null);
                    break;
            }
        }

        private void OpenBuildings()
        {
            UiSfx.Play(UiSfx.Sound.Tap);
            MultiplayerMatchController.EnsureInstance().OpenManagementScreen("Buildings");
        }

        private void OpenRoster()
        {
            UiSfx.Play(UiSfx.Sound.Tap);
            MultiplayerMatchController.EnsureInstance().OpenManagementScreen("Roster");
        }

        // =====================================================================
        // Actions
        // =====================================================================

        private void Capture(int tileX, int tileY)
        {
            if (attackInFlight) return;
            attackInFlight = true;
            UiSfx.Play(UiSfx.Sound.RadioRoger);
            if (detailStatus != null) detailStatus.text = "Ordre transmis...";
            detailAction.SetEnabled(false);
            MultiplayerMatchController.EnsureInstance().AttackZone(tileX, tileY);
        }

        private void MoveTo(int tileX, int tileY)
        {
            UiSfx.Play(UiSfx.Sound.RadioRoger);
            selectedTile = null;
            ZoneManager.EnsureInstance().LoadZone(tileX, tileY);
            RefreshMap();
        }

        private void Siege(int tileX, int tileY)
        {
            if (attackInFlight) return;
            UiSfx.Play(UiSfx.Sound.RadioTargetLocked);
            StartCoroutine(StartSiegeThenDeploy(tileX, tileY));
        }

        private static readonly Regex PostgrestMessage = new Regex("\"message\"\\s*:\\s*\"([^\"]+)\"");

        /// <summary>Déclare le siège (RPC start_siege) puis, seulement en cas de succès, ouvre le
        /// bataille de l'attaquant (JoinSiegeBattle) — jamais l'inverse : un déploiement ne doit jamais
        /// démarrer pour un siège qui n'a pas pu être créé (bouclier posé entre-temps, siège déjà en
        /// cours...).</summary>
        private IEnumerator StartSiegeThenDeploy(int tileX, int tileY)
        {
            attackInFlight = true;
            detailAction.SetEnabled(false);
            if (detailStatus != null) detailStatus.text = "Déclaration du siège...";

            var task = SupabaseDatabaseClient.StartSiege(tileX, tileY, CityGenerator.ZONE_ZOOM);
            while (!task.IsCompleted) yield return null;
            var (ok, siegeId, error) = task.Result;

            if (!ok)
            {
                attackInFlight = false;
                detailAction.SetEnabled(true);
                UiSfx.Play(UiSfx.Sound.Error);
                // Message SQL (PostgREST renvoie {"message": "..."}) plutôt que le JSON brut.
                Match m = error != null ? PostgrestMessage.Match(error) : Match.Empty;
                string reason = m.Success ? m.Groups[1].Value : "réessayez dans un instant";
                if (detailStatus != null) detailStatus.text = "Siège impossible : " + QuartierText.HumanizeServerText(reason);
                yield break;
            }

            // 2026-10-03 : le siège déclaré, l'attaquant rejoint la BATAILLE AU TOUR PAR TOUR de ce
            // siège (salle d'attente côté serveur, voir MatchSessionManager_SiegeBattle.cs).
            MultiplayerMatchController.EnsureInstance().JoinSiegeBattle(tileX, tileY, siegeId, asAttacker: true, OwnerName((tileX, tileY)));
        }

#if UNITY_EDITOR
        /// <summary>Éditeur uniquement (Assets/Editor/ConquestScreenPreview.cs) : remplit la carte avec
        /// des propriétaires fictifs, SANS réseau de jeu ni compte, pour vérifier la mise en page et la
        /// lisibilité par capture d'écran. Les tuiles OpenStreetMap, elles, sont les vraies.</summary>
        public void EditorPreview(int cx, int cy, (int dx, int dy)? select)
        {
            if (!BindUiOnce()) return;
            lastTab = Tab.Map;
            ApplyTabWithoutRefresh(Tab.Map);
            centerX = cx; centerY = cy;
            const string me = "preview-me", bob = "preview-bob", alice = "preview-alice";
            owners.Clear(); shields.Clear(); levels.Clear(); myTiles.Clear(); usernames.Clear();
            owners[(cx, cy)] = me; levels[(cx, cy)] = 2; myTiles.Add((cx, cy));
            owners[(cx + 1, cy)] = bob; usernames[bob] = "Bob";
            owners[(cx + 1, cy + 1)] = alice; usernames[alice] = "Alice";
            owners[(cx - 1, cy - 1)] = bob;
            shields[(cx - 1, cy - 1)] = System.DateTime.UtcNow.AddHours(3).ToString("o");
            mapLoaded = true; mapLoadFailed = false;
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    var tile = (cx + col - 1, cy + row - 1);
                    cells[row, col].userData = tile;
                    CellKind kind = KindOf(tile, EditorPreviewUserId);
                    SetCellVisual(row, col, kind, TagFor(tile, kind));
                    StartCoroutine(LoadTileImage(tile.Item1, tile.Item2, cells[row, col]));
                }
            selectedTile = select.HasValue ? (cx + select.Value.dx, cy + select.Value.dy) : ((int, int)?)null;
            RefreshSelectionHighlight();
            RenderDetailAs(EditorPreviewUserId);
        }

        public void EditorPreviewTab(Tab tab) { if (BindUiOnce()) { lastTab = tab; ApplyTabWithoutRefresh(tab); } }

        private const string EditorPreviewUserId = "preview-me";
        private void ApplyTabWithoutRefresh(Tab tab)
        {
            bool map = tab == Tab.Map;
            mapContent.style.display = map ? DisplayStyle.Flex : DisplayStyle.None;
            manageContent.style.display = map ? DisplayStyle.None : DisplayStyle.Flex;
            tabMapButton.EnableInClassList("cq-tab--active", map);
            tabManageButton.EnableInClassList("cq-tab--active", !map);
        }
#endif

        private void HandleZoneResult(string message, bool success)
        {
            BindUiOnce();
            attackInFlight = false;
            if (resultLabel != null) resultLabel.text = message;
            UiSfx.Play(success ? UiSfx.Sound.Success : UiSfx.Sound.Error);
            UIScreenManager.Instance?.Show("ZoneResult");
        }
    }
#endif
}
