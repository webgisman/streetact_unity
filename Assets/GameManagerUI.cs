using UnityEngine;
using UnityEngine.UI;
using System.Collections;

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
    private float startupPanelFade = 0f; // Anim. d'apparition (pop + fondu) de l'écran de démarrage

    private Texture2D overlayDimTex;

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

        // Texture d'assombrissement pour l'écran d'accueil
        overlayDimTex = new Texture2D(1, 1);
        overlayDimTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
        overlayDimTex.Apply();
    }

    private void Start()
    {
        OptimizeSceneMaterials();
    }

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

    void OnGUI()
    {
        // ÉCRAN DE DÉMARRAGE : CHOIX DU THÉÂTRE D'OPÉRATIONS AVANT DE JOUER
        if (isMapSelectorOpen)
        {
            GUI.depth = -200; // Priorité absolue au-dessus de tout le jeu au démarrage

            // Fond noir translucide couvrant tout l'écran
            if (overlayDimTex != null)
            {
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), overlayDimTex);
            }

            Matrix4x4 origMat = GUI.matrix;
            float uiScale = Mathf.Clamp(Screen.width / 450f, 1.35f, 2.2f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float virtualW = Screen.width / uiScale;
            float virtualH = Screen.height / uiScale;

            float w = Mathf.Min(380f, virtualW - 30f);
            float h = 310f;
            float x = (virtualW - w) * 0.5f;
            float y = (virtualH - h) * 0.5f;

            startupPanelFade = UIAnimator.Advance(startupPanelFade, true, 6f);
            Rect poppedPanel = UIAnimator.PopRect(new Rect(x, y, w, h), startupPanelFade);
            x = poppedPanel.x; y = poppedPanel.y; w = poppedPanel.width; h = poppedPanel.height;
            UIAnimator.ApplyFadeColor(startupPanelFade);

            GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.fontSize = 15;
            boxStyle.fontStyle = FontStyle.Bold;
            boxStyle.normal.textColor = Color.white;

            GUI.Box(new Rect(x, y, w, h), "⚔️ STREETACT : CHAMP DE BATAILLE", boxStyle);

            GUIStyle btnStyle1 = new GUIStyle(GUI.skin.button);
            btnStyle1.fontSize = 13;
            btnStyle1.fontStyle = FontStyle.Bold;
            btnStyle1.normal.textColor = Color.cyan;

            // OPTION 1 : Carte Hors-Ligne par défaut
            if (ProceduralIconFactory.IconButton(new Rect(x + 15, y + 50, w - 30, 62), ProceduralIconFactory.House(), "🏙️ 1. COMBAT URBAIN HORS-LIGNE\n(Chargement Immédiat)", btnStyle1))
            {
                isMapSelectorOpen = false;
                OnClickLoadDefaultOfflineMap();
            }

            GUIStyle btnStyle2 = new GUIStyle(GUI.skin.button);
            btnStyle2.fontSize = 13;
            btnStyle2.fontStyle = FontStyle.Bold;
            btnStyle2.normal.textColor = new Color(0.3f, 1f, 0.4f);

            // OPTION 2 : Ma Position GPS Réelle
            if (ProceduralIconFactory.IconButton(new Rect(x + 15, y + 124, w - 30, 62), ProceduralIconFactory.Eye(), "🛰️ 2. MA POSITION GPS RÉELLE\n(Géolocalisation Directe)", btnStyle2))
            {
                StartCoroutine(StartDeviceGPS());
            }

            if (!string.IsNullOrEmpty(gpsStatus))
            {
                GUIStyle statusStyle = new GUIStyle(GUI.skin.label);
                statusStyle.alignment = TextAnchor.MiddleCenter;
                statusStyle.normal.textColor = Color.yellow;
                statusStyle.fontSize = 12;
                GUI.Label(new Rect(x + 15, y + 196, w - 30, 35), gpsStatus, statusStyle);
            }

            // Bouton Quitter / Fermer (Jouer immédiatement avec la scène actuelle)
            if (ProceduralIconFactory.IconButton(new Rect(x + 15, y + 242, w - 30, 48), ProceduralIconFactory.Check(), "▶️ JOUER (Terrain Actuel)"))
            {
                isMapSelectorOpen = false;
            }

            GUI.color = Color.white;
            GUI.matrix = origMat;
        }
        else
        {
            if (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Action) return;

            Matrix4x4 origMat = GUI.matrix;
            float uiScale = Mathf.Clamp(Screen.width / 450f, 1.35f, 2.2f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float virtualW = Screen.width / uiScale;
            
            GUIStyle restartStyle = new GUIStyle(GUI.skin.button);
            restartStyle.fontSize = 10;
            restartStyle.fontStyle = FontStyle.Bold;
            restartStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.85f);

            // Bouton discret et épuré
            if (ProceduralIconFactory.IconButton(new Rect(virtualW * 0.5f - 55, 10, 110, 30), ProceduralIconFactory.Reset(), "🔄 Recommencer", restartStyle))
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }

            GUI.matrix = origMat;
        }
    }

    private IEnumerator StartDeviceGPS()
    {
        gpsStatus = "📡 Recherche du signal GPS de l'appareil...";
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
