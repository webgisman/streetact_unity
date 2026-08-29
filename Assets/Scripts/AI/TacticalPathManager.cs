using UnityEngine;

/// <summary>
/// Point d'entrée du gameplay tactique tour-par-tour : cycle de phases (Planification/Exécution)
/// et état de sélection partagés par tous les fichiers partiels de cette classe —
///   - TacticalPathManager_Input.cs : détection du tap/clic (sélection, cascade porte/fenêtre/bâtiment/sol)
///   - TacticalPathManager_Selection.cs : sélection d'unité (clic direct, cloche blessés, squad-bar)
///   - TacticalPathManager_ContextMenu.cs : ouverture/fermeture des menus d'ordre, confirmation
///   - TacticalPathManager_UI.cs : binding UI Toolkit (barre du bas, squad-bar, ContextMenu)
///   - TacticalPathManager_PathDrawing.cs : tracé des trajectoires (LineRenderer par unité)
///   - TacticalPathManager_Execution.cs : déroulement du tour (Fin de tour → Planification)
/// </summary>
public partial class TacticalPathManager : MonoBehaviour
{
    public enum GamePhase { Planification, CreationPath, Execution }
    public enum NodeAction { Continuer = 0, Attendre5Min = 1, Guetter = 2, Embuscade = 3, Escalade = 4, GarnisonFenetre = 5, EntrerBatiment = 6, SortirBatiment = 7, GuetterPorte = 8, Attendre30s = 9, SeCacher = 10, TirMortier = 11 }

    [Header("Système")]
    public GamePhase phaseActuelle = GamePhase.Planification;

    [System.Serializable]
    public struct TacticalNode
    {
        public Vector3 position;
        public NodeAction action;
    }

    [Header("UI Références")]
    public GameObject menuPanel;

    public GameObject uniteSelectionnee;
    // Vector3.positiveInfinity (pas Vector3.zero) comme sentinelle "aucun clic en attente" : le plan
    // "Sol" est centré sur l'origine du monde (voir MapTileLoader.GenerateQuadMesh), donc un tap au
    // centre de la carte résolvait littéralement à (0,0,0) et faisait disparaître silencieusement la
    // prévisualisation du tracé (voir DessinerTousLesChemins).
    private Vector3 positionClicTemporaire = Vector3.positiveInfinity;
    private Novgov.Interaction.DoorInteraction selectedDoor = null;
    private Novgov.Interaction.WindowInteraction selectedWindow = null;
    private BuildingStructure selectedBuilding = null;
    private RoadBarrier selectedBarricade = null;
    private bool isBuildingSelected = false;
    private bool isDoorSelected = false;
    private bool isWindowSelected = false;
    private bool isExitDoorAction = false;
    private bool isGroundCheckpointSelected = false;
    private bool isBarricadeSelected = false;
    private bool isBarricadeDeployMenuOpen = false;
    private bool isNearBuildingWall = false;

    /// <summary>Un menu d'ordre (porte/fenêtre/bâtiment/sol/barricade) est actuellement ouvert.
    /// Centralisé pour ne plus jamais oublier d'y ajouter un nouveau type de sélection dans l'un
    /// des multiples endroits qui referment un menu resté ouvert avant d'en ouvrir un autre (voir
    /// historique des bugs "trajet confirmé après ANNULER/tap invalide" — le même oubli avec un
    /// 5e flag ajouté à la main aurait pu se reproduire ici).</summary>
    private bool AnyOrderMenuOpen => isDoorSelected || isWindowSelected || isBuildingSelected || isGroundCheckpointSelected || isBarricadeSelected || isBarricadeDeployMenuOpen;

    private Color couleurOriginale;
    // Pour stocker TOUTES les parties du soldat (corps, arme, etc.)
    private Renderer[] renderersSelectionnes;
    private LineRenderer lineRenderer;
    private readonly System.Collections.Generic.List<Vector3> cachedLinePoints = new System.Collections.Generic.List<Vector3>();
    private Gradient cachedPathGradient;
    private static bool isPathsDirty = true;

    public static void SetPathsDirty() { isPathsDirty = true; }

    /// <summary>Solo uniquement (voir TacticalPathManager_Execution.CheckSoloGameOver) — un camp a
    /// été entièrement anéanti. Vérifié explicitement dans Update() plutôt que de compter
    /// uniquement sur l'ordre d'affichage de l'écran GameOver : un recouvrement visuel qui rate ne
    /// doit jamais laisser le joueur continuer à donner des ordres après la fin de la partie.
    /// Remis à false dans Awake() : un rechargement de scène via SceneManager.LoadScene ne
    /// réinitialise PAS les champs static, contrairement à un vrai redémarrage du Play Mode.</summary>
    public static bool IsSoloGameOver = false;

    public static TacticalPathManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
        IsSoloGameOver = false;
#if !UNITY_SERVER
        BindTacticalUI();
#endif
    }

    void Start()
    {
        Instance = this;
        // Initialisation automatique du LineRenderer
        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null)
        {
            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.startWidth = 0.3f;
            lineRenderer.endWidth = 0.3f;
            lineRenderer.material = SafeMaterialFactory.CreateUnlit(Color.cyan);
            lineRenderer.startColor = Color.cyan;
            lineRenderer.endColor = Color.blue;
        }
        lineRenderer.positionCount = 0;

        // Initialisation automatique du gestionnaire de déploiement d'unités
        if (GetComponent<UnitSpawnerUI>() == null)
        {
            gameObject.AddComponent<UnitSpawnerUI>();
        }

        // Initialisation automatique du Radar Tactique
        if (TacticalRadarUI.Instance != null)
        {
            Debug.Log("[TacticalPathManager] 🛰️ Radar Tactique prêt et connecté.");
        }
    }

    void Update()
    {
#if UNITY_SERVER
        return; // Aucune entrée tactile/souris à traiter côté serveur headless.
#else
        RefreshTacticalUI();
#endif
        // Pulsation "battement de cœur" des lignes de trajectoire : ne touche qu'un multiplicateur
        // de largeur (aucun recalcul de chemin/positions), donc s'exécute chaque frame — y compris
        // pendant l'Exécution où le tracé restant reste affiché — sans réintroduire le coût que
        // isPathDirty a justement été ajouté pour éliminer.
        AnimateTacticalLinesHeartbeat();

        // Partie terminée (voir CheckSoloGameOver) : plus aucun ordre, tracé, ni tap ne doit être
        // traité — vérifié explicitement ici plutôt que de compter uniquement sur le recouvrement
        // visuel de l'écran GameOver.
        if (IsSoloGameOver) return;

        // Bloquer l'assignation de nouveaux ordres pendant l'exécution ou pendant le placement d'unités
        if (phaseActuelle == GamePhase.Execution || UnitSpawnerUI.IsPlacingUnit) return;

        // Tracé fluide et ultra-léger (recalculé uniquement si dirty ou en phase dynamique)
        if (isPathsDirty || phaseActuelle == GamePhase.CreationPath)
        {
            DessinerTousLesChemins();
        }

        // 1. Si le panneau d'action (porte/fenêtre/bâtiment) est ouvert, on attend l'interaction du joueur
        if (menuPanel != null && menuPanel.activeSelf) return;

        HandlePointerInput();
    }
}
