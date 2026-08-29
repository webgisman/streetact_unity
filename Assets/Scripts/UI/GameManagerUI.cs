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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigurePerformanceSettings()
    {
        // 1. Verrouillage du Framerate à 60 FPS pour éviter la surchauffe et économiser la batterie sur 60Hz/120Hz
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
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
        Application.targetFrameRate = 60;

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
    private void Update()
    {
        if (gpsStatusLabel != null) gpsStatusLabel.text = gpsStatus;
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
                root.Q<UnityEngine.UIElements.Button>("btn-offline").clicked += () =>
                {
                    isMapSelectorOpen = false;
                    UIScreenManager.Instance.HideAll();
                    MusicManager.SetGameplayVolume();
                    GameManagerUI.Instance?.OnClickLoadDefaultOfflineMap();
                };
                root.Q<UnityEngine.UIElements.Button>("btn-multiplayer").clicked += () =>
                {
                    MusicManager.SetGameplayVolume();
                    GameManagerUI.Instance?.StartCoroutine(GameManagerUI.Instance.StartDeviceGPS(thenConnectMultiplayer: true));
                };
                startupButtonsBound = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameManagerUI] Liaison des boutons du menu de démarrage échouée : {ex.GetType().Name} — {ex.Message}");
            }
        }

        gpsStatusLabel = root.Q<Label>("gps-status-label");

        // Menu de démarrage prioritaire : ce Show() doit s'exécuter même si l'écran "Error"
        // (non critique) échoue à se lier plus bas — sans ce garde-fou, une seule exception dans
        // le bloc suivant empêchait TOUT le menu de s'afficher (écran totalement vide au lancement).
        if (isMapSelectorOpen)
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

    /// <summary>Retour au menu de démarrage (choix hors-ligne/GPS/multijoueur) — appelé depuis le
    /// bouton RETOUR des boîtes de dialogue de connexion/création de compte
    /// (voir MultiplayerMatchController), qui ne connaissent pas ce menu directement.</summary>
    public void ReturnToStartupMenu()
    {
        isMapSelectorOpen = true;
        if (UIScreenManager.Instance != null)
        {
            UIScreenManager.Instance.Show("StartupMenu");
            MusicManager.SetMenuVolume();
        }
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

    /// <summary>Géolocalise l'appareil, génère la ville réelle correspondante, puis — si
    /// <paramref name="thenConnectMultiplayer"/> est vrai (bouton MODE CAMPAGNE MULTIJOUEUR) —
    /// enchaîne directement sur la connexion PvP une fois la carte prête. En mode solo, le GPS
    /// n'est jamais utilisé : ce paramètre est donc toujours vrai pour l'unique appelant restant.</summary>
    private IEnumerator StartDeviceGPS(bool thenConnectMultiplayer)
    {
        gpsStatus = "Recherche du signal GPS de l'appareil...";

#if UNITY_ANDROID
        // Android 6+ : la permission déclarée dans le manifeste ne suffit pas, il faut la demander
        // explicitement à l'exécution pour déclencher la boîte de dialogue système — sans ça,
        // chaque joueur devrait aller l'activer à la main dans les paramètres de l'appareil comme
        // il a fallu le faire manuellement pendant les tests.
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            gpsStatus = "Autorise l'accès à la position pour continuer...";
            bool permissionResolved = false;
            bool permissionGranted = false;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => { permissionGranted = true; permissionResolved = true; };
            callbacks.PermissionDenied += _ => { permissionResolved = true; };
            Permission.RequestUserPermission(Permission.FineLocation, callbacks);

            while (!permissionResolved) yield return null;

            if (!permissionGranted)
            {
                gpsStatus = "Autorisation de localisation refusée. Active-la dans les paramètres de l'appareil pour utiliser le GPS.";
                yield break;
            }
        }
#endif

        if (!Input.location.isEnabledByUser)
        {
            gpsStatus = "GPS désactivé dans les paramètres du téléphone.";
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
            gpsStatus = "Impossible de capter le signal GPS.";
            yield break;
        }

        float lat = (float)Input.location.lastData.latitude;
        float lon = (float)Input.location.lastData.longitude;
        gpsStatus = "Coordonnées acquises ! Génération de votre ville...";
        Input.location.Stop();

        yield return new WaitForSeconds(0.6f);
        isMapSelectorOpen = false;
#if !UNITY_SERVER
        if (UIScreenManager.Instance != null) UIScreenManager.Instance.HideAll();
#endif

        // Générer la ville à la position GPS réelle avec 250m de rayon
        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();

        UIScreenManager.Instance?.SetVisible("Loading", true);

        if (cityGen != null)
        {
            cityGen.latitude = lat;
            cityGen.longitude = lon;
            cityGen.radius = 250f;
            cityGen.GenerateCity();
        }

        if (mapLoader != null)
        {
            mapLoader.LoadMap();
        }

        yield return new WaitForSeconds(0.5f);

        // Plus de déploiement automatique : la carte se charge vide, le joueur place lui-même ses
        // unités via le dock "QG Renforts" (voir UnitSpawnerUI.Start()) — sauf en enchaînement
        // multijoueur ci-dessous, où c'est l'écran de connexion qui prend immédiatement la main.
        UIScreenManager.Instance?.SetVisible("Loading", false);

        if (thenConnectMultiplayer)
        {
#if !UNITY_SERVER
            Novgov.Network.MultiplayerMatchController.EnsureInstance().BeginLoginFlow();
#endif
        }
    }
}
