using UnityEngine;
using System;

/// <summary>
/// Gère les 2 états de la caméra : 2D (Commandement) et 3D (Action).
/// Vue 2D : Carte plane haute définition, bâtiments polygonaux 2D, icônes militaires, zoom fixe ultra-lisible.
/// Vue 3D : Rendu 3D complet avec limitation stricte à un cercle de 20-30m (Brouillard tactique sombre).
/// Bouton de sortie 3D ultra-visible et accessible.
/// </summary>
public class CameraStateManager : MonoBehaviour
{
    public static CameraStateManager Instance;

    public enum CameraState { Command, Action }
    
    [Header("Paramètres de la Vue Action (3D)")]
    public float actionDistance = 65f; // Recul optimal pour une vue isométrique ample et immersive
    public float actionPitch = 50f;
    public float actionFarClip = 400f;

    [Header("Paramètres de la Vue Commandement (2D)")]
    public float commandDistance = 90f;
    public float commandPitch = 90f;
    public float commandFarClip = 500f;
    public float commandOrthographicSize = 95f; // Recul panoramique large pour une vision tactique globale

    [Header("Culling Layers (Calculés Automatiquement)")]
    private LayerMask actionViewMask;
    private LayerMask commandViewMask;

    public static event Action<CameraState> OnCameraStateChanged;
    public CameraState CurrentState { get; private set; } = CameraState.Command;
    private Camera mainCamera;

    private Transform focusedUnit = null;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);

        mainCamera = Camera.main;

        // -- CONFIGURATION DYNAMIQUE DES LAYERS --
        int markersLayer = LayerMask.NameToLayer("Units_UI_Markers");
        int units3DLayer = LayerMask.NameToLayer("Units_3D");

        if (markersLayer != -1)
        {
            // En vue 3D : On voit tout SAUF les marqueurs 2D
            actionViewMask = ~(1 << markersLayer);
            
            // En vue 2D : On voit tout SAUF les modèles 3D complexes des unités (remplacés par les icônes)
            if (units3DLayer != -1) commandViewMask = ~(1 << units3DLayer);
            else commandViewMask = -1;
        }

        // Maximiser le framerate sur mobile
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
    }

    private void Start()
    {
        SwitchState(CameraState.Command, force: true);
        
        if (GetComponent<TacticalStreamingManager>() == null)
        {
            gameObject.AddComponent<TacticalStreamingManager>();
        }
    }

    /// <summary>
    /// Appelé par le bouton UI après avoir sélectionné une unité en 2D.
    /// </summary>
    public void Enter3DView(Transform target)
    {
        focusedUnit = target;
        SwitchState(CameraState.Action);
    }

    /// <summary>
    /// Appelé par le bouton UI pour revenir à la carte 2D globale.
    /// </summary>
    public void ReturnTo2DView()
    {
        focusedUnit = null;
        SwitchState(CameraState.Command);
    }

    public void SwitchState(CameraState newState, bool force = false)
    {
        if (!force && CurrentState == newState) return;

        CurrentState = newState;

        if (mainCamera != null)
        {
            mainCamera.cullingMask = (newState == CameraState.Action) ? actionViewMask : commandViewMask;
            
            // Basculer la projection
            mainCamera.orthographic = (newState == CameraState.Command);
            if (newState == CameraState.Command)
            {
                mainCamera.orthographicSize = commandOrthographicSize;
                QualitySettings.shadowDistance = 0f; // Aucune ombre en 2D pour booster la fluidité
            }
            else
            {
                QualitySettings.shadowDistance = 60f; // Ombres douces en 3D
            }

            mainCamera.farClipPlane = (newState == CameraState.Action) ? actionFarClip : commandFarClip;

            // Gestion du Fog (Brume atmosphérique douce et lointaine en 3D)
            RenderSettings.fog = (newState == CameraState.Action);
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 350f;
            RenderSettings.fogColor = new Color(0.12f, 0.16f, 0.22f);
        }

        if (TacticalCamera.Instance != null)
        {
            float dist = (newState == CameraState.Action) ? actionDistance : commandDistance;
            float pitch = (newState == CameraState.Action) ? actionPitch : commandPitch;
            
            TacticalCamera.Instance.SetInstantView(dist, pitch);
            
            if (newState == CameraState.Action && focusedUnit != null)
            {
                TacticalCamera.Instance.focusPosition = focusedUnit.position;
            }
        }

        // --- GESTION DU STREAMING ---
        if (TacticalStreamingManager.Instance != null)
        {
            if (newState == CameraState.Action)
            {
                TacticalStreamingManager.Instance.StartStreamingOnTarget(focusedUnit);
            }
            else
            {
                TacticalStreamingManager.Instance.StopStreamingAndClear();
            }
        }

        OnCameraStateChanged?.Invoke(newState);
    }

    private void OnGUI()
    {
        if (CurrentState == CameraState.Action)
        {
            GUI.depth = -200; // Au premier plan absolu (ne peut pas être masqué par le radar)

            Matrix4x4 origMat = GUI.matrix;
            float uiScale = Mathf.Clamp(Screen.width / 480f, 1.35f, 2.2f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            GUIStyle backBtnStyle = new GUIStyle(GUI.skin.button);
            backBtnStyle.fontSize = 12;
            backBtnStyle.fontStyle = FontStyle.Bold;
            backBtnStyle.alignment = TextAnchor.MiddleCenter;
            backBtnStyle.normal.textColor = new Color(1f, 0.95f, 0.2f);

            // Bouton unique élégant en Haut à Gauche
            if (GUI.Button(new Rect(14, 12, 160, 42), "◀ VUE 2D (CARTE)", backBtnStyle))
            {
                ReturnTo2DView();
            }

            // Bannière d'état en haut au centre
            if (focusedUnit != null)
            {
                GUIStyle bannerStyle = new GUIStyle(GUI.skin.box);
                bannerStyle.fontSize = 11;
                bannerStyle.fontStyle = FontStyle.Bold;
                bannerStyle.alignment = TextAnchor.MiddleCenter;
                bannerStyle.normal.textColor = Color.cyan;
                
                float virtualW = Screen.width / uiScale;
                GUI.Box(new Rect(virtualW * 0.5f - 110, 12, 220, 36), $"🔍 ACTION 3D : {focusedUnit.name.ToUpper()}", bannerStyle);
            }

            GUI.matrix = origMat;
        }
    }
}
