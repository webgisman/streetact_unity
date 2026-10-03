using UnityEngine;

/// <summary>
/// Point d'entrée du gameplay tactique tour-par-tour : cycle de phases (Planification/Exécution)
/// et état de sélection partagés par tous les fichiers partiels de cette classe —
///   - Input/TacticalPathManager_Input.cs : orchestration du tap/clic (lecture pointeur, détection
///       tap, sélection d'unité) — voir Assets/Scripts/AI/Input/ pour le détail des briques
///       (PointerFrameReader, GestureScale, TapGestureDetector, UnitSelectionResolver)
///   - Input/TacticalPathManager_TapActionRouter.cs : cascade porte/fenêtre/bâtiment/sol/barricade
///       une fois qu'aucune unité n'est visée par le tap
///   - TacticalPathManager_Selection.cs : sélection d'unité (clic direct, bouton BLESSÉS, portraits)
///   - TacticalPathManager_ContextMenu.cs : ouverture/fermeture des menus d'ordre, confirmation
///   - TacticalPathManager_UI.cs : binding UI Toolkit (barre du bas, squad-bar, ContextMenu)
///   - TacticalPathManager_PathDrawing.cs : tracé des trajectoires (LineRenderer par unité)
///   - TacticalPathManager_Execution.cs : déroulement du tour (Fin de tour → Planification)
/// </summary>
public partial class TacticalPathManager : MonoBehaviour
{
    public enum GamePhase { Planification, CreationPath, Execution }
    // Valeurs figées : elles transitent en entier dans le protocole réseau (NetMessage.PathNode.action).
    // "1" est libre (ancienne action Attendre5Min supprimée). Descendre = 12 est une action dédiée : la
    // descente d'un toit ne se devine pas d'après la géométrie.
    public enum NodeAction { Continuer = 0, Guetter = 2, Embuscade = 3, Escalade = 4, GarnisonFenetre = 5, EntrerBatiment = 6, SortirBatiment = 7, GuetterPorte = 8, Attendre30s = 9, SeCacher = 10, TirMortier = 11, Descendre = 12 }

    [Header("Système")]
    public GamePhase phaseActuelle = GamePhase.Planification;

    [System.Serializable]
    public struct TacticalNode
    {
        public Vector3 position;
        public NodeAction action;
    }

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

    private LineRenderer lineRenderer;
    private static bool isPathsDirty = true;

    // Dernière valeur de positionClicTemporaire pour laquelle un tracé a été produit — sert à
    // détecter un changement de cible d'aperçu (voir Update).
    private Vector3 lastPreviewTargetDrawn = Vector3.positiveInfinity;

    // Un redessin est demandé pour la seule unité sélectionnée (changement de cible d'aperçu).
    // Distinct du drapeau statique isPathsDirty, qui invalide le tracé de TOUTES les unités.
    private bool previewRedrawRequested = false;

    /// <summary>Y a-t-il une cible de tap en attente ? Jamais `!= Vector3.positiveInfinity` : avec des
    /// composantes infinies, l'égalité de Unity (inf - inf = NaN) est toujours fausse, donc ce test
    /// serait toujours vrai. Voir Novgov.Core.VectorSentinel (testé).</summary>
    private static bool HasTapTarget(Vector3 p)
    {
        return Novgov.Core.VectorSentinel.IsSet(p);
    }

    /// <summary>Deux cibles d'aperçu désignent-elles le même point ? Traite correctement le cas
    /// "les deux sont la sentinelle" (voir <see cref="HasTapTarget"/>), que l'opérateur == de
    /// Vector3 rapporte à tort comme différent.</summary>
    private static bool SameTapTarget(Vector3 a, Vector3 b)
    {
        return Novgov.Core.VectorSentinel.Same(a, b);
    }

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
        // Menu pause ouvert (voir Novgov.UI.InGameMenuController) : même principe, aucun tap ne doit
        // atteindre la carte derrière lui.
        if (Novgov.UI.InGameMenuController.IsOpen) return;

        // Bloquer l'assignation de nouveaux ordres pendant l'exécution, pendant le placement d'unités,
        // ou tant que le placement initial des troupes est en cours (Solo ou en ligne, voir
        // UnitSpawnerUI/UnitAI_Movement) — avant ce garde-fou, une unité déjà posée restait
        // sélectionnable/planifiable via ce même menu tactique alors que le tour/la partie n'a pas
        // encore commencé (rapport utilisateur : "il faut pas laisser le joueur choisir des
        // trajectoire ... le jeu n'a pas encore commencé"). Même flag que UnitAI_Movement.cs utilise
        // déjà pour geler les NavMeshAgent pendant cette même fenêtre.
        if (phaseActuelle == GamePhase.Execution || UnitSpawnerUI.IsPlacingUnit || UnitSpawnerUI.IsSoloDeploymentPhase
            || Novgov.Network.MultiplayerMatchController.IsDeploymentPhaseActive) return;

        // Aperçu de destination : redessiner dès que le point tapé change (tous les taps et fermetures de
        // menu passent par positionClicTemporaire). Comparaison via SameTapTarget, jamais != sur Vector3
        // (voir HasTapTarget). Seul le tracé de l'unité sélectionnée est invalidé : isPathsDirty
        // recalculerait les chemins de toutes les unités à chaque tap.
        if (!SameTapTarget(positionClicTemporaire, lastPreviewTargetDrawn))
        {
            lastPreviewTargetDrawn = positionClicTemporaire;
            if (uniteSelectionnee != null)
            {
                UnitAI selectedForPreview = uniteSelectionnee.GetComponent<UnitAI>();
                if (selectedForPreview != null) selectedForPreview.isPathDirty = true;
            }
            previewRedrawRequested = true;
        }

        // Tracé fluide et ultra-léger (recalculé uniquement si dirty ou en phase dynamique)
        if (isPathsDirty || previewRedrawRequested || phaseActuelle == GamePhase.CreationPath)
        {
            previewRedrawRequested = false;
            DessinerTousLesChemins();
        }

        HandlePointerInput();
    }
}
