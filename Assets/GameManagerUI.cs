using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;
using System.Collections;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class GameManagerUI : MonoBehaviour
{
    [Header("UI Panels")]
    public GameObject loadingPanel;
    public GameObject errorPanel;
    
    [Header("UI Text")]
    public Text errorText;
    
    private static GameManagerUI instance;
    public static GameManagerUI Instance { get { return instance; } }

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

        CanvasGroupFader.SetVisible(errorPanel, false);
        CanvasGroupFader.SetVisible(loadingPanel, false);

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
        root.Q<UnityEngine.UIElements.Button>("btn-offline").clicked += () =>
        {
            isMapSelectorOpen = false;
            UIScreenManager.Instance.HideAll();
            MusicManager.SetGameplayVolume();
            OnClickLoadDefaultOfflineMap();
        };
        root.Q<UnityEngine.UIElements.Button>("btn-gps").clicked += () =>
        {
            MusicManager.SetGameplayVolume();
            StartCoroutine(StartDeviceGPS());
        };
        root.Q<UnityEngine.UIElements.Button>("btn-multiplayer").clicked += () =>
        {
            isMapSelectorOpen = false;
            UIScreenManager.Instance.HideAll();
            Novgov.Network.MultiplayerMatchController.EnsureInstance().BeginLoginFlow();
        };
        root.Q<UnityEngine.UIElements.Button>("btn-play-current").clicked += () =>
        {
            isMapSelectorOpen = false;
            UIScreenManager.Instance.HideAll();
            MusicManager.SetGameplayVolume();
        };

        gpsStatusLabel = root.Q<Label>("gps-status-label");

        if (isMapSelectorOpen)
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
        CanvasGroupFader.SetVisible(loadingPanel, false);
        if (errorPanel != null)
        {
            CanvasGroupFader.SetVisible(errorPanel, true);
            if (errorText != null) errorText.text = message;
        }
        else
        {
            Debug.LogError("GAME ERROR: " + message);
        }
    }

    public void OnClickLoadDefaultOfflineMap()
    {
        CanvasGroupFader.SetVisible(errorPanel, false);
        CanvasGroupFader.SetVisible(loadingPanel, true);
        StartCoroutine(LoadOfflineRoutine());
    }
    
    private IEnumerator LoadOfflineRoutine()
    {
        MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        
        if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
        if (cityGen != null) cityGen.LoadDefaultOfflineCity();
        
        yield return new WaitForSeconds(0.4f);

        if (UnitSpawnerUI.Instance != null)
        {
            UnitSpawnerUI.Instance.AutoDeployBattlefield();
        }

        CanvasGroupFader.SetVisible(loadingPanel, false);
    }
    
    public void HideLoading()
    {
        CanvasGroupFader.SetVisible(loadingPanel, false);
        CanvasGroupFader.SetVisible(errorPanel, false);
    }

    private IEnumerator StartDeviceGPS()
    {
        gpsStatus = "📡 Recherche du signal GPS de l'appareil...";

#if UNITY_ANDROID
        // Android 6+ : la permission déclarée dans le manifeste ne suffit pas, il faut la demander
        // explicitement à l'exécution pour déclencher la boîte de dialogue système — sans ça,
        // chaque joueur devrait aller l'activer à la main dans les paramètres de l'appareil comme
        // il a fallu le faire manuellement pendant les tests.
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            gpsStatus = "📍 Autorise l'accès à la position pour continuer...";
            bool permissionResolved = false;
            bool permissionGranted = false;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => { permissionGranted = true; permissionResolved = true; };
            callbacks.PermissionDenied += _ => { permissionResolved = true; };
            Permission.RequestUserPermission(Permission.FineLocation, callbacks);

            while (!permissionResolved) yield return null;

            if (!permissionGranted)
            {
                gpsStatus = "⚠️ Autorisation de localisation refusée. Active-la dans les paramètres de l'appareil pour utiliser le GPS.";
                yield break;
            }
        }
#endif

        if (!Input.location.isEnabledByUser)
        {
            gpsStatus = "⚠️ GPS désactivé dans les paramètres du téléphone.";
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
            gpsStatus = "⚠️ Impossible de capter le signal GPS.";
            yield break;
        }

        float lat = (float)Input.location.lastData.latitude;
        float lon = (float)Input.location.lastData.longitude;
        gpsStatus = "✅ Coordonnées acquises ! Génération de votre ville...";
        Input.location.Stop();

        yield return new WaitForSeconds(0.6f);
        isMapSelectorOpen = false;
#if !UNITY_SERVER
        if (UIScreenManager.Instance != null) UIScreenManager.Instance.HideAll();
#endif

        // Générer la ville à la position GPS réelle avec 250m de rayon
        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();

        CanvasGroupFader.SetVisible(loadingPanel, true);

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

        if (UnitSpawnerUI.Instance != null)
        {
            UnitSpawnerUI.Instance.AutoDeployBattlefield();
        }

        CanvasGroupFader.SetVisible(loadingPanel, false);
    }
}
