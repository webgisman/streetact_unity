using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class GameManagerUI : MonoBehaviour
{
    // Écrans "Loading"/"Error" : entièrement pilotés par UIScreenManager (UI Toolkit, thème
    // commando unifié) depuis Resources/UI/LoadingScreen.uxml et ErrorScreen.uxml — remplace
    // l'ancien Canvas legacy (UnityEngine.UI) non stylé qui vivait directement dans la scène.
    private Label errorTextLabel;

    private static GameManagerUI instance;
    public static GameManagerUI Instance { get { return instance; } }

    // Statique : survit à un rechargement de scène (ex. bouton "Rejouer" en fin de partie solo ou
    // multijoueur). GameManagerUI lui-même est recréé à chaque rechargement (pas de
    // DontDestroyOnLoad), mais les Button qu'il câble viennent de UIScreenManager, qui LUI est
    // persistant — sans ce garde, chaque rechargement rajoutait un abonnement `clicked +=`
    // supplémentaire sur les MÊMES boutons "MODE SOLO"/"MODE CAMPAGNE MULTIJOUEUR", faisant
    // déclencher leurs actions (chargement de carte, connexion serveur) une fois par rechargement
    // vécu depuis le lancement de l'app.
    private static bool startupButtonsBound = false;

    [Header("Map Selector Startup")]
    private bool isMapSelectorOpen = true; // S'ouvre automatiquement au lancement du jeu
    public bool IsStartupSelectionActive => isMapSelectorOpen;
    private string gpsStatus = "";
    private Label gpsStatusLabel;
    private Label locationStatusLabel;

    /// <summary>Ce qu'il faut enchaîner automatiquement juste après le PROCHAIN rechargement de
    /// scène (2026-09-30, refonte du parcours des menus). Le jeu recharge sa scène pour repartir
    /// d'un état propre après chaque partie — mais ce rechargement ramenait TOUJOURS au menu de
    /// démarrage Solo/En ligne : "Rejouer" en fin de partie solo n'y rejouait pas, et "Retour au
    /// menu" en fin de partie en ligne obligeait à repasser par JOUER EN LIGNE -> chargement ->
    /// reconnexion pour retrouver le Quartier Général. Statique : doit survivre au rechargement,
    /// comme <see cref="startupButtonsBound"/>.</summary>
    public enum AfterReloadAction { None, StartSolo, OpenOnlineHub }
    private static AfterReloadAction pendingAfterReload = AfterReloadAction.None;

    /// <summary>Recharge la scène (état de partie remis à zéro) puis enchaîne directement sur
    /// <paramref name="then"/> au lieu du menu de démarrage — voir <see cref="pendingAfterReload"/>.
    /// Masque tout AVANT de recharger : UIScreenManager est persistant, ses écrans survivraient
    /// sinon intacts par-dessus la scène rechargée (voir TacticalPathManager.ShowSoloGameOver).</summary>
    public static void ReloadSceneThen(AfterReloadAction then)
    {
        pendingAfterReload = then;
        UIScreenManager.Instance?.HideAll();
        UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    // Quartier réellement chargé dans CETTE scène pour le jeu en ligne (voir EnsureOnlineZoneReady)
    // — champ d'instance, donc vidé à chaque rechargement de scène, qui efface aussi la ville
    // générée. Une tuile (pas un simple booléen, 2026-10-03) : la position peut changer sans
    // chargement (compte de test de l'Éditeur, voir Novgov.Network.EditorTestPlayers), il faut
    // alors recharger le bon quartier.
    private (int x, int y)? onlineZoneLoadedTile = null;
    private Coroutine onlineZoneRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigurePerformanceSettings()
    {
        // 1. Verrouillage du Framerate (2026-09-20, retour utilisateur : "le téléphone chauffe alors
        // que c'est un simple jeu tour par tour" sur 2 vrais appareils Android) — 30 FPS abaissé
        // depuis 60 : un rythme tour par tour, sans besoin de fluidité 60 FPS, n'a aucune raison de
        // faire tourner le rendu (et tout le travail par-frame non mis en cache, voir
        // l'historique git (ancien journal 08) §7 "Performance client") au double de la vitesse nécessaire.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 30;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        instance = this;

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 30;

        // Le Canvas legacy (UnityEngine.UI) encore présent dans la scène (Panel/LoadingPanel/
        // ErrorPanel, actifs par défaut) n'est plus piloté par aucun script depuis la migration
        // des écrans Chargement/Erreur vers UI Toolkit (LoadingScreen/ErrorScreen.uxml) — sans ce
        // nettoyage il resterait affiché en permanence, avec son rendu non stylé, par-dessus le jeu.
        // Important : désactiver seulement le COMPOSANT Canvas (pas gameObject.SetActive) — ce
        // Canvas legacy vit sur le MÊME GameObject que ce script GameManagerUI dans la scène, donc
        // désactiver le GameObject désactiverait GameManagerUI lui-même et empêcherait Start() de
        // s'exécuter (menu de démarrage jamais affiché — bug vécu et corrigé pendant cette session).
        Canvas legacyCanvas = GetComponent<Canvas>();
        if (legacyCanvas != null) legacyCanvas.enabled = false;

        // Ambiance visuelle militaire Brouillard de Guerre
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 45f;
        RenderSettings.fogEndDistance = 180f;
        RenderSettings.fogColor = new Color(0.12f, 0.16f, 0.22f);
    }

    private void Start()
    {
        OptimizeSceneMaterials();
#if !UNITY_SERVER
        BindStartupUI();
#endif
    }

#if !UNITY_SERVER
    private UnityEngine.UIElements.Button zoneMapButton;

    private void Update()
    {
        if (gpsStatusLabel != null) gpsStatusLabel.text = gpsStatus;
        if (locationStatusLabel != null) locationStatusLabel.text = gpsStatus;

        // Le bouton "CARTE DES ZONES" n'a de sens qu'une fois une Zone d'origine fixée (voir
        // ZoneManager.InitializeHomeZoneFromGps, appelé au premier "MODE CAMPAGNE MULTIJOUEUR") —
        // revérifié chaque frame plutôt qu'une seule fois au binding, puisque ça peut devenir vrai
        // en cours de session.
        if (zoneMapButton != null)
        {
            bool hasHomeZone = Novgov.Generation.ZoneManager.Instance != null && Novgov.Generation.ZoneManager.Instance.HasHomeZone;
            zoneMapButton.style.display = hasHomeZone ? UnityEngine.UIElements.DisplayStyle.Flex : UnityEngine.UIElements.DisplayStyle.None;
        }
    }

    private void BindStartupUI()
    {
        if (UIScreenManager.Instance == null)
        {
            Debug.LogError("[GameManagerUI] UIScreenManager.Instance introuvable — UIBootstrap ne s'est-il pas exécuté avant cette scène ?");
            return;
        }

        VisualElement root = UIScreenManager.Instance.GetScreen("StartupMenu");
        if (root == null)
        {
            Debug.LogError("[GameManagerUI] Écran 'StartupMenu' introuvable (UXML non chargé) — le menu de démarrage restera invisible.");
            return;
        }

        if (!startupButtonsBound)
        {
            try
            {
                // Menu réduit à 2 choix : MODE SOLO (carte déjà présente, contre l'IA) et MODE CAMPAGNE
                // MULTIJOUEUR (géolocalisation GPS — anciennement un 3e bouton séparé "btn-gps" — puis
                // connexion PvP, anciennement déclenchée directement sans passer par le GPS).
                // Via GameManagerUI.Instance, JAMAIS via `this` (correctif 2026-09-30) : ce câblage
                // n'a lieu qu'une fois par lancement de l'app (voir startupButtonsBound), donc `this`
                // désignait pour toujours l'instance de la PREMIÈRE scène. Après n'importe quel
                // rechargement (fin de partie, menu pause), "JOUER SOLO" remettait à faux le drapeau
                // d'une instance détruite : celle de la scène courante gardait isMapSelectorOpen à
                // vrai, et le dock de déploiement comme la barre tactique restaient masqués.
                root.Q<UnityEngine.UIElements.Button>("btn-offline").clicked += () =>
                {
                    GameManagerUI self = GameManagerUI.Instance;
                    if (self == null) return;
                    self.isMapSelectorOpen = false;
                    UIScreenManager.Instance.HideAll();
                    MusicManager.SetGameplayVolume();
                    self.OnClickLoadDefaultOfflineMap();
                };
                // 2026-09-30 : COMPTE D'ABORD, localisation ENSUITE (et une seule fois, expliquée
                // avant la boîte système — voir EnsureOnlineZoneReady). Avant, ce bouton lançait
                // directement le GPS : la demande d'autorisation Android surgissait sans aucune
                // explication, puis un chargement, et SEULEMENT ENSUITE le formulaire de connexion.
                root.Q<UnityEngine.UIElements.Button>("btn-multiplayer").clicked += () =>
                {
                    if (GameManagerUI.Instance != null) GameManagerUI.Instance.gpsStatus = "";
                    MusicManager.SetGameplayVolume();
                    Novgov.Network.MultiplayerMatchController.EnsureInstance().BeginLoginFlow();
                };
                startupButtonsBound = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameManagerUI] Liaison des boutons du menu de démarrage échouée : {ex.GetType().Name} — {ex.Message}");
            }
        }

        gpsStatusLabel = root.Q<Label>("gps-status-label");
        zoneMapButton = root.Q<UnityEngine.UIElements.Button>("btn-zone-map");
        BindLocationPromptOnce();

        // Enchaînement demandé AVANT le rechargement de scène (voir ReloadSceneThen) : on saute le
        // menu de démarrage au lieu d'y renvoyer le joueur à chaque fin de partie.
        AfterReloadAction pending = pendingAfterReload;
        pendingAfterReload = AfterReloadAction.None;
        if (pending == AfterReloadAction.StartSolo)
        {
            isMapSelectorOpen = false;
            UIScreenManager.Instance.HideAll();
            MusicManager.SetGameplayVolume();
            OnClickLoadDefaultOfflineMap();
        }
        else if (pending == AfterReloadAction.OpenOnlineHub)
        {
            MusicManager.SetGameplayVolume();
            Novgov.Network.MultiplayerMatchController.EnsureInstance().BeginLoginFlow();
        }
        // Menu de démarrage prioritaire : ce Show() doit s'exécuter même si l'écran "Error"
        // (non critique) échoue à se lier plus bas — sans ce garde-fou, une seule exception dans
        // le bloc suivant empêchait TOUT le menu de s'afficher (écran totalement vide au lancement).
        else if (isMapSelectorOpen)
        {
            UIScreenManager.Instance.Show("StartupMenu");
            MusicManager.SetMenuVolume();
        }

        VisualElement errorRoot = UIScreenManager.Instance.GetScreen("Error");
        if (errorRoot == null)
        {
            Debug.LogWarning("[GameManagerUI] Écran 'Error' introuvable (UXML non chargé) — ShowError() se contentera de logger dans la Console.");
            return;
        }
        errorTextLabel = errorRoot.Q<Label>("error-text");
        UnityEngine.UIElements.Button btnLoadOffline = errorRoot.Q<UnityEngine.UIElements.Button>("btn-load-offline");
        // userData sert de marqueur anti double-abonnement (même pattern que
        // TacticalPathManager_Execution.ShowSoloGameOver) : ce bouton persistant serait sinon rebranché
        // à chaque rechargement de scène, comme "btn-offline"/"btn-multiplayer" ci-dessus.
        if (btnLoadOffline != null && btnLoadOffline.userData == null)
        {
            btnLoadOffline.userData = true;
            btnLoadOffline.clicked += () =>
            {
                // Même reset que "btn-offline" ci-dessus : sans lui, UnitSpawnerUI.IsStartupSelectionActive
                // resterait vrai et le dock de déploiement resterait caché après ce chemin de secours.
                GameManagerUI self = GameManagerUI.Instance;
                if (self == null) return;
                self.isMapSelectorOpen = false;
                UIScreenManager.Instance.SetVisible("Error", false);
                self.OnClickLoadDefaultOfflineMap();
            };
        }
    }

    /// <summary>Retour au menu de démarrage (Solo / En ligne) — appelé depuis le bouton RETOUR de
    /// l'écran de connexion, "MENU PRINCIPAL" / "Se déconnecter" du Quartier Général, ou un
    /// abandon de la mise en place du QG (voir MultiplayerMatchController), qui ne connaissent pas
    /// ce menu directement. <paramref name="message"/> (facultatif) s'affiche sous les deux boutons,
    /// ex. la raison d'un échec ; tout message précédent est effacé sinon.</summary>
    public void ReturnToStartupMenu(string message = null)
    {
        if (onlineZoneRoutine != null) { StopCoroutine(onlineZoneRoutine); onlineZoneRoutine = null; }
        gpsStatus = message ?? "";
        isMapSelectorOpen = true;
        if (UIScreenManager.Instance != null)
        {
            UIScreenManager.Instance.Show("StartupMenu");
            MusicManager.SetMenuVolume();
        }
    }

    // --- Mise en place du QG en ligne (localisation expliquée + chargement de la Zone) -----------

    private System.Action<bool> pendingLocationChoice;
    private UnityEngine.UIElements.Button locationAllowButton, locationCancelButton;

    /// <summary>Câblé une seule fois (écran persistant, voir startupButtonsBound) : les callbacks
    /// passent par GameManagerUI.Instance, recréé à chaque rechargement de scène.</summary>
    private void BindLocationPromptOnce()
    {
        VisualElement promptRoot = UIScreenManager.Instance.GetScreen("LocationPrompt");
        if (promptRoot == null)
        {
            Debug.LogWarning("[GameManagerUI] Écran 'LocationPrompt' introuvable (UXML non chargé) — la localisation sera demandée sans explication préalable.");
            return;
        }
        locationStatusLabel = promptRoot.Q<Label>("location-status-label");
        locationAllowButton = promptRoot.Q<UnityEngine.UIElements.Button>("btn-location-allow");
        locationCancelButton = promptRoot.Q<UnityEngine.UIElements.Button>("btn-location-cancel");
        if (locationAllowButton != null && locationAllowButton.userData == null)
        {
            locationAllowButton.userData = true;
            locationAllowButton.clicked += () => GameManagerUI.Instance?.ResolveLocationChoice(true);
        }
        if (locationCancelButton != null && locationCancelButton.userData == null)
        {
            locationCancelButton.userData = true;
            locationCancelButton.clicked += () => GameManagerUI.Instance?.ResolveLocationChoice(false);
        }
    }

    private void ResolveLocationChoice(bool allow)
    {
        var callback = pendingLocationChoice;
        pendingLocationChoice = null;
        callback?.Invoke(allow);
    }

    /// <summary>Garantit, avant d'ouvrir le Quartier Général, que (1) la Zone d'origine du joueur
    /// est fixée — en expliquant POURQUOI la position est demandée avant la boîte système, la toute
    /// première fois seulement — et (2) que sa Zone courante est chargée dans cette scène. Appelé
    /// par MultiplayerMatchController UNE FOIS LE COMPTE CONNECTÉ (le GPS passait avant la connexion
    /// jusqu'au 2026-09-30). <paramref name="onCancelled"/> : le joueur a renoncé à placer son QG.</summary>
    public void EnsureOnlineZoneReady(System.Action onReady, System.Action onCancelled)
    {
        if (onlineZoneRoutine != null) StopCoroutine(onlineZoneRoutine);
        onlineZoneRoutine = StartCoroutine(EnsureOnlineZoneReadyRoutine(onReady, onCancelled));
    }

    private IEnumerator EnsureOnlineZoneReadyRoutine(System.Action onReady, System.Action onCancelled)
    {
        Novgov.Generation.ZoneManager zoneManager = Novgov.Generation.ZoneManager.EnsureInstance();

        // Boucle : un échec GPS (signal introuvable, autorisation refusée...) reste affiché SUR cet
        // écran, avec le bouton pour réessayer — au lieu de renvoyer au menu de démarrage.
        while (!zoneManager.HasHomeZone)
        {
            bool? choice = null;
            pendingLocationChoice = allow => choice = allow;
            SetLocationButtonsEnabled(true);
            // Pas de Show() répété après un échec : il rejouerait l'animation d'apparition de
            // l'écran déjà affiché (clignotement) au moment où le joueur lit le message d'erreur.
            if (UIScreenManager.Instance != null && !UIScreenManager.Instance.IsVisible("LocationPrompt"))
                UIScreenManager.Instance.Show("LocationPrompt");
            while (choice == null) yield return null;

            if (choice == false)
            {
                onlineZoneRoutine = null;
                gpsStatus = "";
                onCancelled?.Invoke();
                yield break;
            }

            SetLocationButtonsEnabled(false);
            yield return AcquireHomeZoneFromGps();
        }

        if (onlineZoneLoadedTile != (zoneManager.CurrentTileX, zoneManager.CurrentTileY))
        {
            isMapSelectorOpen = false;
            gpsStatus = "";
            UIScreenManager.Instance?.Show("Loading");

            // Charge la Zone courante du joueur (son origine, ou la dernière Zone visitée) — plus
            // aucune référence à une position GPS brute ici, uniquement l'index de tuile géré par
            // ZoneManager. Plus de déploiement automatique : la carte se charge vide.
            zoneManager.LoadCurrentZone();
            yield return new WaitForSeconds(0.5f);

            UIScreenManager.Instance?.SetVisible("Loading", false);
            onlineZoneLoadedTile = (zoneManager.CurrentTileX, zoneManager.CurrentTileY);
        }

        isMapSelectorOpen = false;
        onlineZoneRoutine = null;
        onReady?.Invoke();
    }

    private void SetLocationButtonsEnabled(bool enabled)
    {
        locationAllowButton?.SetEnabled(enabled);
        locationCancelButton?.SetEnabled(enabled);
    }
#endif

    /// <summary>
    /// Active le GPU Instancing sur les matériaux de la scène pour minimiser les Draw Calls.
    /// </summary>
    public static void OptimizeSceneMaterials()
    {
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include);
        int count = 0;
        foreach (var r in renderers)
        {
            if (r == null) continue;
            foreach (var mat in r.sharedMaterials)
            {
                if (mat != null && !mat.enableInstancing && !mat.name.StartsWith("Default") && mat.name != "Lit" && mat.name != "LiberationSans SDF Material")
                {
                    mat.enableInstancing = true;
                    count++;
                }
            }
        }
        if (count > 0)
        {
            Debug.Log($"<color=green>[Performance Optimizer] ⚡ GPU Instancing activé sur {count} matériaux de la scène !</color>");
        }
    }

    public void ShowError(string message)
    {
        UIScreenManager.Instance?.SetVisible("Loading", false);
        if (UIScreenManager.Instance != null && errorTextLabel != null)
        {
            if (errorTextLabel != null) errorTextLabel.text = message;
            UIScreenManager.Instance.SetVisible("Error", true);
        }
        else
        {
            Debug.LogError("GAME ERROR: " + message);
        }
    }

    public void OnClickLoadDefaultOfflineMap()
    {
#if !UNITY_SERVER
        // La carte solo remplace la Zone réelle éventuellement chargée pour le jeu en ligne : un
        // prochain "JOUER EN LIGNE" devra la recharger (voir EnsureOnlineZoneReady).
        onlineZoneLoadedTile = null;
#endif
        UIScreenManager.Instance?.SetVisible("Error", false);
        UIScreenManager.Instance?.SetVisible("Loading", true);
        StartCoroutine(LoadOfflineRoutine());
    }

    private IEnumerator LoadOfflineRoutine()
    {
        MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();

        if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
        if (cityGen != null) cityGen.LoadDefaultOfflineCity();

        yield return new WaitForSeconds(0.4f);

        // Plus de déploiement automatique : la carte se charge vide, le joueur place lui-même ses
        // unités via le dock "QG Renforts" (voir UnitSpawnerUI.Start()).
        UIScreenManager.Instance?.SetVisible("Loading", false);
    }

    public void HideLoading()
    {
        UIScreenManager.Instance?.SetVisible("Loading", false);
        UIScreenManager.Instance?.SetVisible("Error", false);
    }

    // Position simulée pour les tests en Éditeur — le service Input.location d'Unity n'est de toute
    // façon jamais fonctionnel dans l'Éditeur (pas de matériel GPS), donc ce court-circuit ne retire
    // aucune fonctionnalité réelle : sans lui, la lecture GPS finissait systématiquement par
    // échouer après 20s d'attente ("Impossible de capter le signal GPS"), rendant tout test du flux
    // multijoueur en Play Mode impossible sans passer par un vrai appareil.
    //
    // 2026-09-06 : d'abord une liste de centres-villes réels ÉLOIGNÉS (Lille/Paris/Lyon/Marseille)
    // pour éviter qu'Éditeur principal et Joueurs Virtuels ne tapent tous EXACTEMENT la même bbox
    // Overpass au même instant ("Échec de connexion au serveur OSM" en testant à deux instances).
    // Mais ça rendait le test PvP réel impossible : ATTAQUER (CONQUÊTE) n'affiche que 4 boutons
    // Nord/Sud/Est/Ouest relatifs à la Zone COURANTE (voir ZoneMapController), donc deux comptes
    // dans des villes différentes ne peuvent jamais viser la Zone de l'autre — chaque attaque ne
    // fait alors que capturer une Zone neutre (zone_captured) et revient au menu, sans jamais
    // déclencher de vraie bataille (match_found), ce qui ressemblait à un bug de navigation mais
    // n'en était pas un (voir MatchSessionManager.RunConquestRequest côté serveur).
    //
    // Remplacé par 4 points calculés (voir script Python de vérification, jamais commité) EXACTEMENT
    // à 2 tuiles Slippy Zoom 17 (~390m à cette latitude) de la Zone de l'Éditeur principal — PAS 1 :
    // à 1 tuile, la Zone de chacun est directement voisine de celle de l'autre, mais on ne peut
    // jamais attaquer sa PROPRE Zone courante (ce n'est le voisin de personne), donc aucun des deux
    // ne peut jamais riposter sur la Zone exacte que l'autre vient de prendre. À 2 tuiles, il existe
    // une Zone neutre PARTAGÉE entre les deux (voisin Est de l'Éditeur principal = voisin Ouest du
    // Joueur Virtuel) : le premier qui l'attaque la capture (neutre), puis l'autre l'attaque à son
    // tour et tombe cette fois sur un propriétaire RÉEL différent de lui → vraie bataille. Chaque
    // point reste sur une tuile différente de celle de l'Éditeur principal (bbox Overpass différente
    // — le problème de requêtes simultanées identiques reste évité).
#if UNITY_EDITOR
    // Tout ce bloc référence Unity.Multiplayer.PlayMode (package Éditeur uniquement, jamais présent
    // dans un build Android/iOS) — englobé dans #if UNITY_EDITOR à partir d'ici (correctif : avant,
    // seul l'appel dans StartDeviceGPS() était gardé, pas la définition de la méthode elle-même, ce
    // qui aurait cassé la compilation d'un build appareil dès que ce code y serait aussi compilé).
    private static readonly (string Name, float Lat, float Lon)[] EditorMockCities =
    {
        ("Lille Sud",           50.5975f,    3.0553f),     // Éditeur principal — Zone (66648, 44111)
        ("Lille Sud (Est)",     50.5975f,    3.061066f),   // 2 tuiles EST      — Zone (66650, 44111)
        ("Lille Sud (Sud)",     50.594571f,  3.0553f),     // 2 tuiles SUD      — Zone (66648, 44113)
        ("Lille Sud (Nord)",    50.601544f,  3.0553f),     // 2 tuiles NORD     — Zone (66648, 44109)
    };

    // Tag (Window > Multiplayer Play Mode > [Joueur] > Tags) pour FORCER une ville précise sur un
    // Joueur Virtuel donné plutôt que de laisser le choix automatique ci-dessous décider : nommer le
    // tag "TestCity0", "TestCity1"... (indices de EditorMockCities). Optionnel — sans tag, chaque
    // clone reçoit quand même une ville différente basée sur son propre dossier de projet cloné.
    private const string EditorMockCityTagPrefix = "TestCity";

    /// <summary>Choisit la ville simulée pour CETTE instance Éditeur. L'Éditeur principal (ou une
    /// session Play Mode normale sans Multiplayer Play Mode actif) reçoit toujours la première
    /// entrée (comportement historique inchangé, Lille Sud). Un Joueur Virtuel reçoit soit la ville
    /// forcée par son tag, soit — à défaut — une ville dérivée du hash de son propre Application.
    /// dataPath (chemin du clone), stable d'une session à l'autre pour CE clone mais différent des
    /// autres instances qui tournent en même temps.</summary>
    // Non-private (voir EditorDebugOverlay.cs) : le panneau de diagnostic Éditeur affiche cette même
    // ville simulée sans dupliquer la logique de choix.
    public static (string Name, float Lat, float Lon) PickEditorMockCity()
    {
        if (Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor) return EditorMockCities[0];

        string[] tags = Unity.Multiplayer.PlayMode.CurrentPlayer.ReadOnlyTags();
        if (tags != null)
        {
            for (int i = 0; i < EditorMockCities.Length; i++)
            {
                if (System.Array.IndexOf(tags, EditorMockCityTagPrefix + i) >= 0) return EditorMockCities[i];
            }
        }

        int range = EditorMockCities.Length - 1;
        int offset = ((Application.dataPath.GetHashCode() % range) + range) % range;
        return EditorMockCities[1 + offset];
    }
#endif

    /// <summary>Lit la position de l'appareil UNE fois et fixe la Zone d'origine (voir
    /// ZoneManager.InitializeHomeZoneFromGps). En cas d'échec, laisse la raison — formulée avec
    /// la marche à suivre — dans <see cref="gpsStatus"/>, affiché sur l'écran LocationPrompt où le
    /// joueur peut réessayer (voir EnsureOnlineZoneReadyRoutine). Le chargement de la ville et la
    /// connexion au compte, autrefois enchaînés ici (ancien StartDeviceGPS), n'en font plus partie.</summary>
    private IEnumerator AcquireHomeZoneFromGps()
    {
        Novgov.Generation.ZoneManager zoneManager = Novgov.Generation.ZoneManager.EnsureInstance();

        // Le GPS n'est consulté QUE tant qu'aucune Zone d'origine n'a encore été fixée — une fois
        // ZoneManager.InitializeHomeZoneFromGps() appelé une première fois (persisté via
        // PlayerPrefs, y compris entre deux lancements de l'app), le jeu ne raisonne plus qu'en
        // (tileX, tileY), voir ZoneManager.
        if (!zoneManager.HasHomeZone)
        {
            float lat, lon;

#if UNITY_EDITOR
            var mockCity = PickEditorMockCity();
            gpsStatus = $"[Éditeur] Position simulée : {mockCity.Name}.";
            lat = mockCity.Lat;
            lon = mockCity.Lon;
            yield return new WaitForSeconds(0.3f);
#else
            gpsStatus = "Recherche de votre position...";

#if UNITY_ANDROID
            // Android 6+ : la permission déclarée dans le manifeste ne suffit pas, il faut la demander
            // explicitement à l'exécution pour déclencher la boîte de dialogue système — sans ça,
            // chaque joueur devrait aller l'activer à la main dans les paramètres de l'appareil comme
            // il a fallu le faire manuellement pendant les tests.
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                gpsStatus = "Autorisez l'accès à la position dans la fenêtre qui s'ouvre...";
                bool permissionResolved = false;
                bool permissionGranted = false;

                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => { permissionGranted = true; permissionResolved = true; };
                callbacks.PermissionDenied += _ => { permissionResolved = true; };
                Permission.RequestUserPermission(Permission.FineLocation, callbacks);

                while (!permissionResolved) yield return null;

                if (!permissionGranted)
                {
                    gpsStatus = "Localisation refusée. Touchez à nouveau PLACER MON QG ICI et choisissez « Autoriser » (ou activez-la dans les paramètres du téléphone).";
                    yield break;
                }
            }
#endif

            if (!Input.location.isEnabledByUser)
            {
                gpsStatus = "La localisation du téléphone est désactivée : activez-la, puis touchez à nouveau PLACER MON QG ICI.";
                yield break;
            }

            Input.location.Start(10f, 10f);
            int maxWait = 20;
            while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
            {
                yield return new WaitForSeconds(1);
                maxWait--;
            }

            if (maxWait < 1 || Input.location.status == LocationServiceStatus.Failed)
            {
                gpsStatus = "Position introuvable pour l'instant (signal GPS faible). Rapprochez-vous d'une fenêtre, puis réessayez.";
                yield break;
            }

            lat = (float)Input.location.lastData.latitude;
            lon = (float)Input.location.lastData.longitude;
            Input.location.Stop();
#endif

            gpsStatus = "Position trouvée ! Votre QG est installé.";
            zoneManager.InitializeHomeZoneFromGps(lat, lon);
            yield return new WaitForSeconds(0.6f);
        }
    }
}
